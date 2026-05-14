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
/// Окно backtest сеточной стратегии.
///
/// Стратегия строит динамическую процентную сетку вокруг цены инструмента.
/// Когда закрытие свечи пересекает уровень сетки, модель открывает сделку
/// от этого уровня: long с take profit на следующем уровне выше и short
/// с take profit на следующем уровне ниже. Риск ограничивается размером
/// позиции, маржой, комиссией и stop loss, рассчитанным от шага сетки.
/// </summary>
public partial class GridStrategy : Window, INotifyPropertyChanged
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

    public GridStrategy()
    {
        InitializeComponent();
        Console.SetOut(new MainViewModelWriter(this));
        DataContext = this;

        _client = InvestApiClientFactory.Create(Token);
        FromDatePicker.SelectedDate = DateTime.Now.Date.AddMonths(-4);
        ToDatePicker.SelectedDate = DateTime.Now.Date;
        StatusText.Text = "Загрузка инструментов...";
    }

    public void NotifyPropertyChanged([CallerMemberName] string propertyName = "")
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadInstrumentListAsync();
        await ApplyAsync();
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
                .Select(x => new InstrumentOption(x.Ticker, x.Figi, x.Name, x.Uid))
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

        var from = FromDatePicker.SelectedDate ?? DateTime.Now.Date.AddMonths(-4);
        var to = (ToDatePicker.SelectedDate ?? DateTime.Now.Date).Date.AddDays(1).AddTicks(-1);
        if (from >= to)
        {
            StatusText.Text = "Дата начала должна быть меньше даты окончания.";
            return;
        }

        ApplyButton.IsEnabled = false;
        ConsoleText = "";
        StatusText.Text = $"Расчет сетки {share.Ticker} с {from:dd.MM.yyyy} по {to:dd.MM.yyyy}...";

        try
        {
            var result = await Task.Run(() => RunBacktest(share, from, to));
            Render(result);
            StatusText.Text =
                $"Готово: {result.Depo.ClosedDeals.Count} сделок, " +
                $"прибыль {result.Profit:P2}, просадка {result.Drawdown:P2}, " +
                $"{result.DealsFrequency:F2} сделок/день.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Ошибка: " + ex.Message;
            MessageBox.Show(ex.ToString(), "GridStrategy", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ApplyButton.IsEnabled = true;
        }
    }

    private BacktestResult RunBacktest(InstrumentOption share, DateTime from, DateTime to)
    {
        HistoricalTimeFrame tf = HistoricalTimeFrame.H1;
        CandleInterval ci = CandleInterval.Hour;

        // Исторические данные читаются из локального кеша HistoricalData,
        // а недостающие свечи догружаются через T-Invest API.
        var shareData = HistoricalData.ReadHistoricalData(share.Ticker, share.Figi, tf, false,
            new HistoricalData.QueryDataDelegate((figi, frame, queryFrom, queryTo) =>
            {
                var candles = _client.MarketData.GetCandles(new GetCandlesRequest
                {
                    Figi = figi,
                    Interval = ci,
                    From = queryFrom.ToUniversalTime().ToTimestamp(),
                    To = queryTo.ToUniversalTime().ToTimestamp()
                });

                return candles.Candles
                    .Where(x => x.IsComplete)
                    .Select(x => x.ToHistoricalCandle())
                    .ToList();
            }));

        var shareCandles = shareData.GetData(from, to).ToList();
        if (shareData.DataHasChanges)
            shareData.SaveHistoricalData();

        if (shareCandles.Count < 2)
            throw new InvalidOperationException("Недостаточно свечей для backtest.");

        ApplyDividendAdjustment(share, from, to, shareCandles);

        var parameters = new Dictionary<string, object>
        {
            ["maxPercentPerDeal"] = 1d,
            ["riskPercent"] = 20.0d,
            ["gridStep"] = 0.2d,
            ["SLMultiplicator"] = 300d
        };

        return Evaluate(share, from, to, shareCandles, parameters);
    }

    private void ApplyDividendAdjustment(InstrumentOption share, DateTime from, DateTime to, List<HistoricalCandle> shareCandles)
    {
        var dividends = _client.Instruments.GetDividends(new GetDividendsRequest
        {
            InstrumentId = share.Uid,
            From = from.ToUniversalTime().ToTimestamp(),
            To = to.ToUniversalTime().ToTimestamp()
        }).Dividends;

        // Дивидендная корректировка убирает искусственные гэпы после отсечки.
        // Без нее сетка воспринимала бы дивидендный разрыв как обычное ценовое движение.
        foreach (var candle in shareCandles)
        {
            var addon = dividends
                .Where(x => candle.Time < x.LastBuyDate.ToDateTime().AddDays(1))
                .Sum(x => Helper.FromQuotation(x.YieldValue) / 100d);

            candle.Low /= 1 + addon;
            candle.High /= 1 + addon;
            candle.Open /= 1 + addon;
            candle.Close /= 1 + addon;
        }
    }

    private BacktestResult Evaluate(
        InstrumentOption share,
        DateTime from,
        DateTime to,
        List<HistoricalCandle> shareCandles,
        Dictionary<string, object> parameters)
    {
        double riskPercent = (double)parameters["riskPercent"];
        double gridStep = (double)parameters["gridStep"];
        double slMultiplicator = (double)parameters["SLMultiplicator"];
        double maxPercentPerDeal = (double)parameters["maxPercentPerDeal"];

        double maxDepo = 0;
        double maxDrawDown = 0;
        double startDepo = 1_000_000;
        var depo = new Depo(startDepo);
        var depoxs = new List<DateTime>();
        var depoys = new List<double>();
        var longDeals = new Dictionary<double, Deal?>();
        var shortDeals = new Dictionary<double, Deal?>();
        var gridLevels = new List<double>();
        HistoricalCandle? lastCandle = null;
        double gridStepDecimal = gridStep / 100d;

        foreach (var candle in shareCandles.Where(x => x.Time > from && x.Time < to))
        {
            // Первый уровень сетки ставится на цену закрытия первой свечи тестового окна.
            if (!gridLevels.Any())
            {
                var level = candle.Close;
                gridLevels.Add(level);
                longDeals.Add(level, null);
                shortDeals.Add(level, null);
            }

            // Если цена вышла за текущие границы, сетка расширяется вверх.
            while (candle.Close > gridLevels.Max())
            {
                var level = gridLevels.Max() * (1d + gridStepDecimal);
                gridLevels.Add(level);
                longDeals.Add(level, null);
                shortDeals.Add(level, null);
            }

            // Если цена вышла ниже текущих границ, сетка расширяется вниз.
            while (candle.Close < gridLevels.Min())
            {
                var level = gridLevels.Min() * (1d - gridStepDecimal);
                gridLevels.Add(level);
                longDeals.Add(level, null);
                shortDeals.Add(level, null);
            }

            if (lastCandle != null)
            {
                // Сначала закрываем сделки, по которым текущая свеча коснулась TP или SL.
                foreach (var deal in depo.OpenedDeals.ToList())
                {
                    if (!deal.MustCloseAtCandle(candle))
                        continue;

                    deal.CloseDeal(candle, depo);
                    var longLevel = longDeals.FirstOrDefault(x => x.Value == deal);
                    if (!longLevel.Equals(default(KeyValuePair<double, Deal?>)))
                        longDeals[longLevel.Key] = null;

                    var shortLevel = shortDeals.FirstOrDefault(x => x.Value == deal);
                    if (!shortLevel.Equals(default(KeyValuePair<double, Deal?>)))
                        shortDeals[shortLevel.Key] = null;
                }

                // Затем ищем уровни, которые цена пересекла между прошлым и текущим Close.
                // На каждом свободном уровне открываются две независимые идеи:
                // long на движение к верхнему соседнему уровню и short на движение к нижнему.
                foreach (var level in gridLevels.Where(x =>
                             x >= Math.Min(candle.Close, lastCandle.Close) &&
                             x <= Math.Max(candle.Close, lastCandle.Close)))
                {
                    if (longDeals[level] == null)
                        longDeals[level] = new Deal(candle, true, level * (1d + gridStepDecimal), level * (1d - gridStepDecimal * slMultiplicator), depo, riskPercent, maxPercentPerDeal);

                    if (shortDeals[level] == null)
                        shortDeals[level] = new Deal(candle, false, level * (1d - gridStepDecimal), level * (1d + gridStepDecimal * slMultiplicator), depo, riskPercent, maxPercentPerDeal);
                }
            }

            lastCandle = candle;

            // Обновляем equity curve и максимальную просадку по mark-to-market стоимости.
            var currentDepo = depo.GetCurrentMoney(candle.Close);
            if (currentDepo > maxDepo)
                maxDepo = currentDepo;
            if (maxDepo > 0 && (maxDepo - currentDepo) / maxDepo > maxDrawDown)
                maxDrawDown = (maxDepo - currentDepo) / maxDepo;

            depoxs.Add(candle.Time);
            depoys.Add(currentDepo);
        }

        // В конце backtest все оставшиеся открытые сделки закрываются последней свечой,
        // чтобы итоговый результат не зависел от незакрытой позиции.
        depo.OpenedDeals.ToList().ForEach(x => x.CloseDeal(shareCandles.Last(), depo));

        var profit = (depo.GetCurrentMoney(shareCandles.Last().Close) - startDepo) / startDepo;
        var days = Math.Max(1d, (to - from).TotalDays);
        return new BacktestResult(
            share,
            from,
            to,
            shareCandles,
            depo,
            depoxs,
            depoys,
            profit,
            maxDrawDown,
            depo.ClosedDeals.Count / days);
    }

    private void Render(BacktestResult result)
    {
        plot.Multiplot.Reset();
        plot.Multiplot.AddPlots(2);

        var mainPlot = plot.Multiplot.GetPlot(0);
        mainPlot.Title(
            $"GridStrategy [{result.Share.Ticker}] {result.Share.Name}\r\n" +
            $"Всего {result.Depo.ClosedDeals.Count} сделок. Ликвидный портфель {result.Depo.LiquidPortfolio:C}\r\n" +
            $"c {result.From:dd.MM.yyyy} по {result.To:dd.MM.yyyy}. " +
            $"Прибыль: {result.Profit:P}. Макс. просадка: {result.Drawdown:P}. Сделок/день: {result.DealsFrequency:F2}");

        var candleSpan = TimeSpan.FromHours(1);
        mainPlot.Add.Candlestick(result.Candles
            .Select(x => new OHLC(x.Open, x.High, x.Low, x.Close, x.Time, candleSpan))
            .ToList());

        foreach (var deal in result.Depo.ClosedDeals)
        {
            mainPlot.Add.Line(
                new Coordinates(deal.Start.Time.ToOADate(), deal.Start.Close),
                new Coordinates(deal.End.Time.ToOADate(), deal.End.Close))
                .Color = deal.IsLong ? ScottPlot.Colors.Green : ScottPlot.Colors.Red;
        }

        var equityPlot = plot.Multiplot.GetPlot(1);
        equityPlot.Title("Динамика расчетной стоимости депозита");
        var equity = equityPlot.Add.Scatter(result.DepoXs, result.DepoYs);
        equity.MarkerShape = MarkerShape.None;
        equity.LineWidth = 2;

        var padding = new PixelPadding(left: 100, right: 10, bottom: 50, top: 50);
        foreach (var subPlot in plot.Multiplot.GetPlots())
        {
            subPlot.Layout.Fixed(padding);
            subPlot.Axes.DateTimeTicksBottom();
        }

        mainPlot.Layout.Fixed(new PixelPadding(left: 100, right: 10, bottom: 50, top: 100));
        plot.Multiplot.SharedAxes.ShareX(plot.Multiplot.GetPlots());
        plot.Refresh();
    }

    /// <summary>
    /// Смоделированная сделка сетки: свеча входа, свеча выхода,
    /// направление позиции, размер, take profit и stop loss.
    /// </summary>
    public class Deal
    {
        public HistoricalCandle Start { get; set; }
        public HistoricalCandle End { get; set; }
        public long Quontity { get; set; }
        public double StopLoss { get; set; }
        public double TakeProfit { get; set; }

        public Deal(HistoricalCandle start, bool isLong, double takeProfit, double stopLoss, Depo depo, double maxRiskPercent = double.NaN, double maxPercentPerDeal = 3d)
        {
            Start = start;

            // Доступный капитал считается после вычета уже занятой маржи.
            // Размер позиции дополнительно ограничивается максимальной долей на одну сделку.
            var availableMoney = Math.Max(0, (depo.LiquidPortfolio - depo.PortfolioMargin) / (depo.MarginPercent / 100d));
            Quontity = Convert.ToInt64(Math.Floor(availableMoney / Start.Close * maxPercentPerDeal / 100d) * (isLong ? 1d : -1d));
            if (!double.IsNaN(maxRiskPercent) && Quontity > 0)
            {
                // Risk cap не дает одной сделке потерять больше заданного процента депозита
                // при движении цены до stop loss.
                double riskMoney = depo.LiquidPortfolio * maxRiskPercent / 100d;
                long maxRiskQuontity = Convert.ToInt64(Math.Floor(riskMoney / stopLoss));
                Quontity = Math.Min(Quontity, maxRiskQuontity);
            }

            if (Quontity == 0)
                return;

            var money = Quontity * Start.Close;
            TakeProfit = takeProfit;
            StopLoss = stopLoss;
            depo.LiquidPortfolio -= money + Math.Abs(money * depo.CommissionPercent / 100d);
            depo.PortfolioMargin += Math.Abs(money * depo.MarginPercent / 100d);
            depo.OpenedDeals.Add(this);
        }

        public bool IsClosed => End != null;
        public bool IsLong => Quontity > 0;

        public bool MustCloseAtCandle(HistoricalCandle candle)
        {
            // Проверяем достижение TP/SL внутри свечи по High/Low.
            // Модель не знает порядок касаний внутри свечи, поэтому факт касания закрывает сделку.
            bool takeProfit = false;
            bool stopLoss = false;
            if (!double.IsNaN(StopLoss))
                takeProfit = IsLong && candle.Low < StopLoss || !IsLong && candle.High > StopLoss;
            if (!double.IsNaN(TakeProfit))
                stopLoss = IsLong && candle.High > TakeProfit || !IsLong && candle.Low < TakeProfit;
            return stopLoss || takeProfit;
        }

        public void CloseDeal(HistoricalCandle end, Depo depo)
        {
            // Закрытие возвращает денежный результат в ликвидную часть портфеля,
            // снимает маржу и переносит сделку из открытых в закрытые.
            End = end;
            var money = Quontity * End.Close;
            depo.LiquidPortfolio += money - Math.Abs(money * depo.CommissionPercent / 100d);
            depo.PortfolioMargin -= Math.Abs(Start.Close * Quontity * depo.MarginPercent / 100d);
            depo.OpenedDeals.Remove(this);
            depo.ClosedDeals.Add(this);
        }
    }

    /// <summary>
    /// Упрощенная модель депозита для backtest: ликвидная часть портфеля,
    /// занятая маржа, комиссия и списки открытых/закрытых сделок.
    /// </summary>
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
            // Mark-to-market: ликвидные деньги плюс текущая стоимость открытых позиций
            // за вычетом комиссии, которую пришлось бы заплатить при закрытии.
            return LiquidPortfolio + OpenedDeals.Select(x =>
            {
                var money = x.Quontity * currentPrice;
                return money - Math.Abs(money * CommissionPercent / 100d);
            }).Sum();
        }
    }

    private sealed record InstrumentOption(string Ticker, string Figi, string Name, string Uid)
    {
        public string DisplayName => $"{Ticker} - {Name}";
    }

    private sealed record BacktestResult(
        InstrumentOption Share,
        DateTime From,
        DateTime To,
        List<HistoricalCandle> Candles,
        Depo Depo,
        List<DateTime> DepoXs,
        List<double> DepoYs,
        double Profit,
        double Drawdown,
        double DealsFrequency);

    private class MainViewModelWriter : TextWriter
    {
        private readonly GridStrategy vm;

        public MainViewModelWriter(GridStrategy vm)
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
