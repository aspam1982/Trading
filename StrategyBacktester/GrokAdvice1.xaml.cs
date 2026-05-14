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
/// Backtest и автоподбор параметров стратегии пересечения скользящих средних.
///
/// Стратегия открывает long, когда быстрая MA пересекает медленную снизу вверх,
/// и short, когда быстрая MA пересекает медленную сверху вниз. Сделки защищаются
/// ATR stop loss и ATR take profit. Опционально сигнал может фильтроваться по
/// волатильности: вход разрешается только если текущий ATR выше средней ATR за loopback.
///
/// Выбранный пользователем период используется как интервал загрузки свечей:
/// первая половина периода служит для оптимизации параметров, вторая половина -
/// для контрольного прогона и построения графиков.
/// </summary>
public partial class GrokAdvice1 : Window, INotifyPropertyChanged
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

    public GrokAdvice1()
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
        await LoadInstrumentListAsync("MGNT");
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
        StatusText.Text = $"Автоподбор MA/ATR-стратегии для {share.Ticker}: opt {intervalLen} мес., delay {intervalDelay} мес., check {intervalCheck} мес...";

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
            MessageBox.Show(ex.ToString(), "GrokAdvice1", MessageBoxButton.OK, MessageBoxImage.Error);
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

        AdjustForDividends(share, from, to, shareCandles);

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

        var bestParams = TradingRobotOptimizer.Optimize(parameterDefinitions, evaluator, 100, 200, 0.5f, 0.8f, true);

        dryRun = false;
        evalFrom = testFrom;
        evalTo = testTo;
        var fitness = evaluator(bestParams.Parameters);

        // Для графиков показываем весь walk-forward участок: оптимизация + проверка.
        // Итоговая прибыльность выше при этом остается посчитанной только на проверке.
        var chartOutput = new EvaluationOutput();
        Evaluate(bestParams.Parameters, shareData, optimizationFrom, testTo, false, chartOutput);

        return new BacktestResult(share, from, to, optimizationFrom, optimizationTo, testFrom, testTo, shareCandles, chartOutput.Depo, chartOutput.Xs, chartOutput.Equity,
            chartOutput.FastMa, chartOutput.SlowMa, fitness, ts);
    }

    private void AdjustForDividends(InstrumentOption share, DateTime from, DateTime to, List<HistoricalCandle> candles)
    {
        var dividends = _client.Instruments.GetDividends(new GetDividendsRequest
        {
            InstrumentId = share.Uid,
            From = from.ToUniversalTime().ToTimestamp(),
            To = to.ToUniversalTime().ToTimestamp()
        }).Dividends;

        foreach (var c in candles)
        {
            var addon = dividends
                .Where(x => c.Time < x.LastBuyDate.ToDateTime().AddDays(1))
                .Sum(x => Helper.FromQuotation(x.YieldValue) / 100d);

            c.Low /= 1 + addon;
            c.High /= 1 + addon;
            c.Open /= 1 + addon;
            c.Close /= 1 + addon;
        }
    }

    private static List<TradingRobotOptimizer.ParameterDefinition> CreateParameterDefinitions()
    {
        return new()
        {
            new() { Name = "lengthFast", Type = TradingRobotOptimizer.ParameterType.Integer, MinValue = 1, MaxValue = 30 },
            new() { Name = "lengthSlow", Type = TradingRobotOptimizer.ParameterType.Integer, MinValue = 1, MaxValue = 200 },
            new() { Name = "atrLength", Type = TradingRobotOptimizer.ParameterType.Integer, MinValue = 2, MaxValue = 100 },
            new() { Name = "atrMultiplierSL", Type = TradingRobotOptimizer.ParameterType.Double, MinValue = 1d, MaxValue = 10d },
            new() { Name = "atrMultiplierTP", Type = TradingRobotOptimizer.ParameterType.Double, MinValue = 1d, MaxValue = 100d },
            new() { Name = "loopback", Type = TradingRobotOptimizer.ParameterType.Integer, MinValue = 1, MaxValue = 50 },
            new() { Name = "riskPercent", Type = TradingRobotOptimizer.ParameterType.Double, MinValue = 1d, MaxValue = 100d },
            new() { Name = "useBuyVolatileFilter", Type = TradingRobotOptimizer.ParameterType.Boolean },
            new() { Name = "useSellVolatileFilter", Type = TradingRobotOptimizer.ParameterType.Boolean },
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
        uint lengthFast = Convert.ToUInt32(parameters["lengthFast"]);
        uint lengthSlow = Convert.ToUInt32(parameters["lengthSlow"]);
        uint atrLength = Convert.ToUInt32(parameters["atrLength"]);
        double atrMultiplierSL = (double)parameters["atrMultiplierSL"];
        double atrMultiplierTP = (double)parameters["atrMultiplierTP"];
        uint loopback = Convert.ToUInt32(parameters["loopback"]);
        double riskPercent = (double)parameters["riskPercent"];
        bool useBuyVolatileFilter = (bool)parameters["useBuyVolatileFilter"];
        bool useSellVolatileFilter = (bool)parameters["useSellVolatileFilter"];

        double maxDepo = 0;
        double maxDrawDown = 0;
        double startDepo = 1_000_000;
        var depo = new Depo(startDepo);
        var xs = new List<DateTime>();
        var equity = new List<double>();
        var fastMa = new List<double>();
        var slowMa = new List<double>();
        var atrQueue = new Queue<double>();
        double prevFastMa = 0;
        double prevSlowMa = 0;
        var candles = shareData.Candles.Where(x => x.Time > from && x.Time < to).ToList();
        if (candles.Count == 0)
            return (double.MinValue, 1, 999);

        foreach (var c in candles)
        {
            var currentFastMa = shareData.GetMA(c, lengthFast);
            var currentSlowMa = shareData.GetMA(c, lengthSlow);
            var atr = shareData.GetATR(c, atrLength);
            atrQueue.Enqueue(atr);
            if (atrQueue.Count > loopback)
                atrQueue.Dequeue();

            var atrSma = atrQueue.Average();
            bool isVolatile = atr > atrSma;
            bool buySignal = prevFastMa <= prevSlowMa && currentFastMa > currentSlowMa && (isVolatile || !useBuyVolatileFilter);
            bool sellSignal = prevFastMa >= prevSlowMa && currentFastMa < currentSlowMa && (isVolatile || !useSellVolatileFilter);
            prevFastMa = currentFastMa;
            prevSlowMa = currentSlowMa;

            double stopLossDistance = atr * atrMultiplierSL;
            double takeProfitDistance = atr * atrMultiplierTP;

            foreach (var deal in depo.OpenedDeals.ToList())
                if (deal.MustCloseAtCandle(c))
                    deal.CloseDeal(c, depo);

            if (atrQueue.Count < loopback || atr == 0)
            {
                buySignal = false;
                sellSignal = false;
            }

            if (buySignal)
            {
                foreach (var deal in depo.OpenedDeals.Where(x => !x.IsLong).ToList())
                    deal.CloseDeal(c, depo);
                if (!depo.OpenedDeals.Any(x => x.IsLong))
                    new Deal(c, true, takeProfitDistance, stopLossDistance, depo, riskPercent);
            }

            if (sellSignal)
            {
                foreach (var deal in depo.OpenedDeals.Where(x => x.IsLong).ToList())
                    deal.CloseDeal(c, depo);
                if (!depo.OpenedDeals.Any(x => !x.IsLong))
                    new Deal(c, false, takeProfitDistance, stopLossDistance, depo, riskPercent);
            }

            var currentDepo = depo.GetCurrentMoney(c.Close);
            if (currentDepo > maxDepo)
                maxDepo = currentDepo;
            if (maxDepo > 0 && (maxDepo - currentDepo) / maxDepo > maxDrawDown)
                maxDrawDown = (maxDepo - currentDepo) / maxDepo;

            if (!dryRun)
            {
                xs.Add(c.Time);
                equity.Add(currentDepo);
                fastMa.Add(currentFastMa);
                slowMa.Add(currentSlowMa);
            }
        }

        depo.OpenedDeals.ToList().ForEach(x => x.CloseDeal(candles.Last(), depo));
        var profit = (depo.GetCurrentMoney(candles.Last().Close) - startDepo) / startDepo;

        if (!dryRun)
        {
            output.Depo = depo;
            output.Xs = xs;
            output.Equity = equity;
            output.FastMa = fastMa;
            output.SlowMa = slowMa;
        }

        return (profit / Math.Max(1, (to - from).TotalDays) * 30d, maxDrawDown,
            depo.ClosedDeals.Count / Math.Max(1, (to - from).TotalDays));
    }

    private void Render(BacktestResult result)
    {
        foreach (var oldPlot in plot.Multiplot.GetPlots())
            oldPlot.Clear();

        plot.Plot.Clear();
        plot.Multiplot.Reset();
        plot.Multiplot.AddPlots(2);

        var mainPlot = plot.Multiplot.GetPlot(0);
        mainPlot.Title($"{GetType().Name} [{result.Share.Ticker}] {result.Share.Name}\n" +
                       $"Сделок: {result.Depo.ClosedDeals.Count}. Ликвидный портфель: {result.Depo.LiquidPortfolio:C}\n" +
                       $"Данные: {result.From:g} - {result.To:g}\n" +
                       $"Оптимизация: {result.OptimizationFrom:g} - {result.OptimizationTo:g}\n" +
                       $"Итоговая прибыльность: {result.TestFrom:g} - {result.TestTo:g}. " +
                       $"Прибыль: {result.Fitness.Profit:P}/мес. Просадка: {result.Fitness.Drawdown:P}. Сделок: {result.Fitness.DealsFrequency:F2}/день");

        mainPlot.Add.Candlestick(result.Candles.Select(x => new OHLC(x.Open, x.High, x.Low, x.Close, x.Time, result.CandleSpan)).ToList());
        var slow = mainPlot.Add.Scatter(result.Xs, result.SlowMa);
        slow.MarkerShape = MarkerShape.None;
        slow.LineColor = ScottPlot.Colors.Orange;
        var fast = mainPlot.Add.Scatter(result.Xs, result.FastMa);
        fast.MarkerShape = MarkerShape.None;
        fast.LineColor = ScottPlot.Colors.Blue;

        var equityPlot = plot.Multiplot.GetPlot(1);
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

        public Deal(HistoricalCandle start, bool isLong, double takeProfit, double stopLoss, Depo depo, double maxRiskPercent = double.NaN)
        {
            Start = start;
            var availableMoney = Math.Max(0, (depo.LiquidPortfolio - depo.PortfolioMargin) / (depo.MarginPercent / 100d));
            Quontity = Convert.ToInt64(Math.Floor(availableMoney / Start.Close) * (isLong ? 1d : -1d));
            if (!double.IsNaN(maxRiskPercent) && Quontity > 0)
            {
                double riskMoney = depo.LiquidPortfolio * maxRiskPercent / 100d;
                long maxRiskQuontity = Convert.ToInt32(Math.Floor(riskMoney / stopLoss));
                Quontity = Math.Min(Quontity, maxRiskQuontity);
            }
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
        public List<double> FastMa { get; set; } = new();
        public List<double> SlowMa { get; set; } = new();
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
        List<double> FastMa,
        List<double> SlowMa,
        (double Profit, double Drawdown, double DealsFrequency) Fitness,
        TimeSpan CandleSpan);

    private class MainViewModelWriter : TextWriter
    {
        private readonly GrokAdvice1 vm;

        public MainViewModelWriter(GrokAdvice1 vm)
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
