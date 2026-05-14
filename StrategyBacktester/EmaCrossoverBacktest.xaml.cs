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
/// Backtest стратегии пересечения EMA.
///
/// Идея стратегии: входить в long, когда быстрая EMA пересекает медленную снизу вверх,
/// и входить в short, когда быстрая EMA пересекает медленную сверху вниз. Выход происходит
/// по обратному пересечению, по состоянию "флэт" или по ATR-стопу. Размер позиции считается
/// от риска на сделку и дополнительно ограничивается максимальной экспозицией.
/// </summary>
public partial class EmaCrossoverBacktest : Window, INotifyPropertyChanged
{
    private static readonly string Token =
        WindowsCredentialManager.ReadSecret(StrategyBacktester.Properties.Settings.Default.ApiKey) ?? "key not found";

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

    public EmaCrossoverBacktest()
    {
        InitializeComponent();
        Console.SetOut(new MainViewModelWriter(this));
        DataContext = this;

        _client = InvestApiClientFactory.Create(Token);
        FromDatePicker.SelectedDate = DateTime.Now.Date.AddYears(-5);
        ToDatePicker.SelectedDate = DateTime.Now.Date;
        StatusText.Text = "Загрузка инструментов...";
    }

    public void NotifyPropertyChanged([CallerMemberName] string propertyName = "")
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadInstrumentListAsync();
        StatusText.Text = "Выберите тикер и период, затем нажмите \"Применить\".";
    }

    private async void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        await ApplyAsync();
    }

    private async Task LoadInstrumentListAsync()
    {
        ApplyButton.IsEnabled = false;

        try
        {
            _shares = await Task.Run(() => _client.Instruments.Shares().Instruments
                .Where(x => x.ApiTradeAvailableFlag)
                .OrderBy(x => x.Ticker)
                .Select(x => new InstrumentOption(x.Ticker, x.Figi, x.Name))
                .ToList());

            ShareComboBox.ItemsSource = _shares;
            ShareComboBox.SelectedItem = _shares.FirstOrDefault(x => x.Ticker == "GAZP") ?? _shares.FirstOrDefault();
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

        var from = FromDatePicker.SelectedDate ?? DateTime.Now.Date.AddYears(-5);
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
        StatusText.Text = $"Расчет EMA Crossover {share.Ticker}: opt {intervalLen} мес., delay {intervalDelay} мес., check {intervalCheck} мес...";

        try
        {
            var result = await Task.Run(() => RunBacktest(share, from, to, intervalLen, intervalDelay, intervalCheck));
            Render(result);
            StatusText.Text =
                $"Готово: {result.Deals.Count} сделок, " +
                $"прибыль {result.ProfitPerMonth:P2}/мес., просадка {result.Drawdown:P2}, " +
                $"{result.DealsFrequency:F2} сделок/день.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Ошибка: " + ex.Message;
            MessageBox.Show(ex.ToString(), "EMA Crossover Backtest", MessageBoxButton.OK, MessageBoxImage.Error);
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
        var data = HistoricalData.ReadHistoricalData(
            share.Ticker,
            share.Figi,
            HistoricalTimeFrame.H1,
            false,
            new HistoricalData.QueryDataDelegate((figi, frame, queryFrom, queryTo) =>
            {
                var candles = _client.MarketData.GetCandles(new GetCandlesRequest
                {
                    Figi = figi,
                    Interval = CandleInterval.Hour,
                    From = queryFrom.ToUniversalTime().ToTimestamp(),
                    To = queryTo.ToUniversalTime().ToTimestamp()
                });

                return candles.Candles
                    .Where(x => x.IsComplete)
                    .Select(x => x.ToHistoricalCandle())
                    .ToList();
            }));

        var candlesAll = data.GetData(from, to);
        if (data.DataHasChanges)
            data.SaveHistoricalData();

        var optimizationTo = to.AddMonths(-intervalDelay);
        var optimizationFrom = optimizationTo.AddMonths(-intervalLen);
        var testFrom = optimizationTo;
        var testTo = testFrom.AddMonths(intervalCheck);
        if (optimizationFrom < from || testTo > to)
            throw new InvalidOperationException(
                $"Интервалы opt/delay/check выходят за загруженный диапазон {from:g} - {to:g}. " +
                $"Оптимизация: {optimizationFrom:g} - {optimizationTo:g}, итоговая прибыльность: {testFrom:g} - {testTo:g}.");

        var optimizationCandles = candlesAll.Where(x => x.Time > optimizationFrom && x.Time < optimizationTo).ToList();
        var testCandles = candlesAll.Where(x => x.Time > testFrom && x.Time < testTo).ToList();
        if (optimizationCandles.Count < 200 || testCandles.Count < 100)
            throw new InvalidOperationException("Недостаточно часовых свечей для EMA backtest.");

        var parameterDefinitions = CreateParameterDefinitions();
        var output = BacktestOutput.Invalid;
        var dryRun = true;
        var evalCandles = optimizationCandles;
        var evalFrom = optimizationFrom;
        var evalTo = optimizationTo;

        var evaluator = new TradingRobotOptimizer.FitnessFunction(parameters =>
        {
            var result = Evaluate(data, evalCandles, evalFrom, evalTo, parameters, !dryRun);
            if (!dryRun)
                output = result;

            return (result.ProfitPerMonth, result.Drawdown, result.DealsFrequency);
        });

        TradingRobotOptimizer.FitnessCalculator fitnessCalc = (profit, drawdown, dealsFrequency) =>
        {
            const double minTradesPerDay = 0.1;
            if (dealsFrequency < minTradesPerDay)
                return -1e9;

            const double maxDD = 0.35;
            if (drawdown > maxDD)
                return -1e9;

            const double ddPenalty = 4.0;
            const double tradePenalty = 0.05;

            return profit - ddPenalty * drawdown * drawdown - tradePenalty * dealsFrequency;
        };

        var bestParams = TradingRobotOptimizer.Optimize(
            parameterDefinitions,
            evaluator,
            120,
            220,
            0.5f,
            0.8f,
            true,
            fitnessCalc);

        dryRun = false;
        evalCandles = testCandles;
        evalFrom = testFrom;
        evalTo = testTo;
        var finalFitness = evaluator(bestParams.Parameters);

        // Для графиков показываем весь walk-forward участок: оптимизация + проверка.
        // Итоговая прибыльность выше при этом остается посчитанной только на проверке.
        var chartCandles = candlesAll.Where(x => x.Time > optimizationFrom && x.Time < testTo).ToList();
        var chartOutput = Evaluate(data, chartCandles, optimizationFrom, testTo, bestParams.Parameters, true);

        return new BacktestResult(
            share,
            from,
            to,
            optimizationFrom,
            optimizationTo,
            testFrom,
            testTo,
            candlesAll.Where(x => x.Time > from && x.Time < to).ToList(),
            chartOutput.Deals,
            chartOutput.EquityXs,
            chartOutput.EquitySeries,
            chartOutput.EmaFastSeries,
            chartOutput.EmaSlowSeries,
            chartOutput.PositionSeries,
            chartOutput.FlatSeries,
            chartOutput.LastEquity,
            finalFitness.Profit,
            finalFitness.Drawdown,
            finalFitness.DealsFrequency,
            new Dictionary<string, object>(bestParams.Parameters));
    }

    private static List<TradingRobotOptimizer.ParameterDefinition> CreateParameterDefinitions()
    {
        return new List<TradingRobotOptimizer.ParameterDefinition>
        {
            new() { Name = "fastLen", Type = TradingRobotOptimizer.ParameterType.Integer, MinValue = 3, MaxValue = 35 },
            new() { Name = "slowLen", Type = TradingRobotOptimizer.ParameterType.Integer, MinValue = 10, MaxValue = 120 },
            new() { Name = "atrLen", Type = TradingRobotOptimizer.ParameterType.Integer, MinValue = 7, MaxValue = 40 },

            // Флэт: |EMAfast - EMAslow| < flatDiffAtr * ATR.
            new() { Name = "flatDiffAtrX100", Type = TradingRobotOptimizer.ParameterType.Integer, MinValue = 10, MaxValue = 120 },

            // ATR-стоп: SL = slAtr * ATR.
            new() { Name = "slAtrX100", Type = TradingRobotOptimizer.ParameterType.Integer, MinValue = 50, MaxValue = 200 },

            // Риск на сделку: X / 1000.
            new() { Name = "riskPctX1000", Type = TradingRobotOptimizer.ParameterType.Integer, MinValue = 2, MaxValue = 30 },

            // Ограничитель стоп-дистанции в % от цены.
            new() { Name = "maxStopPctX1000", Type = TradingRobotOptimizer.ParameterType.Integer, MinValue = 5, MaxValue = 80 },

            // Ограничитель экспозиции в % от equity.
            new() { Name = "maxNotionalPctX100", Type = TradingRobotOptimizer.ParameterType.Integer, MinValue = 20, MaxValue = 300 },

            // Антидребезг после выхода.
            new() { Name = "cooldownBars", Type = TradingRobotOptimizer.ParameterType.Integer, MinValue = 0, MaxValue = 24 },
        };
    }

    private static BacktestOutput Evaluate(
        HistoricalData data,
        List<HistoricalCandle> candles,
        DateTime from,
        DateTime to,
        Dictionary<string, object> parameters,
        bool collectSeries)
    {
        int fastLen = (int)parameters["fastLen"];
        int slowLen = (int)parameters["slowLen"];
        int atrLen = (int)parameters["atrLen"];

        double flatDiffAtr = ((int)parameters["flatDiffAtrX100"]) / 100.0;
        double slAtr = ((int)parameters["slAtrX100"]) / 100.0;
        double riskPct = ((int)parameters["riskPctX1000"]) / 1000.0;
        double maxStopPct = ((int)parameters["maxStopPctX1000"]) / 1000.0;
        double maxNotionalPct = ((int)parameters["maxNotionalPctX100"]) / 100.0;
        int cooldownBars = (int)parameters["cooldownBars"];

        if (slowLen <= fastLen)
            return BacktestOutput.Invalid;

        var deals = new List<Deal>();
        Deal? position = null;

        double depo = 1_000_000d;
        double startDepo = depo;
        const double commission = 0.0005d;

        double maxEquity = startDepo;
        double maxDrawdown = 0;
        int counter = 0;
        int lastExitBar = -1_000_000;
        HistoricalCandle? previous = null;

        var xs = new List<DateTime>();
        var equitySeries = new List<double>();
        var emaFastSeries = new List<double>();
        var emaSlowSeries = new List<double>();
        var posSeries = new List<double>();
        var flatSeries = new List<double>();

        foreach (var candle in candles)
        {
            if (previous == null)
            {
                previous = candle;
                counter++;
                continue;
            }

            double emaFast = data.GetEMA(candle, (uint)fastLen);
            double emaSlow = data.GetEMA(candle, (uint)slowLen);
            double emaFastPrev = data.GetEMA(previous, (uint)fastLen);
            double emaSlowPrev = data.GetEMA(previous, (uint)slowLen);

            double atr = data.GetATR(candle, (uint)atrLen);
            if (atr <= 0)
                atr = Math.Max(1e-9, candle.High - candle.Low);

            double diffNow = emaFast - emaSlow;
            double diffPrev = emaFastPrev - emaSlowPrev;

            bool bullCross = diffPrev <= 0 && diffNow > 0;
            bool bearCross = diffPrev >= 0 && diffNow < 0;
            bool isFlat = Math.Abs(diffNow) < flatDiffAtr * atr;

            // Сначала проверяем защитный ATR-стоп внутри свечи.
            if (position != null && IsStopHit(position, candle))
            {
                ClosePosition(position, position.StopPrice, candle.Time, "STOP", ref depo, commission, deals);
                position = null;
                lastExitBar = counter;
            }

            // Затем закрываем позицию по обратному пересечению или флэту.
            if (position != null)
            {
                bool exitByReverse = position.DirLong && bearCross || !position.DirLong && bullCross;
                bool exitByFlat = isFlat;

                if (exitByReverse || exitByFlat)
                {
                    ClosePosition(position, candle.Open, candle.Time, exitByReverse ? "REVERSE" : "FLAT", ref depo, commission, deals);
                    position = null;
                    lastExitBar = counter;
                }
            }

            // Вход только на фактическом пересечении EMA, без дополнительных тренд-фильтров.
            if (position == null)
            {
                bool cooldownOk = counter - lastExitBar > cooldownBars;
                if (cooldownOk && !isFlat)
                {
                    int entryDir = bullCross ? 1 : bearCross ? -1 : 0;
                    if (entryDir != 0)
                        position = TryOpenPosition(candle, entryDir > 0, depo, riskPct, slAtr, atr, maxStopPct, maxNotionalPct, commission);
                }
            }

            double currentEquity = GetCurrentEquity(depo, position, candle.Open);
            if (currentEquity > maxEquity)
                maxEquity = currentEquity;

            double dd = (maxEquity - currentEquity) / Math.Max(1e-9, maxEquity);
            if (dd > maxDrawdown)
                maxDrawdown = dd;

            if (collectSeries)
            {
                xs.Add(candle.Time);
                equitySeries.Add(currentEquity);
                emaFastSeries.Add(emaFast);
                emaSlowSeries.Add(emaSlow);
                posSeries.Add(position == null ? 0 : position.DirLong ? 1 : -1);
                flatSeries.Add(isFlat ? 1.0 : 0.0);
            }

            previous = candle;
            counter++;
        }

        double lastPrice = candles.Count > 0 ? candles.Last().Open : 0;
        double lastEquity = GetCurrentEquity(depo, position, lastPrice);
        double profit = (lastEquity - startDepo) / Math.Max(1e-9, startDepo);
        double days = Math.Max(1e-9, (to - from).TotalDays);

        return new BacktestOutput(
            deals,
            xs,
            equitySeries,
            emaFastSeries,
            emaSlowSeries,
            posSeries,
            flatSeries,
            lastEquity,
            profit / days * 30d,
            maxDrawdown,
            deals.Count / days);
    }

    private static Deal? TryOpenPosition(
        HistoricalCandle candle,
        bool isLong,
        double depo,
        double riskPct,
        double slAtr,
        double atr,
        double maxStopPct,
        double maxNotionalPct,
        double commission)
    {
        double entryPrice = candle.Open;
        double riskMoney = depo * riskPct;

        double stopDistAtr = slAtr * atr;
        double stopDistPct = entryPrice * maxStopPct;
        double stopDist = Math.Max(Math.Min(stopDistAtr, stopDistPct), 1e-9);

        long qtyByRisk = (long)Math.Floor(riskMoney / stopDist);
        double maxNotional = depo * maxNotionalPct;
        long qtyByNotional = (long)Math.Floor(maxNotional / Math.Max(1e-9, entryPrice));
        long qty = Math.Min(qtyByRisk, qtyByNotional);

        if (qty <= 0)
            return null;

        double stopPrice = isLong ? entryPrice - stopDist : entryPrice + stopDist;
        double signedQty = qty * (isLong ? 1 : -1);
        double dealSum = entryPrice * signedQty;
        depo -= dealSum + Math.Abs(dealSum) * commission;

        return new Deal
        {
            PriceStart = entryPrice,
            DateStart = candle.Time,
            DirLong = isLong,
            Qty = qty,
            StopPrice = stopPrice,
            StopDist = stopDist,
            AtrAtEntry = atr,
            DepoAfterEntry = depo
        };
    }

    private static bool IsStopHit(Deal position, HistoricalCandle candle)
        => position.DirLong ? candle.Low <= position.StopPrice : candle.High >= position.StopPrice;

    private static void ClosePosition(Deal position, double price, DateTime time, string reason, ref double depo, double commission, List<Deal> deals)
    {
        position.PriceEnd = price;
        position.DateEnd = time;
        position.ExitReason = reason;

        double signedQty = position.Qty * (position.DirLong ? 1 : -1);
        double dealSum = position.PriceEnd.Value * signedQty;
        depo = position.DepoAfterEntry + dealSum - Math.Abs(dealSum) * commission;

        deals.Add(position);
    }

    private static double GetCurrentEquity(double depo, Deal? position, double currentPrice)
    {
        if (position == null)
            return depo;

        double signedQty = position.Qty * (position.DirLong ? 1 : -1);
        return position.DepoAfterEntry + signedQty * currentPrice;
    }

    private void Render(BacktestResult result)
    {
        foreach (var oldPlot in plot.Multiplot.GetPlots())
            oldPlot.Clear();

        plot.Plot.Clear();
        plot.Multiplot.Reset();
        plot.Multiplot.AddPlots(3);

        var mainPlot = plot.Multiplot.GetPlot(0);
        string paramLine = string.Join(", ", result.BestParams.Select(p => $"{p.Key}={p.Value}"));

        mainPlot.Title(
            $"EMA Crossover [{result.Share.Ticker}] {result.Share.Name}\r\n" +
            $"Всего сделок: {result.Deals.Count}. Equity: {result.LastEquity:C}\r\n" +
            $"Данные: {result.From:g} - {result.To:g}\r\n" +
            $"Оптимизация: {result.OptimizationFrom:g} - {result.OptimizationTo:g}\r\n" +
            $"Итоговая прибыльность: {result.TestFrom:g} - {result.TestTo:g}\r\n" +
            $"Прибыль: {result.ProfitPerMonth:P}/мес. Макс. просадка: {result.Drawdown:P}. Сделок/день: {result.DealsFrequency:F2}\r\n" +
            $"Params: {paramLine}");

        mainPlot.Add.Candlestick(result.Candles
            .Select(x => new OHLC(x.Open, x.High, x.Low, x.Close, x.Time, TimeSpan.FromHours(1)))
            .ToList());

        var fastPlot = mainPlot.Add.Scatter(result.Xs, result.EmaFastSeries);
        fastPlot.MarkerShape = MarkerShape.None;

        var slowPlot = mainPlot.Add.Scatter(result.Xs, result.EmaSlowSeries);
        slowPlot.MarkerShape = MarkerShape.None;

        var equityPlot = plot.Multiplot.GetPlot(1);
        equityPlot.Title($"Кривая капитала: {result.OptimizationFrom:g} - {result.TestTo:g}. Equity: {result.EquitySeries.LastOrDefault():F0}  deals: {result.Deals.Count}");
        AddWalkForwardBands(equityPlot, result.OptimizationFrom, result.OptimizationTo, result.TestFrom, result.TestTo);
        var eq = equityPlot.Add.Scatter(result.Xs, result.EquitySeries);
        eq.MarkerShape = MarkerShape.None;

        var statePlot = plot.Multiplot.GetPlot(2);
        statePlot.Title("Позиция (-1/0/+1) и флет-индикатор");
        var pos = statePlot.Add.Scatter(result.Xs, result.PositionSeries);
        pos.MarkerShape = MarkerShape.None;

        var flat = statePlot.Add.Scatter(result.Xs, result.FlatSeries);
        flat.MarkerShape = MarkerShape.None;

        plot.Multiplot.Layout = new ScottPlot.MultiplotLayouts.Grid(3, 1);
        plot.Multiplot.SharedAxes.ShareX(plot.Multiplot.GetPlots());

        var padding = new PixelPadding(left: 100, right: 10, bottom: 50, top: 50);
        foreach (var subPlot in plot.Multiplot.GetPlots())
        {
            subPlot.Axes.DateTimeTicksBottom();
            subPlot.Layout.Fixed(padding);
        }

        mainPlot.Layout.Fixed(new PixelPadding(left: 100, right: 10, bottom: 50, top: 120));
        plot.Refresh();
    }

    private static void AddWalkForwardBands(Plot targetPlot, DateTime optimizationFrom, DateTime optimizationTo, DateTime testFrom, DateTime testTo)
    {
        var optimizationBand = targetPlot.Add.HorizontalSpan(optimizationFrom.ToOADate(), optimizationTo.ToOADate());
        optimizationBand.FillColor = ScottPlot.Colors.Green.WithAlpha(40);

        var testBand = targetPlot.Add.HorizontalSpan(testFrom.ToOADate(), testTo.ToOADate());
        testBand.FillColor = ScottPlot.Colors.Red.WithAlpha(40);
    }

    private sealed class Deal
    {
        public double PriceStart { get; set; }
        public DateTime DateStart { get; set; }
        public long Qty { get; set; }
        public bool DirLong { get; set; }
        public double StopPrice { get; set; }
        public double StopDist { get; set; }
        public double AtrAtEntry { get; set; }
        public double DepoAfterEntry { get; set; }
        public double? PriceEnd { get; set; }
        public DateTime? DateEnd { get; set; }
        public string ExitReason { get; set; } = "";
    }

    private sealed record InstrumentOption(string Ticker, string Figi, string Name)
    {
        public string DisplayName => $"{Ticker} - {Name}";
    }

    private sealed record BacktestOutput(
        List<Deal> Deals,
        List<DateTime> EquityXs,
        List<double> EquitySeries,
        List<double> EmaFastSeries,
        List<double> EmaSlowSeries,
        List<double> PositionSeries,
        List<double> FlatSeries,
        double LastEquity,
        double ProfitPerMonth,
        double Drawdown,
        double DealsFrequency)
    {
        public static BacktestOutput Invalid { get; } = new(
            new List<Deal>(),
            new List<DateTime>(),
            new List<double>(),
            new List<double>(),
            new List<double>(),
            new List<double>(),
            new List<double>(),
            0,
            double.MinValue,
            1.0,
            999.0);
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
        List<Deal> Deals,
        List<DateTime> Xs,
        List<double> EquitySeries,
        List<double> EmaFastSeries,
        List<double> EmaSlowSeries,
        List<double> PositionSeries,
        List<double> FlatSeries,
        double LastEquity,
        double ProfitPerMonth,
        double Drawdown,
        double DealsFrequency,
        Dictionary<string, object> BestParams);

    private sealed class MainViewModelWriter : TextWriter
    {
        private readonly EmaCrossoverBacktest vm;

        public MainViewModelWriter(EmaCrossoverBacktest vm)
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
