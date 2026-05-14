using Google.Protobuf.WellKnownTypes;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using Tinkoff.InvestApi;
using System.Linq;
using ScottPlot;
using System.Windows.Media.Animation;
using Tinkoff.InvestApi.V1;
using ScottPlot.Colormaps;
using ScottPlot.AxisRules;
using System.Net.Quic;
using CommonClasses;
using System.Runtime.CompilerServices;
using ScottPlot.Plottables;
using System.Security.Cryptography;
using System.Diagnostics.Metrics;
using OpenTK.Compute.OpenCL;
using System.Security.RightsManagement;
namespace StrategyBacktester;

/// <summary>
/// OrderbookTimeDensity — окно визуализации и подбора параметров стратегии
/// временно́й плотности цены на часовых данных.
///
/// СТРАТЕГИЯ (Time Density / Horizontal Support-Resistance на H1):
///   Для каждой часовой свечи весь ценовой диапазон Low→High получает
///   равномерный вес 1.0 на каждом уровне (шаг цены = MinPriceIncrement).
///   Между свечами накопленная плотность умножается на коэффициент
///   (1 - Decay% / 100), который задается на форме.
///   Полученные значения дополнительно обрезаются сверху (ManualRange = 5)
///   для лучшего контраста.
///
///   В отличие от OrderbookDensity:
///     • Тиковый интервал — H1 (не D1)
///     • Распределение — равномерное по Low→High, а не гауссово вдоль Open→Close
///     • Затухание — мультипликативное через параметр Decay%
///     • Вес не модулируется объёмом
///
///   Результат: карта ценовых уровней, на которых цена проводила наибольшее
///   время в выбранном периоде. Высокая плотность → зона баланса / консолидации,
///   от которой вероятен отбой (для внутридневных стратегий).
///
/// ОСНОВНЫЕ МЕТОДЫ:
///   LoadData()                — загрузка часовых свечей (Tinkoff API / кэш)
///   RebuildPlot()             — перестроение тепловой карты и графика
///   btnApply_Click            — применение новых параметров
///   GetPriceDistribution(p1, p2, vol, step, digits)
///                           — равномерное распределение веса по диапазону
///   AddDistribution(d1, d2, step)
///                           — слияние распределений
///   AddCandleDistribution(c, prev, step, digits)
///                           — контур одной свечи: равномерный Low→High
///
/// ИНСТРУМЕНТЫ: Tinkoff Invest API, ScottPlot (2D-Heatmap + Candlestick)
/// </summary>
public partial class OrderbookTimeDensity : Window
{
    /// <summary>
    /// Одна открытая сделка. Определяет цену/дату входа (pricestart, datestart),
    /// объём (qty), направление (dirlong), цену/дату выхода (priceend, dateend),
    /// максимальную цену (maxprice) и уровень сетки (gridlevel).
    /// В текущей версии OrderbookTimeDensity не используется напрямую.
    /// </summary>
    private class Deal
    {
        public double pricestart { get; set; }
        public DateTime datestart { get; set; }
        public long qty { get; set; }
        public bool dirlong {get;set;}
        public double? priceend { get; set; }
        public DateTime? dateend { get; set; }
        public double? maxprice { get; set; }
        public double gridlevel { get; set; }
    }

    // ── Кэшированные данные для перестроения графика ──
    private List<HistoricalCandle> _candles;    // исходные часовые свечи
    private double _ymin, _ymax;                 // min/max цены (ось Y)
    private DateTime _dmin, _dmax;               // min/max даты (ось X)
    private double _stepy;                       // шаг цены = MinPriceIncrement
    private TimeSpan _stepx;                     // шаг времени (1 час)
    private TimeSpan _ts;                        // длительность свечи для OHLC (1h)
    private OHLC[] _scottCandles;                // свечи для отрисовки ScottPlot

    // ── Параметры стратегии (шаг изменения 0.05) ──
    private double _decayPercent = 5d;       // процентное затухание накопленной плотности между свечами
    private readonly InvestApiClient _client;
    private List<InstrumentOption> _shares = new();

    public bool rndbool (Random r)
    {
        return r.Next(2) == 1 ? true : false;
    }
    private (double, double) CalcProfit (List<Deal> deals, double CurrentPrice)
    {
        var summclose = 0d;
        var summopen = 0d;
        foreach (var deal in deals)
        {
            summclose += CurrentPrice * (deal.dirlong ? 1d : -1d) * deal.qty;
            summopen += deal.pricestart * (deal.dirlong ? 1d : -1d) * deal.qty;
        }
        return (summopen, summclose);
    }

    public OrderbookTimeDensity ()
    {
        InitializeComponent();

        _client = InvestApiClientFactory.Create(WindowsCredentialManager.ReadSecret(StrategyBacktester.Properties.Settings.Default.ApiKey) ?? "key not found");

        // Set default UI values
        numDecay.Value = _decayPercent;
        LoadTickerList("GAZP");
        dtFrom.SelectedDate = DateTime.Now.AddMonths(-3);
        dtTo.SelectedDate = DateTime.Now;

        _ts = new TimeSpan(1, 0, 0);

        plot.Multiplot.AddPlots(1);
        PixelPadding padding = new(left: 100, right: 10, bottom: 50, top: 50);
        foreach (var plot in plot.Multiplot.GetPlots())
            plot.Layout.Fixed(padding);

        LoadData();
        RebuildPlot();
    }

    private void LoadTickerList(string defaultTicker)
    {
        _shares = _client.Instruments.Shares().Instruments
            .Where(u => u.ApiTradeAvailableFlag)
            .OrderBy(u => u.Ticker)
            .Select(u => new InstrumentOption(u.Ticker, u.Figi, u.Name, u.MinPriceIncrement))
            .ToList();

        cmbTicker.ItemsSource = _shares;
        cmbTicker.SelectedItem = _shares.FirstOrDefault(u => u.Ticker.StartsWith(defaultTicker, StringComparison.OrdinalIgnoreCase))
                                 ?? _shares.FirstOrDefault();
    }

    private void LoadData()
    {
        if (cmbTicker.SelectedItem is not InstrumentOption share)
        {
            MessageBox.Show("Please select a ticker.", "Error");
            return;
        }
        var from = dtFrom.SelectedDate ?? new DateTime(2025, 1, 1);
        var to = dtTo.SelectedDate ?? DateTime.Now;

        HistoricalTimeFrame tf = HistoricalTimeFrame.H1;
        CandleInterval ci = CandleInterval.Hour;
        var data = HistoricalData.ReadHistoricalData(share.Ticker, share.Figi, tf, false, new HistoricalData.QueryDataDelegate((figi, frame, from2, to2) =>
        {
            var candles = _client.MarketData.GetCandles(new GetCandlesRequest { Figi = figi, Interval = ci, From = from2.ToUniversalTime().ToTimestamp(), To = to2.ToUniversalTime().ToTimestamp() });
            return candles.Candles.Select(u => new HistoricalCandle
            {
                Low = Helper.FromQuotation(u.Low),
                High = Helper.FromQuotation(u.High),
                Open = Helper.FromQuotation(u.Open),
                Close = Helper.FromQuotation(u.Close),
                Volume = u.Volume,
                Time = u.Time.ToDateTime()
            }).ToList();
        }));
        _candles = data.GetData(from, to);
        if (data.DataHasChanges)
            data.SaveHistoricalData();

        _dmin = _candles.Min(u => u.Time);
        _dmax = _candles.Max(u => u.Time);
        _ymin = _candles.Min(u => u.Low);
        _ymax = _candles.Max(u => u.High);
        _stepy = Helper.FromQuotation(share.MinPriceIncrement);
        _stepx = Helper.CandleIntervalTimeSpan(ci);

        _scottCandles = _candles.Select(u => new OHLC(u.Open, u.High, u.Low, u.Close, u.Time, _ts)).ToArray();
    }

    /// <summary>
    /// Перестроение 2D-тепловой карты временно́й плотности.
    ///
    /// Алгоритм:
    ///   1. Инициализация матрицы heatmap[priceStep, timeStep].
    ///   2. Для каждой часовой свечи:
    ///      a. Пропуски интерполируются копированием предыдущего распределения.
    ///      b. Процентное затухание: val = val * (1 - Decay% / 100).
    ///      c. AddCandleDistribution — равномерный вес на Low→High.
    ///      d. Запись в heatmap.
    ///   3. Отрисовка heatmap + свечной график.
    ///      Значения плотности capped сверху (ManualRange = 5) для контрастности.
    ///
    /// Отличия от OrderbookDensity.RebuildPlot:
    ///   — Затухание задается параметром Decay% на форме.
    ///   — Нет нормализации по объёму.
    ///   — Ручной лимит диапазона heatmap (0..5).
    /// </summary>
    private void RebuildPlot()
    {
        var sp = plot.Multiplot.GetPlot(0);
        sp.Clear();

        // ── Определение размера матрицы ──
        var stepsy = Convert.ToInt32(Math.Round((_ymax - _ymin) / _stepy));  // число ценовых уровней
        var stepsx = Convert.ToInt32(Math.Round((_dmax - _dmin) / _stepx));  // число временных шагов
        double[,] heatmap = new double[stepsy, stepsx];
        HistoricalCandle lastcandle = null;
        double decaymultiplier = 1 - _decayPercent / 100d;
        List<Tuple<double, double>> lastdistribution = new List<Tuple<double, double>>();  // текущее распределение

        // ── Основной цикл по свечам ──
        {
            int ii = 0;  // индекс временного шага
            foreach (var c in _candles)
            {
                // Расстояние между свечами в шагах
                int distance = 1;
                if (lastcandle != null)
                    distance = Convert.ToInt32(Math.Round((c.Time - lastcandle.Time) / _stepx));

                // Интерполяция пропусков — копирование предыдущего распределения
                for (int k = 0; k < distance - 1; k++)
                {
                    if (ii < stepsx)
                        foreach (var val in lastdistribution)
                        {
                            var i = Math.Min(stepsy - 1, Math.Max(0, Convert.ToInt32(Math.Round((val.Item1 - _ymin) / _stepy))));
                            heatmap[i, ii] = val.Item2;
                        }
                    ii++;
                }

                // Мультипликативное затухание накопленной плотности между свечами.
                lastdistribution = lastdistribution.Select(u => new Tuple<double, double>(u.Item1, Math.Max(0,u.Item2 * decaymultiplier))).ToList();

                // Добавление равномерного распределения текущей свечи Low→High
                lastdistribution = AddCandleDistribution(c, lastdistribution, _stepy, 2);

                // Запись распределения в heatmap
                if (ii < stepsx)
                    foreach (var val in lastdistribution)
                    {
                        var i = Math.Min(stepsy - 1, Math.Max(0, Convert.ToInt32(Math.Round((val.Item1 - _ymin) / _stepy))));
                        heatmap[i, ii] = val.Item2;
                    }
                ii++;
                lastcandle = c;
            }
        }

        // ── Отрисовка heatmap + свечной график ──
        var hm = sp.Add.Heatmap(heatmap);
        CoordinateRange yRange = new(_ymin, _ymax);
        CoordinateRange xRange = new(_dmin.ToOADate(), _dmax.ToOADate());
        hm.Rectangle = new CoordinateRect(xRange, yRange);
        hm.FlipVertically = true;
        hm.Colormap = new ScottPlot.Colormaps.Grayscale();
        hm.ManualRange = new ScottPlot.Range(0, 5);       // обрезка сверху для контрастности
        sp.Add.Candlestick(_scottCandles);
        sp.Axes.DateTimeTicksBottom();

        plot.Refresh();
    }

    /// <summary>
    /// Обработчик кнопки Apply: считывает текущие параметры (Decay,
    /// тикер, даты), перезагружает данные и перестраивает график.
    /// </summary>
    private void btnApply_Click(object sender, RoutedEventArgs e)
    {
        _decayPercent = numDecay.Value ?? 1d;

        LoadData();
        RebuildPlot();
    }

    private List<Tuple<double, double>> AddDistribution(List<Tuple<double, double>> distribution1, List<Tuple<double, double>> distribution2, double step)
    {
        if (!distribution2.Any())
            return distribution1;
        if (!distribution1.Any())
            return distribution2;
        List<Tuple<double, double>> res = new List<Tuple<double, double>>();
        var maxy1 = distribution1.Max(u => u.Item1);
        var miny1 = distribution1.Min(u => u.Item1);
        var maxy2 = distribution2.Max(u => u.Item1);
        var miny2 = distribution2.Min(u => u.Item1);
        var min = Math.Round(Math.Min(miny1, miny2) / step) * step;
        var max = Math.Max(maxy1, maxy2);
        var steps = Convert.ToInt32(Math.Round((max - min) / step));
        int k1 = 0;
        int k2 = 0;
        int start1 = Convert.ToInt32(Math.Round((miny1 - min) / step));
        int start2 = Convert.ToInt32(Math.Round((miny2 - min) / step));
        for (int i = 0; i <= steps; i++)
        {
            k1 = i - start1;
            k2 = i - start2;
            double val1 = 0;
            if (k1 >= 0 && k1 < distribution1.Count())
                val1 = distribution1[k1].Item2;
            double val2 = 0;
            if (k2 >= 0 && k2 < distribution2.Count())
                val2 = distribution2[k2].Item2;
            res.Add(new Tuple<double, double>(min + i * step, val1 == 0 || val2 == 0 ? val1 + val2 : (Math.Sign(val1) != Math.Sign(val2) ? val2 : val1 + val2)));
        }
        return res.OrderBy(u => u.Item1).ToList();
    }
    /// <summary>
    /// Строит равномерное распределение веса по ценовому диапазону [price1, price2].
    /// В отличие от распределения в OrderbookDensity:
    ///   — Нет гауссовой формы, все уровни получают одинаковый вес (1.0).
    ///   — Нет деления на три сегмента (нижний хвост / плато / верхний хвост).
    ///   — Нет модуляции объёмом (volume игнорируется).
///
    /// Результат: простая "прямоугольная" плотность — каждый уровень в диапазоне
    /// Low→High считается "посещённым" с весом 1.
    /// </summary>
    private List<Tuple<double,double>> GetPriceDistribution(double price1, double price2, double volume, double step, int digits)
    {
        var pricemax = Math.Round(Math.Max(price1, price2) / step, digits) * step;
        var pricemin = Math.Round(Math.Min(price1, price2) / step, digits) * step;
        var steps = Convert.ToInt32(Math.Round((pricemax - pricemin) / step));

        List<Tuple<double, double>> res = new List<Tuple<double, double>>();
        for (int i = 0 ; i < steps; i++)
        {
            var cprice = pricemin + i * step;
            var val = 1d;   // uniform weight (volume не используется)
            res.Add(new Tuple<double,double>(cprice, val));
        }
        return res;
    }
    /// <summary>
    /// Строит контур распределения одной часовой свечи.
    /// В отличие от OrderbookDensity.AddCandleDistribution — всего один сегмент
    /// Low→High, без разбивки на Open→...→Close. Это даёт равномерную плотность
    /// по всему диапазону свечи.
    ///
    /// Предыдущее распределение (prevdistribution) прикрепляется спереди,
    /// что создаёт эффект накопления плотности на уровнях,
    /// часто посещаемых за временной период.
    /// </summary>
    private List<Tuple<double, double>> AddCandleDistribution(HistoricalCandle c, List<Tuple<double,double>> prevdistribution, double step, int digits)
    {
        // Равномерное распределение по Low→High (весь диапазон свечи)
        var res = GetPriceDistribution(c.Low, c.High, c.Volume, step, digits);
        if (prevdistribution.Any())
            res = AddDistribution(prevdistribution, res, step);
        return res;
    }

    public class DoubleComparer : Comparer<double>
    {
        public override int Compare(double x, double y)
        {
            return x > y ? 1 : (x < y ? 1:0);
        }
    }

    private sealed record InstrumentOption(string Ticker, string Figi, string Name, Quotation MinPriceIncrement)
    {
        public string DisplayName => $"{Ticker} - {Name}";
    }
}

