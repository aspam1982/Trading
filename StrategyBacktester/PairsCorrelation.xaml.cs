using CommonClasses;
using Google.Protobuf.WellKnownTypes;
using ScottPlot;
using System.Windows;
using Tinkoff.InvestApi;
using Tinkoff.InvestApi.V1;

namespace StrategyBacktester;

/// <summary>
/// Исследование парного расхождения акции и связанного фьючерса.
///
/// Окно загружает минутные свечи выбранной акции и фьючерса, совмещает их
/// по времени и анализирует отклонение отношения цен от локального тренда.
/// Это не торговая система само по себе, а диагностический график: он помогает
/// увидеть периоды, когда фьючерс и базовая акция временно расходятся.
/// </summary>
public partial class PairsCorrelation : Window
{
    private const int RollingPeriod = 100;

    private readonly InvestApiClient _client;
    private List<InstrumentOption> _shares = new();
    private List<InstrumentOption> _futures = new();

    public PairsCorrelation()
    {
        InitializeComponent();

        _client = InvestApiClientFactory.Create(
            WindowsCredentialManager.ReadSecret(StrategyBacktester.Properties.Settings.Default.ApiKey) ?? "key not found");

        FromDatePicker.SelectedDate = DateTime.Now.Date.AddYears(-1);
        ToDatePicker.SelectedDate = DateTime.Now.Date;
        StatusText.Text = "Загрузка инструментов...";
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadInstrumentListsAsync();
        await ApplyAsync();
    }

    private async void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        await ApplyAsync();
    }

    private async Task LoadInstrumentListsAsync()
    {
        ApplyButton.IsEnabled = false;
        try
        {
            var result = await Task.Run(() =>
            {
                var shares = _client.Instruments.Shares().Instruments
                    .Where(x => x.ApiTradeAvailableFlag)
                    .OrderBy(x => x.Ticker)
                    .Select(x => new InstrumentOption(x.Ticker, x.Figi, x.Name))
                    .ToList();

                var futures = _client.Instruments.Futures().Instruments
                    .Where(x => x.ApiTradeAvailableFlag)
                    .OrderBy(x => x.Ticker)
                    .Select(x => new InstrumentOption(x.Ticker, x.Figi, x.Name))
                    .ToList();

                return (shares, futures);
            });

            _shares = result.shares;
            _futures = result.futures;

            ShareComboBox.ItemsSource = _shares;
            FutureComboBox.ItemsSource = _futures;

            ShareComboBox.SelectedItem = _shares.FirstOrDefault(x => x.Ticker == "SBER") ?? _shares.FirstOrDefault();
            FutureComboBox.SelectedItem = _futures.FirstOrDefault(x => x.Ticker.StartsWith("SBERF", StringComparison.OrdinalIgnoreCase))
                                          ?? _futures.FirstOrDefault();
        }
        finally
        {
            ApplyButton.IsEnabled = true;
        }
    }

    private async Task ApplyAsync()
    {
        if (ShareComboBox.SelectedItem is not InstrumentOption share ||
            FutureComboBox.SelectedItem is not InstrumentOption future)
        {
            StatusText.Text = "Выберите акцию и фьючерс.";
            return;
        }

        var from = FromDatePicker.SelectedDate ?? new DateTime(2025, 01, 01);
        var to = (ToDatePicker.SelectedDate ?? DateTime.Now.Date).Date.AddDays(1).AddTicks(-1);
        if (from >= to)
        {
            StatusText.Text = "Дата начала должна быть меньше даты окончания.";
            return;
        }

        ApplyButton.IsEnabled = false;
        StatusText.Text = $"Расчет {share.Ticker} / {future.Ticker} с {from:dd.MM.yyyy} по {to:dd.MM.yyyy}...";

        try
        {
            var result = await Task.Run(() => Calculate(share, future, from, to));
            Render(result);
            StatusText.Text =
                $"Готово: {result.Pairs.Count} общих минутных свечей. " +
                $"Среднее отклонение: {result.AverageAbsDeviation:P2}, максимум: {result.MaxAbsDeviation:P2}.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Ошибка: " + ex.Message;
            MessageBox.Show(ex.ToString(), "PairsCorrelation", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ApplyButton.IsEnabled = true;
        }
    }

    private CalculationResult Calculate(InstrumentOption share, InstrumentOption future, DateTime from, DateTime to)
    {
        HistoricalTimeFrame tf = HistoricalTimeFrame.M1;
        CandleInterval ci = CandleInterval._1Min;

        var shareData = ReadCandles(share, tf, ci);
        var futureData = ReadCandles(future, tf, ci);

        var shareCandles = FilterRequestedInterval(shareData.GetData(from, to), from, to);
        var futureCandles = FilterRequestedInterval(futureData.GetData(from, to), from, to);

        if (shareData.DataHasChanges)
            shareData.SaveHistoricalData();
        if (futureData.DataHasChanges)
            futureData.SaveHistoricalData();

        var pairs = AlignByTimestamp(shareCandles, futureCandles);
        CalculateRatioDeviation(pairs, RollingPeriod);

        return new CalculationResult(share, future, from, to, shareCandles, futureCandles, pairs);
    }

    private static List<HistoricalCandle> FilterRequestedInterval(IEnumerable<HistoricalCandle> candles, DateTime from, DateTime to)
    {
        return candles
            .Where(x => x.Time >= from && x.Time <= to)
            .OrderBy(x => x.Time)
            .ToList();
    }

    private HistoricalData ReadCandles(InstrumentOption instrument, HistoricalTimeFrame tf, CandleInterval ci)
    {
        return HistoricalData.ReadHistoricalData(instrument.Ticker, instrument.Figi, tf, false,
            new HistoricalData.QueryDataDelegate((figi, frame, from, to) =>
            {
                var candles = _client.MarketData.GetCandles(new GetCandlesRequest
                {
                    Figi = figi,
                    Interval = ci,
                    From = from.ToUniversalTime().ToTimestamp(),
                    To = to.ToUniversalTime().ToTimestamp()
                });

                return candles.Candles
                    .Where(x => x.IsComplete)
                    .Select(x => x.ToHistoricalCandle())
                    .ToList();
            }));
    }

    private static List<Pair> AlignByTimestamp(IReadOnlyList<HistoricalCandle> candles1, IReadOnlyList<HistoricalCandle> candles2)
    {
        // Сравниваем только те минуты, которые есть в обеих сериях.
        // Это защищает расчет от разной торговой активности и пропусков свечей.
        var pairs = new List<Pair>();
        int i1 = 0;
        int i2 = 0;

        while (i1 < candles1.Count && i2 < candles2.Count)
        {
            var t1 = candles1[i1].Time;
            var t2 = candles2[i2].Time;

            if (t1 < t2)
            {
                i1++;
                continue;
            }

            if (t2 < t1)
            {
                i2++;
                continue;
            }

            pairs.Add(new Pair(candles1[i1], candles2[i2]));
            i1++;
            i2++;
        }

        return pairs;
    }

    private static void CalculateRatioDeviation(List<Pair> pairs, int period)
    {
        // "Correlation" в старом названии поля фактически является не Pearson correlation,
        // а относительным отклонением текущего ratio Price1/Price2 от rolling-линейного тренда.
        for (int idx = 0; idx < pairs.Count; idx++)
        {
            if (idx < 10)
                continue;

            var window = pairs.Take(idx).TakeLast(period).ToList();
            int n = window.Count;
            if (n < 2)
                continue;

            double sumXY = 0d;
            double sumX = 0d;
            double sumY = 0d;
            double sumX2 = 0d;

            for (int i = 1; i <= n; i++)
            {
                var ratio = window[i - 1].Price1 / window[i - 1].Price2;
                sumY += ratio;
                sumXY += ratio * i;
                sumX += i;
                sumX2 += i * i;
            }

            var denominator = n * sumX2 - sumX * sumX;
            if (Math.Abs(denominator) < 1e-12)
                continue;

            var a = (n * sumXY - sumX * sumY) / denominator;
            var b = (sumY * sumX2 - sumX * sumXY) / denominator;
            var expectedRatio = a * n + b;

            if (Math.Abs(expectedRatio) > 1e-12)
                pairs[idx].Correlation = (pairs[idx].Price1 / pairs[idx].Price2 - expectedRatio) / expectedRatio;
        }
    }

    private void Render(CalculationResult result)
    {
        foreach (var oldPlot in plot.Multiplot.GetPlots())
            oldPlot.Clear();

        plot.Plot.Clear();
        plot.Multiplot.Reset();
        plot.Multiplot.AddPlots(4);
        plot.Multiplot.Layout = new ScottPlot.MultiplotLayouts.Grid(2, 2);

        TimeSpan candleSpan = TimeSpan.FromMinutes(1);

        var sharePlot = plot.Multiplot.GetPlot(0);
        sharePlot.Title($"{result.Share.Ticker}: акция");
        sharePlot.Add.Candlestick(result.ShareCandles
            .Select(x => new OHLC(x.Open, x.High, x.Low, x.Close, x.Time, candleSpan))
            .ToArray());

        var deviationPlot = plot.Multiplot.GetPlot(1);
        deviationPlot.Title($"Отклонение ratio {result.Share.Ticker}/{result.Future.Ticker} от rolling-тренда");
        var deviation = deviationPlot.Add.Scatter(
            result.Pairs.Select(x => x.I1.Time).ToList(),
            result.Pairs.Select(x => x.Correlation).ToList());
        deviation.MarkerShape = MarkerShape.None;
        deviation.LineWidth = 2;
        deviationPlot.Add.HorizontalLine(0).LinePattern = LinePattern.Dashed;

        var futurePlot = plot.Multiplot.GetPlot(2);
        futurePlot.Title($"{result.Future.Ticker}: фьючерс");
        futurePlot.Add.Candlestick(result.FutureCandles
            .Select(x => new OHLC(x.Open, x.High, x.Low, x.Close, x.Time, candleSpan))
            .ToArray());

        var volumePlot = plot.Multiplot.GetPlot(3);
        volumePlot.Title($"Объем фьючерса {result.Future.Ticker}");
        var volume = volumePlot.Add.Scatter(
            result.Pairs.Select(x => x.I1.Time).ToList(),
            result.Pairs.Select(x => x.Volume2).ToList());
        volume.MarkerShape = MarkerShape.None;
        volume.FillYBelow = true;
        volume.FillYBelowColor = ScottPlot.Colors.Azure;

        plot.Multiplot.SharedAxes.ShareX(plot.Multiplot.GetPlots());
        foreach (var subPlot in plot.Multiplot.GetPlots())
        {
            subPlot.Axes.DateTimeTicksBottom();
            subPlot.Axes.SetLimitsX(result.From.ToOADate(), result.To.ToOADate());
            subPlot.Layout.Fixed(new PixelPadding(left: 100, right: 10, bottom: 35, top: 55));
        }

        plot.Refresh();
    }

    private sealed record InstrumentOption(string Ticker, string Figi, string Name)
    {
        public string DisplayName => $"{Ticker} - {Name}";
    }

    private sealed record Pair(HistoricalCandle I1, HistoricalCandle I2)
    {
        public double Price1 => I1.Open;
        public double Price2 => I2.Open;
        public double Volume1 => I1.Volume;
        public double Volume2 => I2.Volume;
        public double Coeff => Price1 / Price2;
        public double Correlation { get; set; }
    }

    private sealed record CalculationResult(
        InstrumentOption Share,
        InstrumentOption Future,
        DateTime From,
        DateTime To,
        List<HistoricalCandle> ShareCandles,
        List<HistoricalCandle> FutureCandles,
        List<Pair> Pairs)
    {
        public double AverageAbsDeviation => Pairs.Count == 0 ? 0 : Pairs.Average(x => Math.Abs(x.Correlation));
        public double MaxAbsDeviation => Pairs.Count == 0 ? 0 : Pairs.Max(x => Math.Abs(x.Correlation));
    }
}
