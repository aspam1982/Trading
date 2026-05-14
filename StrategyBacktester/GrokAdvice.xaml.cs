using CommonClasses;
using Google.Protobuf.WellKnownTypes;
using ScottPlot;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Tinkoff.InvestApi;
using Tinkoff.InvestApi.V1;

namespace StrategyBacktester;

/// <summary>
/// Backtest и автоподбор параметров RSI mean-reversion стратегии.
///
/// Стратегия ищет выход RSI из зон перепроданности/перекупленности. Сигнал long
/// появляется, когда RSI возвращается выше нижней границы, сигнал short - когда RSI
/// возвращается ниже верхней границы. Дополнительный MA-фильтр может разрешать long
/// только выше скользящей средней, а short - ниже нее.
///
/// Выбранный пользователем период используется как интервал загрузки свечей:
/// первая половина периода служит для оптимизации параметров, вторая половина -
/// для контрольного прогона и построения графиков.
/// </summary>
public partial class GrokAdvice : Window, INotifyPropertyChanged
{
    private readonly InvestApiClient _client;
    private List<InstrumentOption> _shares = new();
    private string consoleText = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    public string ConsoleText
    {
        get => consoleText;
        set
        {
            consoleText = value;
            NotifyPropertyChanged();
        }
    }

    public GrokAdvice()
    {
        InitializeComponent();
        Console.SetOut(new MainViewModelWriter(this));
        DataContext = this;

        _client = InvestApiClientFactory.Create(
            WindowsCredentialManager.ReadSecret(StrategyBacktester.Properties.Settings.Default.ApiKey) ?? "key not found");

        FromDatePicker.SelectedDate = DateTime.Now.Date.AddYears(-2);
        ToDatePicker.SelectedDate = DateTime.Now.Date;
        StatusText.Text = "Загрузка инструментов...";
    }

    public void NotifyPropertyChanged([CallerMemberName] string propertyName = "")
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadInstrumentListAsync("SBER");
        StatusText.Text = "Выберите тикер и период, затем нажмите \"Применить\".";
    }

    private async void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        await ApplyAsync();
    }

    private async Task LoadInstrumentListAsync(string defaultTicker)
    {
        ApplyButton.IsEnabled = false;
        try
        {
            _shares = await Task.Run(() => _client.Instruments.Shares().Instruments
                .Where(x => x.ApiTradeAvailableFlag)
                .OrderBy(x => x.Ticker)
                .Select(x => new InstrumentOption(x.Ticker, x.Figi, x.Uid, x.Name))
                .ToList());

            ShareComboBox.ItemsSource = _shares;
            ShareComboBox.SelectedItem = _shares.FirstOrDefault(x => x.Ticker.Equals(defaultTicker, StringComparison.OrdinalIgnoreCase))
                                         ?? _shares.FirstOrDefault();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Ошибка загрузки инструментов: " + ex.Message;
        }
        finally
        {
            ApplyButton.IsEnabled = true;
        }
    }

    private async Task ApplyAsync()
    {
        if (ShareComboBox.SelectedItem is not InstrumentOption share)
        {
            StatusText.Text = "Выберите акцию.";
            return;
        }

        var from = FromDatePicker.SelectedDate ?? DateTime.Now.Date.AddYears(-2);
        var to = (ToDatePicker.SelectedDate ?? DateTime.Now.Date).Date.AddDays(1).AddTicks(-1);
        if (from >= to)
        {
            StatusText.Text = "Дата начала должна быть меньше даты окончания.";
            return;
        }

        if (!TryReadIntervals(out var intervalLen, out var intervalDelay, out var intervalCheck))
            return;

        ApplyButton.IsEnabled = false;
        ConsoleText = "";
        StatusText.Text = $"Автоподбор RSI-стратегии для {share.Ticker}: opt {intervalLen} мес., delay {intervalDelay} мес., check {intervalCheck} мес...";

        try
        {
            var result = await Task.Run(() => RunBacktest(share, from, to, intervalLen, intervalDelay, intervalCheck));
            Render(result);
            StatusText.Text =
                $"Готово: {share.Ticker}, сделок {result.Depo.ClosedDeals.Count}, " +
                $"доходность {result.Fitness.Profit:P}/мес., просадка {result.Fitness.Drawdown:P}.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Ошибка: " + ex.Message;
            MessageBox.Show(ex.ToString(), "GrokAdvice", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ApplyButton.IsEnabled = true;
        }
    }

    private bool TryReadIntervals(out int intervalLen, out int intervalDelay, out int intervalCheck)
    {
        intervalLen = intervalDelay = intervalCheck = 0;
        if (!int.TryParse(IntervalLenTextBox.Text, out intervalLen) ||
            !int.TryParse(IntervalDelayTextBox.Text, out intervalDelay) ||
            !int.TryParse(IntervalCheckTextBox.Text, out intervalCheck) ||
            intervalLen <= 0 ||
            intervalDelay < 0 ||
            intervalCheck <= 0)
        {
            StatusText.Text = "Интервалы должны быть целыми месяцами: Opt > 0, Delay >= 0, Check > 0.";
            return false;
        }

        return true;
    }

    private BacktestResult RunBacktest(InstrumentOption share, DateTime from, DateTime to, int intervalLen, int intervalDelay, int intervalCheck)
    {
        HistoricalTimeFrame tf = HistoricalTimeFrame.H1;
        CandleInterval ci = CandleInterval.Hour;
        TimeSpan ts = new(1, 0, 0);

        var shareData = HistoricalData.ReadHistoricalData(share.Ticker, share.Figi, tf, false,
            new HistoricalData.QueryDataDelegate((figi, frame, from2, to2) =>
            {
                var candles = _client.MarketData.GetCandles(new GetCandlesRequest
                {
                    Figi = figi,
                    Interval = ci,
                    From = from2.ToUniversalTime().ToTimestamp(),
                    To = to2.ToUniversalTime().ToTimestamp()
                });

                return candles.Candles.Where(x => x.IsComplete).Select(x => x.ToHistoricalCandle()).ToList();
            }));

        var shareCandles = shareData.GetData(from, to).ToList();
        if (shareData.DataHasChanges)
            shareData.SaveHistoricalData();
        if (shareCandles.Count < 300)
            throw new InvalidOperationException("Недостаточно часовых свечей для автоподбора. Увеличьте период.");

        var optimizationTo = to.AddMonths(-intervalDelay);
        var optimizationFrom = optimizationTo.AddMonths(-intervalLen);
        var testFrom = optimizationTo;
        var testTo = testFrom.AddMonths(intervalCheck);
        if (optimizationFrom < from || testTo > to)
            throw new InvalidOperationException(
                $"Интервалы opt/delay/check выходят за загруженный диапазон {from:g} - {to:g}. " +
                $"Оптимизация: {optimizationFrom:g} - {optimizationTo:g}, проверка: {testFrom:g} - {testTo:g}.");

        var parameterDefinitions = CreateParameterDefinitions();
        var output = new EvaluationOutput();
        var dryRun = true;
        DateTime evalFrom = optimizationFrom;
        DateTime evalTo = optimizationTo;

        var evaluator = new TradingRobotOptimizer.FitnessFunction(parameters =>
            Evaluate(parameters, shareData, evalFrom, evalTo, dryRun, output));

        var bestParams = TradingRobotOptimizer.Optimize(parameterDefinitions, evaluator, 20, 100, 0.5f, 0.8f, true);

        dryRun = false;
        evalFrom = testFrom;
        evalTo = testTo;
        var fitness = evaluator(bestParams.Parameters);

        // Для графиков показываем весь walk-forward участок: оптимизация + проверка.
        // Итоговая прибыльность выше при этом остается посчитанной только на проверке.
        var chartOutput = new EvaluationOutput();
        Evaluate(bestParams.Parameters, shareData, optimizationFrom, testTo, false, chartOutput);

        return new BacktestResult(share, from, to, optimizationFrom, optimizationTo, testFrom, testTo, shareCandles, chartOutput.Depo, chartOutput.Xs, chartOutput.Equity,
            chartOutput.Ma, chartOutput.Rsi, chartOutput.RsiOversold, chartOutput.RsiOverbought, fitness, ts);
    }

    private static List<TradingRobotOptimizer.ParameterDefinition> CreateParameterDefinitions()
    {
        return new()
        {
            new() { Name = "rsiLength", Type = TradingRobotOptimizer.ParameterType.Integer, MinValue = 1, MaxValue = 60 },
            new() { Name = "rsiCandlesToCheck", Type = TradingRobotOptimizer.ParameterType.Integer, MinValue = 5, MaxValue = 400 },
            new() { Name = "rsiOverbought", Type = TradingRobotOptimizer.ParameterType.Double, MinValue = 51d, MaxValue = 95d },
            new() { Name = "maLenght", Type = TradingRobotOptimizer.ParameterType.Integer, MinValue = 10, MaxValue = 200 },
            new() { Name = "useTrendFilter", Type = TradingRobotOptimizer.ParameterType.Boolean },
        };
    }

    private static (double Profit, double Drawdown, double DealsFrequency) Evaluate(
        Dictionary<string, object> parameters,
        HistoricalData shareData,
        DateTime from,
        DateTime to,
        bool dryRun,
        EvaluationOutput output)
    {
        uint rsiLength = Convert.ToUInt32(parameters["rsiLength"]);
        uint rsiCandlesToCheck = Convert.ToUInt32(parameters["rsiCandlesToCheck"]);
        double rsiOverbought = (double)parameters["rsiOverbought"];
        double rsiOversold = 100 - rsiOverbought;
        uint maLenght = Convert.ToUInt32(parameters["maLenght"]);
        bool useTrendFilter = (bool)parameters["useTrendFilter"];

        double maxDepo = 0;
        double maxDrawDown = 0;
        double startDepo = 1_000_000;
        var depo = new Depo(startDepo);
        var xs = new List<DateTime>();
        var equity = new List<double>();
        var maValues = new List<double>();
        var rsiValues = new List<double>();
        var candles = shareData.Candles.Where(x => x.Time > from && x.Time < to).ToList();
        if (candles.Count == 0)
            return (double.MinValue, 1, 999);

        foreach (var c in candles)
        {
            var ma = shareData.GetMA(c, maLenght);
            var rsi = shareData.GetRSI(c, rsiLength);
            bool upTrend = c.Close > ma;
            bool downTrend = c.Close < ma;
            bool rsiLong = false;
            bool rsiShort = false;
            int idx = shareData.Candles.IndexOf(c);

            for (int i = 0; i <= rsiCandlesToCheck; i++)
            {
                if (idx - i - 1 < 0)
                    break;

                rsiLong |= shareData.GetRSI(shareData.Candles[idx - i - 1], rsiLength) < rsiOversold
                           && shareData.GetRSI(shareData.Candles[idx - i], rsiLength) > rsiOversold;
                rsiShort |= shareData.GetRSI(shareData.Candles[idx - i - 1], rsiLength) > rsiOverbought
                            && shareData.GetRSI(shareData.Candles[idx - i], rsiLength) < rsiOverbought;
            }

            bool entryLong = useTrendFilter ? rsiLong && upTrend : rsiLong;
            bool entryShort = useTrendFilter ? rsiShort && downTrend : rsiShort;

            foreach (var deal in depo.OpenedDeals.ToList())
                if (deal.MustCloseAtCandle(c))
                    deal.CloseDeal(c, depo);

            if (entryLong)
            {
                foreach (var deal in depo.OpenedDeals.Where(x => !x.IsLong).ToList())
                    deal.CloseDeal(c, depo);
                if (!depo.OpenedDeals.Any(x => x.IsLong))
                    new Deal(c, true, double.NaN, double.NaN, depo);
            }

            if (entryShort)
            {
                foreach (var deal in depo.OpenedDeals.Where(x => x.IsLong).ToList())
                    deal.CloseDeal(c, depo);
                if (!depo.OpenedDeals.Any(x => !x.IsLong))
                    new Deal(c, false, double.NaN, double.NaN, depo);
            }

            var currDepo = depo.GetCurrentMoney(c.Close);
            if (currDepo > maxDepo)
                maxDepo = currDepo;
            if (maxDepo > 0 && (maxDepo - currDepo) / maxDepo > maxDrawDown)
                maxDrawDown = (maxDepo - currDepo) / maxDepo;

            if (!dryRun)
            {
                xs.Add(c.Time);
                equity.Add(currDepo);
                maValues.Add(ma);
                rsiValues.Add(rsi);
            }
        }

        depo.OpenedDeals.ToList().ForEach(x => x.CloseDeal(candles.Last(), depo));
        var profit = (depo.GetCurrentMoney(candles.Last().Close) - startDepo) / startDepo;

        if (!dryRun)
        {
            output.Depo = depo;
            output.Xs = xs;
            output.Equity = equity;
            output.Ma = maValues;
            output.Rsi = rsiValues;
            output.RsiOverbought = rsiOverbought;
            output.RsiOversold = rsiOversold;
        }

        return (profit / Math.Max(1, (to - from).TotalDays) * 30d, maxDrawDown,
            depo.ClosedDeals.Count / Math.Max(1, (to - from).TotalDays));
    }

    private void Render(BacktestResult result)
    {
        plot.Multiplot.Reset();
        plot.Multiplot.AddPlots(3);

        var mainPlot = plot.Multiplot.GetPlot(0);
        mainPlot.Title($"{GetType().Name} [{result.Share.Ticker}] {result.Share.Name}\n" +
                       $"Сделок: {result.Depo.ClosedDeals.Count}. Ликвидный портфель: {result.Depo.LiquidPortfolio:C}\n" +
                       $"Данные: {result.From:g} - {result.To:g}\n" +
                       $"Оптимизация: {result.OptimizationFrom:g} - {result.OptimizationTo:g}\n" +
                       $"Итоговая прибыльность: {result.TestFrom:g} - {result.TestTo:g}. " +
                       $"Прибыль: {result.Fitness.Profit:P}/мес. Просадка: {result.Fitness.Drawdown:P}. Сделок: {result.Fitness.DealsFrequency:F2}/день");

        mainPlot.Add.Candlestick(result.Candles.Select(x => new OHLC(x.Open, x.High, x.Low, x.Close, x.Time, result.CandleSpan)).ToList());
        var ma = mainPlot.Add.Scatter(result.Xs, result.Ma);
        ma.MarkerShape = MarkerShape.None;
        ma.LineColor = ScottPlot.Colors.Orange;

        var rsiPlot = plot.Multiplot.GetPlot(1);
        rsiPlot.Title("RSI и границы входа");
        var band = rsiPlot.Add.VerticalSpan(result.RsiOversold, result.RsiOverbought);
        band.FillColor = ScottPlot.Colors.Red.WithAlpha(50);
        var rsi = rsiPlot.Add.Scatter(result.Xs, result.Rsi);
        rsi.Color = ScottPlot.Colors.Black;
        rsi.MarkerShape = MarkerShape.None;

        var equityPlot = plot.Multiplot.GetPlot(2);
        equityPlot.Title($"Кривая капитала: {result.OptimizationFrom:g} - {result.TestTo:g}");
        AddWalkForwardBands(equityPlot, result.OptimizationFrom, result.OptimizationTo, result.TestFrom, result.TestTo);
        var equity = equityPlot.Add.Scatter(result.Xs, result.Equity);
        equity.MarkerShape = MarkerShape.None;
        equity.LineWidth = 2;

        foreach (var deal in result.Depo.ClosedDeals)
            mainPlot.Add.Line(
                new Coordinates(deal.Start.Time.ToOADate(), deal.Start.Close),
                new Coordinates(deal.End!.Time.ToOADate(), deal.End.Close)).Color = deal.IsLong ? ScottPlot.Colors.Green : ScottPlot.Colors.Red;

        PixelPadding padding = new(left: 100, right: 10, bottom: 50, top: 50);
        foreach (var subPlot in plot.Multiplot.GetPlots())
        {
            subPlot.Layout.Fixed(padding);
            subPlot.Axes.DateTimeTicksBottom();
        }

        mainPlot.Layout.Fixed(new PixelPadding(left: 100, right: 10, bottom: 50, top: 100));
        plot.Multiplot.SharedAxes.ShareX(plot.Multiplot.GetPlots());
        plot.Refresh();
    }

    private static void AddWalkForwardBands(Plot targetPlot, DateTime optimizationFrom, DateTime optimizationTo, DateTime testFrom, DateTime testTo)
    {
        var optimizationBand = targetPlot.Add.HorizontalSpan(optimizationFrom.ToOADate(), optimizationTo.ToOADate());
        optimizationBand.FillColor = ScottPlot.Colors.Green.WithAlpha(40);

        var testBand = targetPlot.Add.HorizontalSpan(testFrom.ToOADate(), testTo.ToOADate());
        testBand.FillColor = ScottPlot.Colors.Red.WithAlpha(40);
    }

    public class Deal
    {
        public HistoricalCandle Start { get; set; }
        public HistoricalCandle? End { get; set; }
        public long Quontity { get; set; }
        public double StopLoss { get; set; }
        public double TakeProfit { get; set; }

        public Deal(HistoricalCandle start, bool isLong, double takeProfit, double stopLoss, Depo depo)
        {
            Start = start;
            var availableMoney = Math.Max(0, (depo.LiquidPortfolio - depo.PortfolioMargin) / (depo.MarginPercent / 100d));
            Quontity = Convert.ToInt64(Math.Floor(availableMoney / Start.Close) * (isLong ? 1d : -1d));
            if (Quontity == 0)
                return;

            var money = Quontity * Start.Close;
            TakeProfit = Start.Close + takeProfit * (isLong ? 1d : -1d);
            StopLoss = Start.Close - stopLoss * (isLong ? 1d : -1d);
            depo.LiquidPortfolio -= money + Math.Abs(money * depo.CommissionPercent / 100d);
            depo.PortfolioMargin += Math.Abs(money * depo.MarginPercent / 100d);
            depo.OpenedDeals.Add(this);
        }

        public bool IsLong => Quontity > 0;

        public bool MustCloseAtCandle(HistoricalCandle candle)
        {
            var stopLoss = !double.IsNaN(StopLoss) && (IsLong && candle.Low < StopLoss || !IsLong && candle.High > StopLoss);
            var takeProfit = !double.IsNaN(TakeProfit) && (IsLong && candle.High > TakeProfit || !IsLong && candle.Low < TakeProfit);
            return stopLoss || takeProfit;
        }

        public void CloseDeal(HistoricalCandle end, Depo depo)
        {
            End = end;
            var money = Quontity * End.Close;
            depo.LiquidPortfolio += money - Math.Abs(money * depo.CommissionPercent / 100d);
            depo.PortfolioMargin -= Math.Abs(Start.Close * Quontity * depo.MarginPercent / 100d);
            depo.OpenedDeals.Remove(this);
            depo.ClosedDeals.Add(this);
        }
    }

    public class Depo
    {
        public double LiquidPortfolio { get; set; }
        public double PortfolioMargin { get; set; }
        public double MarginPercent { get; set; } = 30d;
        public double CommissionPercent { get; set; } = 0.04d;
        public List<Deal> OpenedDeals { get; } = new();
        public List<Deal> ClosedDeals { get; } = new();

        public Depo(double liquidPortfolio)
        {
            LiquidPortfolio = liquidPortfolio;
        }

        public double GetCurrentMoney(double currentPrice)
        {
            return LiquidPortfolio + OpenedDeals.Select(x =>
            {
                var money = x.Quontity * currentPrice;
                return money - Math.Abs(money * CommissionPercent / 100d);
            }).Sum();
        }
    }

    private sealed record InstrumentOption(string Ticker, string Figi, string Uid, string Name)
    {
        public string DisplayName => $"{Ticker} - {Name}";
    }

    private sealed class EvaluationOutput
    {
        public Depo Depo { get; set; } = new(0);
        public List<DateTime> Xs { get; set; } = new();
        public List<double> Equity { get; set; } = new();
        public List<double> Ma { get; set; } = new();
        public List<double> Rsi { get; set; } = new();
        public double RsiOverbought { get; set; }
        public double RsiOversold { get; set; }
    }

    private sealed record BacktestResult(
        InstrumentOption Share,
        DateTime From,
        DateTime To,
        DateTime OptimizationFrom,
        DateTime OptimizationTo,
        DateTime TestFrom,
        DateTime TestTo,
        List<HistoricalCandle> Candles,
        Depo Depo,
        List<DateTime> Xs,
        List<double> Equity,
        List<double> Ma,
        List<double> Rsi,
        double RsiOversold,
        double RsiOverbought,
        (double Profit, double Drawdown, double DealsFrequency) Fitness,
        TimeSpan CandleSpan);

    private class MainViewModelWriter : TextWriter
    {
        private readonly GrokAdvice vm;

        public MainViewModelWriter(GrokAdvice vm)
        {
            this.vm = vm;
        }

        public override void WriteLine(string? value)
        {
            Application.Current.Dispatcher.BeginInvoke(
                () => vm.ConsoleText += value + "\r\n",
                DispatcherPriority.Background);
        }

        public override void Write(string? value)
        {
            Application.Current.Dispatcher.BeginInvoke(
                () => vm.ConsoleText += value,
                DispatcherPriority.Background);
        }

        public override Encoding Encoding => Encoding.Default;
    }

    private void TextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        scroller.ScrollToEnd();
    }
}
