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
/// OrderbookDensity — окно визуализации и подбора параметров стратегии ценовой плотности.
///
/// СТРАТЕГИЯ (Price Density / Volume-Weighted Support-Resistance):
///   Для каждой дневной свечи строится непрерывное нормальное распределение
///   "отпечатка цены" вдоль пути Open → Low/High → Close, взвешенное на Volume.
///   Sigma распределения задаётся параметром Deviation%.
///   Между свечами предыдущее распределение затухает мультипликативно:
///   множитель = (1 − Decay% / 100).
///
///   Полученная 2D-карта (время × цена) показывает ценовые уровни с наибольшей
///   накопленной плотностью — потенциальные зоны поддержки/сопротивления.
///
///   Параметры Deviation% и Decay% перекликаются с одноимёнными порогами робота
///   RobotFuturesArbitr (StartTradeDeviationPercent / CloseTradeDeviationPercent):
///   при анализе плотности можно подобрать значения, при которых карта даёт
///   чёткие, стабильные уровни для принятия торговых решений.
///
/// ОСНОВНЫЕ МЕТОДЫ:
///   LoadData()      — загрузка дневных свечей из Tinkoff API или кэша
///   RebuildPlot()   — перестроение тепловой карты и свечного графика
///   btnApply_Click  — применение новых параметров (Deviation, Decay, тикер, даты)
///   GetPriceDeviation(price1, price2, volume, step, digits)
///                   — нормальное распределение отклонения цены между двумя точками
///   AddDistribution(distribution1, distribution2, step)
///                   — слияние двух распределений с учётом знака (направления)
///   AddCandleDistribution(candle, prevdistribution, step, digits)
///                   — полный контур одной свечи: Open→...→Close с тремя сегментами
///
/// ИНСТРУМЕНТЫ: Tinkoff Invest API, ScottPlot (2D-Heatmap + Candlestick)
/// </summary>
public partial class OrderbookDensity : Window
{
    /// <summary>
    /// Одна открытая сделка в контексте расчёта накопленной плотности.
    /// Поля: цена/дата входа (pricestart, datestart), объём (qty),
    /// направление (dirlong), цена/дата выхода (priceend, dateend),
    /// максимальная цена (maxprice), уровень сетки (gridlevel).
    /// В текущей версии OrderbookDensity не используется напрямую.
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
    private List<HistoricalCandle> _candles;    // исходные дневные свечи
    private double _ymin, _ymax;                 // min/max цены (ось Y)
    private DateTime _dmin, _dmax;               // min/max даты (ось X)
    private double _stepy;                       // шаг цены = MinPriceIncrement
    private TimeSpan _stepx;                     // шаг времени (1 день)
    private TimeSpan _ts;                        // длительность свечи для OHLC (24h)
    private OHLC[] _scottCandles;                // свечи для отрисовки ScottPlot

    // ── Параметры стратегии (шаг изменения 0.05) ──
    private double _deviationPercent = 1d;   // размах нормального распределения (%)
    private double _decayPercent = 0d;       // мультипликативное затухание между свечами (%)
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

    public OrderbookDensity()
    {
        InitializeComponent();

        _client = InvestApiClientFactory.Create(WindowsCredentialManager.ReadSecret(StrategyBacktester.Properties.Settings.Default.ApiKey) ?? "key not found");

        // Set default UI values
        numDeviation.Value = _deviationPercent;
        numDecay.Value = _decayPercent;
        LoadTickerList("SNGS");
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
        var from = dtFrom.SelectedDate ?? DateTime.Now.AddMonths(-3);
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
    /// Перестроение 2D-тепловой карты плотности.
    /// Алгоритм:
    ///   1. Инициализация матрицы heatmap[priceStep, timeStep].
    ///   2. Для каждой свечи:
    ///      a. Если между свечами есть пропуск (distance > 1) — интерполяция: предыдущее
    ///         распределение копируется на пропущенные шаги без изменений.
    ///      b. Мультипликативный decay: lastdistribution *= (1 − Decay%/100).
    ///      c. AddCandleDistribution — добавляет распределение текущей свечи (Open→...→Close).
    ///      d. Запись распределения в текущий столбец heatmap.
    ///   3. Отрисовка heatmap + свечной график поверх.
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
        double decaymultiplier = 1 - _decayPercent / 100d;   // множитель затухания
        List<Tuple<double, double>> lastdistribution = new List<Tuple<double, double>>();  // текущее распределение (price, density)

        // ── Основной цикл по свечам ──
        {
            int ii = 0;  // индекс временного шага
            foreach (var c in _candles)
            {
                // Расстояние между свечами в шагах (для пропусков — выходные/праздники)
                int distance = 1;
                if (lastcandle != null)
                    distance = Convert.ToInt32(Math.Round((c.Time - lastcandle.Time) / _stepx));

                // Заполнение пропущенных шагов — перенос предыдущего распределения без изменений
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

                // Decay — затухание предыдущего распределения
                lastdistribution = lastdistribution.Select(u => new Tuple<double, double>(u.Item1, u.Item2 * decaymultiplier)).ToList();

                // Добавление распределения текущей свечи (Open→Low/High→Close)
                lastdistribution = AddCandleDistribution(c, lastdistribution, _stepy, 2);

                // Запись распределения в текущий столбец heatmap
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
        hm.FlipVertically = true;                     // ось Y: цена растёт вверх
        hm.Colormap = new ScottPlot.Colormaps.Grayscale();
        sp.Add.Candlestick(_scottCandles);            // наложить свечи поверх heatmap
        sp.Axes.DateTimeTicksBottom();

        plot.Refresh();
    }

    /// <summary>
    /// Обработчик кнопки Apply: считывает текущие значения параметров (Deviation, Decay,
    /// тикер, диапазон дат), перезагружает данные и перестраивает график.
    /// </summary>
    private void btnApply_Click(object sender, RoutedEventArgs e)
    {
        _deviationPercent = numDeviation.Value ?? 1d;
        _decayPercent = numDecay.Value ?? 0d;

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
    /// Строит нормальное распределение отклонения цены между двумя точками (price1, price2).
    /// Модель: "отпечаток цены" распространяется вверх и вниз от текущего уровня —
    /// гауссово ядро с амплитудой, пропорциональной volume и _deviationPercent.
    ///
    /// Три сегмента:
    ///   1. Нижний хвост (priceLowMin → priceLowMax) — отрицательные значения,
    ///      sigma = (stepsUp + stepsDown) / 12.
    ///   2. Средний плато (priceLowMax → priceHighMin) — константа на максимуме
    ///      (x = 0), знак зависит от направления движения (price2 > price1 → sell pressure).
    ///   3. Верхний хвост (priceHighMin → priceHighMax) — положительные значения,
    ///      та же sigma.
    /// После построения все значения нормализуются на сумму |val| и масштабируются на volume / 3.
    /// </summary>
    private List<Tuple<double,double>> GetPriceDeviation(double price1, double price2, double volume, double step, int digits)
    {
        var deviation = 1d + _deviationPercent / 100d;
        var pricelowmax = Math.Round(Math.Min(price1, price2) / step)*step;
        var pricelowmin = Math.Round(Math.Round(pricelowmax / deviation / step) * step, digits);
        var pricehighmin = Math.Round(Math.Max(price1, price2) / step) * step;
        var pricehighmax = Math.Round(Math.Round(pricehighmin * deviation / step) * step, digits);
        var stepsup = Convert.ToInt32(Math.Round((pricehighmax - pricehighmin) / step));
        var stepsdown = Convert.ToInt32(Math.Round((pricelowmax - pricelowmin) / step));
        var stepsmid = Convert.ToInt32(Math.Round((pricehighmin - pricelowmax) / step));

        List<Tuple<double, double>> res = new List<Tuple<double, double>>();
        // 1. Нижний хвост (отклонение вниз) — отрицательная плотность (продажа)
        for (int i = -stepsdown ; i < 0; i++)
        {
            var cprice = pricelowmax + i * step;
            double x = 12d * i / (stepsup + stepsdown);
            var val = 1 / Math.Sqrt(2 * Math.PI) * Math.Exp(-0.5d * x * x);
            res.Add(new Tuple<double,double>(cprice, -val));
        }
        // 2. Среднее плато — максимальная плотность на границе разворота
        for (int i = 0; i < stepsmid; i++)
        {
            var cprice = pricelowmax + i * step;
            double x = 0d;
            var val = 1 / Math.Sqrt(2 * Math.PI) * Math.Exp(-0.5d * x * x);
            res.Add(new Tuple<double, double>(cprice, price2 > price1 ? -val : val));
        }
        // 3. Верхний хвост (отклонение вверх) — положительная плотность (покупка)
        for (int i = 0; i <= stepsup; i++)
        {
            var cprice = pricehighmin + i * step;
            double x = 12d * i / (stepsup + stepsdown);
            var val = 1 / Math.Sqrt(2 * Math.PI) * Math.Exp(-0.5d * x * x);
            res.Add(new Tuple<double, double>(cprice, val));
        }
        var summ = res.Sum(u => Math.Abs(u.Item2));
        // Нормализация: сумма |val| → 1, затем масштаб на volume/3 (треть объёма на сегмент)
        res =  res.Select(u => new Tuple<double,double>(u.Item1, u.Item2 / summ / 3d * volume)).ToList();
        return res;
    }
    /// <summary>
    /// Строит полный контур распределения одной свечи: три сегмента Open→Close
    /// с учётом направления свечи (бычья/медвежья).
    ///
    /// Для бычьей свечи (Close > Open):  Open → Low, Low → High, High → Close
    /// Для медвежьей (Close ≤ Open):     Open → High, High → Low, Low → Close
    ///
    /// Каждый сегмент вызывает GetPriceDeviation, результаты сливаются через AddDistribution.
    /// Предыдущее распределение (prevdistribution) передаётся только в первый сегмент,
    /// затем накапливается на весь контур свечи.
    /// </summary>
    private List<Tuple<double, double>> AddCandleDistribution(HistoricalCandle c, List<Tuple<double,double>> prevdistribution, double step, int digits)
    {
        // Сегмент 1: от Open до точки разворота (Low для бычьей, High для медвежьей)
        var res = GetPriceDeviation(c.Open, (c.Close <= c.Open ? c.High : c.Low), c.Volume, step, digits);
        if (prevdistribution.Any())
            res = AddDistribution(prevdistribution, res, step);
        // Сегмент 2: от точки разворота до противоположного экстремума
        res = AddDistribution(res, GetPriceDeviation((c.Close <= c.Open ? c.High : c.Low), (c.Close <= c.Open ? c.Low : c.High), c.Volume, step, digits), step);
        // Сегмент 3: от второго экстремума до Close
        res = AddDistribution(res, GetPriceDeviation((c.Close <= c.Open ? c.Low : c.High), c.Close, c.Volume, step, digits), step);
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
