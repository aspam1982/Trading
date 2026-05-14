using CommonClasses;
using Google.Protobuf.WellKnownTypes;
using Newtonsoft.Json;
using OpenTK.Compute.OpenCL;
using ScottPlot;
using ScottPlot.MultiplotLayouts;
using ScottPlot.Plottables;
using ScottPlot.WPF;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Tinkoff.InvestApi;
using Tinkoff.InvestApi.V1;

namespace RoboFutureArbitr
{
    /// <summary>
    /// Окно анализа собранной истории арбитражных отклонений.
    /// Загружает TradesHistory.json, фильтрует сделки по минимальному отклонению,
    /// комиссии, времени и размеру сделки, а затем строит графики по выбранному фьючерсу.
    /// </summary>
    public partial class DataPresenter : Window, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private void NotifyPropertyChanged([CallerMemberName] String propertyName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
        private static string token = WindowsCredentialManager.ReadSecret(Properties.Settings.Default.ApiKey)??"key not found";
        InvestApiClient client = InvestApiClientFactory.Create(token);
        private List<Future> futures = new List<Future>();
        private List<Share> shares = new List<Share>();
        private FuturePresenter selectedFuture;
        public string SummaryInfo { get => $@"Количество инструментов {FuturesList.Count()}
Всего коротких {FuturesList.Sum(u => u.AllSellMoney):C}
Всего длинных {FuturesList.Sum(u => u.AllBuyMoney):C}
Сумма {FuturesList.Sum(u => u.AllBuyMoney + u.AllSellMoney):C}
Максимальная прибыль в коротких {FuturesList.Sum(u => (u.SellMoney) * Math.Max(0d, MinDeviation - MinCommision) / 100d):C}
Максимальная прибыль в длинных {FuturesList.Sum(u => (u.BuyMoney) * Math.Max(0d, MinDeviation - MinCommision) / 100d):C}
Максимальная прибыль {FuturesList.Sum(u => (u.BuyMoney + u.SellMoney)*Math.Max(0d,MinDeviation - MinCommision) / 100d):C}
Максимальный депозит в коротких {(FuturesList.Any() ? FuturesList.Max(u => (u.DepoDemandSell)) : 0d):C}
Максимальная депозит в длинных {(FuturesList.Any() ? FuturesList.Max(u => (u.DepoDemandBuy)) : 0d):C}"; }

        public static double mincommision = 0.2d;
        public double MinCommision
        {
            get => mincommision;
            set
            {
                mincommision = value;
                NotifyPropertyChanged();
            }
        }
        public static double maxdealmoney = 500000;
        public double MaxDealMoney
        {
            get => maxdealmoney;
            set
            {
                maxdealmoney = value;
                NotifyPropertyChanged();
            }
        }
        public static DateTime timeFrom = DateTime.MinValue;

        public DateTime TimeFrom
        {
            get => timeFrom; set { if (value < timeTo) { timeFrom = value; NotifyPropertyChanged(); } }
        }
        public static DateTime timeTo = DateTime.MaxValue;

        public DateTime TimeTo
        {
            get => timeTo; set { if (value > timeFrom) { timeTo = value; NotifyPropertyChanged(); } }
        }
        public FuturePresenter SelectedFuture
        {
            get => selectedFuture;
            set
            { 
                selectedFuture = value;
                if (selectedFuture != null)
                {
                    PlotContainer.Children.Clear();
                    WpfPlot plot = new WpfPlot();
                    PlotContainer.Children.Add(plot);

/*                    var firsttime = PreparedData.First().Data.Trade.Time.ToUniversalTime();
                    var dfrom = firsttime.AddDays(-4);
                    var dto = firsttime.ToUniversalTime();
                    HistoricalTimeFrame tf = HistoricalTimeFrame.M1;
                    var fdata = new HistoricalData(selectedFuture.Future.Ticker, selectedFuture.Future.Figi, tf, new HistoricalData.QueryDataDelegate((figi, timeframe, datefrom, dateto) => {
                        return client.MarketData.GetCandles(new GetCandlesRequest() { InstrumentId = selectedFuture.Future.Uid, From = datefrom.ToTimestamp(), To = dateto.ToTimestamp(), Interval = Helper.HistoricalTimeFrameToCandleInterval(tf)}).Candles.Where(o => o.IsComplete).Select(u => new HistoricalCandle(u)).ToList();
                    }));
                    var fcandles = fdata.GetData(dfrom, dto);
                    var SelectedShare = tickershares[selectedFuture.Future.BasicAsset];
                    var sdata = new HistoricalData(SelectedShare.Ticker, SelectedShare.Figi, tf, new HistoricalData.QueryDataDelegate((figi, timeframe, datefrom, dateto) => {
                        return client.MarketData.GetCandles(new GetCandlesRequest() { InstrumentId = SelectedShare.Uid, From = datefrom.ToTimestamp(), To = dateto.ToTimestamp(), Interval = Helper.HistoricalTimeFrameToCandleInterval(tf) }).Candles.Where(o => o.IsComplete).Select(u => new HistoricalCandle(u)).ToList();
                    }));
                    var scandles = sdata.GetData(dfrom, dto);
                    var divs = client.Instruments.GetDividends(new GetDividendsRequest { From = dfrom.AddDays(-30).ToTimestamp(), InstrumentId = SelectedShare.Uid, To = dto.AddDays(30).ToTimestamp() }).Dividends;
                    if (divs.Any(u => u.LastBuyDate.ToDateTime() < firsttime))
                    {
                        var div = divs.FirstOrDefault();
                        var divdate = div.LastBuyDate.ToDateTime();
                        var discount = Helper.FromMoneyValue(div.ClosePrice) * Helper.FromQuotation(div.YieldValue) / 100d;
                        var candlesformoification = scandles.Where(u => u.Time < divdate).ToList();
                        candlesformoification.ForEach(u => { u.Open -= discount; u.Close -= discount; u.High -= discount; u.Low -= discount; });
                    }
                    var coeff = LinearInterpolator.FromCandles(scandles.Where(u => u.Time < firsttime).ToList(), fcandles.Where(u => u.Time < firsttime).ToList(), 200, 200);*/
                    // На график попадает только выбранный фьючерс и заданный пользователем временной интервал.
                    var currdata = PreparedData.Where(u => u.Future.Ticker == selectedFuture.Future.Ticker && u.Data.Trade.Time.ToLocalTime() >= TimeFrom && u.Data.Trade.Time.ToLocalTime() <= TimeTo);
                    var currdata1 = currdata.Where(u => u.PreferredDirection == "FutureSell");
                    double[] xs1 = currdata1.Select(u => u.Data.Trade.Time.ToLocalTime().ToOADate()).ToArray();
                    double[] ys1 = currdata1.Select(u => u.TradeDeviationPercent).ToArray();
                    double[] vols1 = currdata1.Select(u => u.MoneyVolume).ToArray();

                    var currdata2 = currdata.Where(u => u.PreferredDirection == "FutureBuy");
                    double[] xs2 = currdata2.Select(u => u.Data.Trade.Time.ToLocalTime().ToOADate()).ToArray();
                    double[] ys2 = currdata2.Select(u => u.TradeDeviationPercent).ToArray();
                    double[] vols2 = currdata2.Select(u => u.MoneyVolume).ToArray();
                    plot.Multiplot.AddPlots(3);
                    
                    var span1 = plot.Multiplot.GetPlot(0).Add.VerticalSpan(-mindeviation, mindeviation, ScottPlot.Colors.Green.WithAlpha(50));
                    var span2 = plot.Multiplot.GetPlot(0).Add.VerticalSpan(-mincommision, mincommision, ScottPlot.Colors.Red.WithAlpha(25));
                    var sc01 = plot.Multiplot.GetPlot(0).Add.Scatter(xs1, ys1);
                    sc01.Color = ScottPlot.Colors.Red.WithAlpha(127);
                    //sc01.MarkerShape = MarkerShape.None;
                    var sc11 = plot.Multiplot.GetPlot(1).Add.Scatter(xs1, vols1);
                    sc11.Color = ScottPlot.Colors.Red.WithAlpha(127);

                    plot.Multiplot.GetPlot(0).Title($"Отклонение в %");
                    plot.Multiplot.GetPlot(1).Title($"Сумма сделки в рублях");

                    
                    //var span2 = plot.Multiplot.GetPlot(1).Add.VerticalSpan(-mindeviation, mindeviation, ScottPlot.Colors.Green.WithAlpha(50));
                    var sc02 = plot.Multiplot.GetPlot(0).Add.Scatter(xs2, ys2);
                    sc02.Color = ScottPlot.Colors.Blue.WithAlpha(127);
                    //sc012.MarkerShape = MarkerShape.None;
                    var sc12 = plot.Multiplot.GetPlot(1).Add.Scatter(xs2, vols2);
                    sc12.Color = ScottPlot.Colors.Blue.WithAlpha(127);

                    double[] xs3 = currdata.Select(u => u.Data.Trade.Time.ToLocalTime().ToOADate()).ToArray();
                    double[] ys3 = currdata.Select(u => 
                    {
                        return u.Data.AverageDeviation;
                    }).ToArray();
                    double[] ys4 = currdata.Select(u =>
                    {
                        return u.Data.AverageDeviationSell;
                    }).ToArray();
                    double[] ys5 = currdata.Select(u => u.Data.DeviationLong).ToArray();
                    double[] ys6 = currdata.Select(u => u.Data.DeviationShort).ToArray();
                    var sc05 = plot.Multiplot.GetPlot(2).Add.Scatter(xs3, ys3);
                    var sc06 = plot.Multiplot.GetPlot(2).Add.Scatter(xs3, ys4);
                    var sc07 = plot.Multiplot.GetPlot(2).Add.Scatter(xs3, ys5);
                    var sc08 = plot.Multiplot.GetPlot(2).Add.Scatter(xs3, ys6);
                    sc05.Color = ScottPlot.Colors.Green;
                    sc05.MarkerShape = MarkerShape.None;
                    sc06.Color = ScottPlot.Colors.Orange;
                    sc06.MarkerShape = MarkerShape.None;
                    sc07.Color = ScottPlot.Colors.Blue;
                    sc07.MarkerShape = MarkerShape.None;
                    sc08.Color = ScottPlot.Colors.Red;
                    sc08.MarkerShape = MarkerShape.None;

                    //plot.Multiplot.GetPlot(1).Title($"Отклонение в % лонг");
                    //plot.Multiplot.GetPlot(3).Title($"Сумма сделки в рублях лонг");

                    plot.Multiplot.Layout = new ScottPlot.MultiplotLayouts.Grid(3, 1);
                    plot.Multiplot.SharedAxes.ShareX(plot.Multiplot.GetPlots());
                    plot.Multiplot.GetPlot(0).Axes.DateTimeTicksBottom();
                    plot.Multiplot.GetPlot(1).Axes.DateTimeTicksBottom();
                    plot.Multiplot.GetPlot(2).Axes.DateTimeTicksBottom();
                    //plot.Multiplot.GetPlot(2).Axes.DateTimeTicksBottom();
                    //plot.Multiplot.GetPlot(3).Axes.DateTimeTicksBottom();
                    PixelPadding padding = new(left: 100, right: 10, bottom: 50, top: 50);
                    foreach (var p in plot.Multiplot.GetPlots())
                        p.Layout.Fixed(padding);

                    var MyCrosshair = plot.Multiplot.GetPlot(0).Add.Crosshair(0, 0);
                    MyCrosshair.IsVisible = false;
                    MyCrosshair.VerticalLine.IsVisible = false;
                    MyCrosshair.HorizontalLine.IsVisible = false;
                    MyCrosshair.MarkerShape = MarkerShape.OpenCircle;
                    MyCrosshair.MarkerSize = 15;
                    Annotation ano = null;
                    Annotation ano1 = null;

                    plot.MouseMove += (s, e) =>
                    {
                        // determine where the mouse is and get the nearest point
                        var pos = e.GetPosition(plot);
                        var plt = plot.Multiplot.GetPlot(0);
                        Pixel mousePixel = new(pos.X, pos.Y);
                        Coordinates mouseLocation = plt.GetCoordinates(mousePixel);
                        DataPoint nearest1 = sc01.Data.GetNearest(mouseLocation, plt.LastRender);//rbNearestXY.Checked
                        DataPoint nearest2 = sc02.Data.GetNearest(mouseLocation, plt.LastRender);//rbNearestXY.Checked
                        /*    ? sc02.Data.GetNearest(mouseLocation, plt.LastRender)
                            : sc02.Data.GetNearestX(mouseLocation, plt.LastRender);*/
                        // place the crosshair over the highlighted point
                        if (ano != null)
                            plt.Remove(ano);
                        if (ano1 != null)
                            plt.Remove(ano1);
                        if (nearest1.IsReal)
                        {
                            MyCrosshair.IsVisible = true;
                            MyCrosshair.Position = nearest1.Coordinates;
                            MyCrosshair.MarkerColor = ScottPlot.Colors.Red;
                            var data = PreparedData.First(u => u.Future.Ticker == SelectedFuture.Future.Ticker && u.PreferredDirection == "FutureSell" && u.Data.Trade.Time.ToLocalTime().ToOADate() == nearest1.X && u.TradeDeviationPercent == nearest1.Y);
                            var book = data.Data.OrderBooks.First(u => u.Ticker == SelectedFuture.Future.Ticker);
                            string text = $"[{book.Ticker}] Direction [{(data.Data.Trade.IsBuy ? "Buy" : "Sell")}] Price = {data.Data.Trade.Price} Quontity = {data.Data.Trade.Quontity} at {data.Data.Trade.Time.ToLocalTime()} \r\n---------------------------\r\n";
                            var stackbid = book.Entries.Where(u => u.Quontity < 0).OrderByDescending(u => u.Price);
                            var stackask = book.Entries.Where(u => u.Quontity > 0).OrderBy(u => u.Price);
                            for (int i = 0; i < Math.Min(stackbid.Count(), stackask.Count());i++)
                                text += $"{stackbid.ElementAt(i)}   {stackask.ElementAt(i)} \r\n";
                            plot.Refresh();
                            ano = plt.Add.Annotation(text, Alignment.UpperLeft);

                            book = data.Data.OrderBooks.First(u => u.Ticker != SelectedFuture.Future.Ticker);
                            text = $"[{book.Ticker}]\r\n---------------------------\r\n";
                            stackbid = book.Entries.Where(u => u.Quontity < 0).OrderByDescending(u => u.Price);
                            stackask = book.Entries.Where(u => u.Quontity > 0).OrderBy(u => u.Price);
                            for (int i = 0; i < Math.Min(stackbid.Count(), stackask.Count()); i++)
                                text += $"{stackbid.ElementAt(i)}   {stackask.ElementAt(i)} \r\n";
                            ano1 = plt.Add.Annotation(text, Alignment.UpperRight);
                        }
                        else if (nearest2.IsReal)
                        {
                            MyCrosshair.IsVisible = true;
                            MyCrosshair.Position = nearest2.Coordinates;
                            MyCrosshair.MarkerColor = ScottPlot.Colors.Blue;

                            var data = PreparedData.First(u => u.Future.Ticker == SelectedFuture.Future.Ticker && u.PreferredDirection == "FutureBuy" && u.Data.Trade.Time.ToLocalTime().ToOADate() == nearest2.X && u.TradeDeviationPercent == nearest2.Y);
                            var book = data.Data.OrderBooks.First(u => u.Ticker == SelectedFuture.Future.Ticker);
                            string text = $"Direction [{(data.Data.Trade.IsBuy ? "Buy" : "Sell")}] Price = {data.Data.Trade.Price} Quontity = {data.Data.Trade.Quontity} at {data.Data.Trade.Time.ToLocalTime()} \r\n---------------------------\r\n";
                            var stackbid = book.Entries.Where(u => u.Quontity < 0).OrderByDescending(u => u.Price);
                            var stackask = book.Entries.Where(u => u.Quontity > 0).OrderBy(u => u.Price);
                            for (int i = 0; i < Math.Min(stackbid.Count(), stackask.Count()); i++)
                                text += $"{stackbid.ElementAt(i)}   {stackask.ElementAt(i)} \r\n";
                            plot.Refresh();
                            ano = plt.Add.Annotation(text);

                            book = data.Data.OrderBooks.First(u => u.Ticker != SelectedFuture.Future.Ticker);
                            text = $"[{book.Ticker}]\r\n---------------------------\r\n";
                            stackbid = book.Entries.Where(u => u.Quontity < 0).OrderByDescending(u => u.Price);
                            stackask = book.Entries.Where(u => u.Quontity > 0).OrderBy(u => u.Price);
                            for (int i = 0; i < Math.Min(stackbid.Count(), stackask.Count()); i++)
                                text += $"{stackbid.ElementAt(i)}   {stackask.ElementAt(i)} \r\n";
                            ano1 = plt.Add.Annotation(text, Alignment.UpperRight);
                        }
                        else
                        // hide the crosshair when no point is selected
                        if (MyCrosshair.IsVisible)
                        {
                            MyCrosshair.IsVisible = false;
                            plot.Refresh();
                            //Text = $"No point selected";
                        }
                    };
                    plot.Refresh();

                }
                else
                    PlotContainer.Children.Clear();
                NotifyPropertyChanged();
            }
        }
        public List<Share> Shares 
        { 
            get => shares; 
            set => shares = value; 
        }
        public List<Future> Futures 
        { 
            get => futures;
            set
            {
                futures = value;
                NotifyPropertyChanged();
                NotifyPropertyChanged("FuturesList");
            }
        }
        private List<FuturePresenter> futureslist = null;
        public List<FuturePresenter> FuturesList
        {
            get
            {
                if (futureslist == null)
                {
                    var res = Futures.Select(u => new FuturePresenter { Future = u }).Select(u => { u.CalcAllMoney(); return u; }).ToList();
                    futureslist = res.Where(u => u.SellMoney + u.BuyMoney > 0).OrderByDescending(u => u.BuyMoney + u.SellMoney).ToList();
                }
                return futureslist;
            }
        }
        public static double mindeviation = 0.4d;
        public double MinDeviation 
        { 
            get => mindeviation;
            set 
            { 
                mindeviation = value;
                NotifyPropertyChanged();
            } 
        }
        public static List<TradePreparedData> PreparedData = new List<TradePreparedData>();
        Dictionary<string, object> AggregatedData;
        private Dictionary<string, Future> tickerfutures = new Dictionary<string, Future>();
        private Dictionary<string, Share> tickershares = new Dictionary<string, Share>();

        public DataPresenter()
        {
            this.PropertyChanged += DataPresenter_PropertyChanged;
            DataContext = this;
            InitializeComponent();
            LoadFile("TradesHistory.json");
        }
        private void LoadFile(string filename)
        {
            Shares = client.Instruments.Shares().Instruments.Where(u => u.ApiTradeAvailableFlag && u.BuyAvailableFlag && u.SellAvailableFlag).ToList();
            Futures = client.Instruments.Futures().Instruments.Where(u => u.ApiTradeAvailableFlag && u.ShortEnabledFlag && u.BuyAvailableFlag && u.SellAvailableFlag).ToList();
            Futures = Futures.Where(u => shares.Select(s => s.Ticker).Contains(u.BasicAsset)).ToList();
            Shares = Shares.Where(u => futures.Select(f => f.BasicAsset).Contains(u.Ticker)).ToList();
            //try
            {
                var json = File.ReadAllText(filename);
                var data = JsonConvert.DeserializeObject<List<HistoricalTradeDataForAnalysis>>("[" + json + "]");
                var tickers = data.Select(u => u.Trade.Ticker).Distinct();
                var basicassets = Futures.Where(u => tickers.Contains(u.Ticker)).Select(u => u.BasicAsset).Distinct().ToList();
                var sids = Shares.Where(u => basicassets.Contains(u.Ticker)).Select(u => new { Ticker = u.Ticker, Figi = u.Figi, Uid = u.Uid }).ToList();
                var fids = Futures.Where(u => tickers.Contains(u.Ticker)).Select(f => new { Ticker = f.Ticker, Figi = f.Figi, Uid = f.Uid }).ToList();

                var idsd = sids.Concat(fids).Select(u => new KeyValuePair<string, Tuple<string, string>>(u.Ticker, new Tuple<string, string>(u.Figi, u.Uid))).ToList();
                var ids = idsd.ToDictionary();
                tickerfutures = tickers.Select(u => new KeyValuePair<string, Future>(u, futures.FirstOrDefault(f => f.Ticker == u))).ToDictionary();
                tickerfutures = tickerfutures.Where(u => u.Value != null).ToDictionary();
                data = data.Where(u => tickerfutures.ContainsKey(u.Trade.Ticker)).ToList();
                tickershares = shares.Select(u => new KeyValuePair<string, Share>(u.Ticker, shares.FirstOrDefault(f => f.Ticker == u.Ticker))).ToDictionary();
                Futures = futures.Where(u => tickers.Contains(u.Ticker)).ToList();
                Dictionary<string, List<HistoricalCandle>> candles = new Dictionary<string, List<HistoricalCandle>>();

                timeFrom = data.Min(u => u.Trade.Time);
                timeTo = data.Max(u => u.Trade.Time);
                var dfrom = timeFrom.AddHours(-1);
                var dto = timeTo.AddHours(1);
                timeFrom = timeFrom.ToLocalTime();
                timeTo = timeTo.ToLocalTime();
                HistoricalTimeFrame tf = HistoricalTimeFrame.M1;
                foreach (var t in ids)
                {
                    var fdata = new HistoricalData(t.Key, t.Value.Item1, tf, new HistoricalData.QueryDataDelegate((figi, timeframe, datefrom, dateto) => {
                        return client.MarketData.GetCandles(new GetCandlesRequest()
                        {
                            InstrumentId = t.Value.Item2,
                            From = datefrom.ToTimestamp(),
                            To = dateto.ToTimestamp(),
                            Interval = Helper.HistoricalTimeFrameToCandleInterval(tf)
                        }).Candles.Where(o => o.IsComplete).Select(u => new HistoricalCandle(u)).ToList();
                    }));
                    candles.Add(t.Key, fdata.GetData(dfrom, dto));
                    /*if (fdata.DataHasChanges)
                        fdata.SaveHistoricalData();*/
                }
                foreach (var t in tickerfutures)
                {
                    var trades = data.Where(u => u.Trade.Ticker == t.Key).ToList();
                    var fcandles = candles[t.Key];
                    var scandles = candles[t.Value.BasicAsset];
                    var pairs = Helper.GetHistoricalCandlesPairs(scandles, fcandles).ToList();
                    int i = 0; int ii = 0;
                    Queue<Tuple<HistoricalCandle, HistoricalCandle>> queue60 = new Queue<Tuple<HistoricalCandle, HistoricalCandle>>();
                    Queue<Tuple<HistoricalCandle, HistoricalCandle>> queue120 = new Queue<Tuple<HistoricalCandle, HistoricalCandle>>();
                    while (i < trades.Count)
                    {
                        var trade = trades[i];
                        while (ii < pairs.Count && trade.Trade.Time > pairs[ii].Item1.Time)
                            ii++;
                        if (ii < pairs.Count())
                        {
                            var pair = pairs[ii];
                            if (!queue60.Contains(pair))
                            {
                                queue60.Enqueue(pair);
                                queue120.Enqueue(pair);
                                if (queue60.Count > Properties.Settings.Default.AVGBuyCandlesCount)
                                    queue60.Dequeue();
                                if (queue120.Count > Properties.Settings.Default.AVGSellCandlesCount)
                                    queue120.Dequeue();
                            }
                        }
                        trade.AverageDeviation = queue60.Average(u => u.Item1.Price / u.Item2.Price);
                        trade.AverageDeviationSell = queue120.Average(u => u.Item1.Price / u.Item2.Price);
                        i++;
                    }
                }

                /*Dictionary<string, double> avgdev = new Dictionary<string, double>();
                Dictionary<string, long> avgcnt = new Dictionary<string, long>();
                Dictionary<string, double> lastdev = new Dictionary<string, double>();
                Dictionary<string, Queue<HistoricalTradeDataForAnalysis>> Queues = new Dictionary<string, Queue<HistoricalTradeDataForAnalysis>>();
                foreach (var d in data)
                {
                    var ticker = d.Trade.Ticker;
                    if (!Queues.ContainsKey(ticker))
                        Queues.Add(ticker, new Queue<HistoricalTradeDataForAnalysis>());
                    var queue = Queues[ticker];
                    queue.Enqueue(d);
                    if (queue.Count > 100)
                        queue.Dequeue();
                    d.AverageDeviation = queue.Average(u => u.DeviationShort + u.DeviationLong) / 2d;
                }*/
                PreparedData = data.Select(u => {
                    var futurebook = u.OrderBooks.First(b => b.Ticker == u.Trade.Ticker);
                    var sharebook = u.OrderBooks.First(b => b.Ticker != u.Trade.Ticker);
                    var future = tickerfutures[futurebook.Ticker];
                    var share = tickershares[sharebook.Ticker];
                    var shareask = sharebook.Entries.FirstOrDefault(u => u.Quontity > 0);
                    var sharebid = sharebook.Entries.LastOrDefault(u => u.Quontity < 0);
                    var tradedeviation = u.AverageDeviation;
                    var tradedeviationpercent = 0d;
                    bool FutureTradeDirectionIsLong = true;
                    if (u.Trade.IsBuy && shareask != null)
                    {
                        tradedeviation = shareask.Price / u.Trade.Price;
                        tradedeviationpercent = (tradedeviation - u.AverageDeviation) / u.AverageDeviation * 100d;
                        FutureTradeDirectionIsLong = false;
                    }
                    else if (!u.Trade.IsBuy && sharebid != null)
                    {
                        tradedeviation = sharebid.Price / u.Trade.Price;
                        tradedeviationpercent = (tradedeviation - u.AverageDeviation) / u.AverageDeviation * 100d;
                        FutureTradeDirectionIsLong = true;
                    }
                    var MoneyVolume = u.Trade.Price * u.Trade.Quontity / Helper.FromQuotation(future.MinPriceIncrement) * (future.MinPriceIncrementAmount == null ? 1d : Helper.FromQuotation(future.MinPriceIncrementAmount));
                    return new TradePreparedData
                    {
                        Data = u,
                        Future = future,
                        Share = share,
                        MoneyVolume = MoneyVolume,
                        PreferredDirection = u.Trade.IsBuy ? "FutureSell" : "FutureBuy",
                        TradeDeviation = tradedeviation,
                        TradeDeviationPercent = tradedeviationpercent
                    };
                }).ToList();
            }
            /*catch (Exception ex)
            {
            }*/
            futureslist = null;
            NotifyPropertyChanged(nameof(TimeFrom));
            NotifyPropertyChanged(nameof(TimeTo));
            NotifyPropertyChanged(nameof(FuturesList));
        }
        private void DataPresenter_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MinDeviation) || e.PropertyName == nameof(MinCommision) || e.PropertyName == nameof(MaxDealMoney) || e.PropertyName == nameof(TimeFrom) || e.PropertyName == nameof(TimeTo))
            {
                futureslist = null;
                NotifyPropertyChanged(nameof(FuturesList));
            }
            else if (e.PropertyName == nameof(FuturesList))
                NotifyPropertyChanged(nameof(SummaryInfo));
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog ofd = new OpenFileDialog();
            ofd.Filter = "JSON file|*.json";
            ofd.CheckFileExists = true;
            ofd.CheckPathExists = true;
            if (ofd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                LoadFile(ofd.FileName);
            }
        }

        private void TextBlock_Loaded(object sender, RoutedEventArgs e)
        {
            var textBlock = e.OriginalSource as TextBlock;
            string text = textBlock.Text;
            Regex b = new Regex(@"(<b>([\s\S]*?)<\/b>)|(<red>([\s\S]*?)<\/red>)|(<green>([\s\S]*?)<\/green>)|(<blue>([\s\S]*?)<\/blue>)", RegexOptions.IgnoreCase | RegexOptions.Multiline);
            var mc = b.Matches(text);
            int lastidx = 0;
            textBlock.Text = "";
            foreach(Match m in mc)
            {
                if (m.Index != lastidx)
                    textBlock.Inlines.Add(new Run(text.Substring(lastidx, m.Index - lastidx)));
                if (m.Groups[2].Success)
                textBlock.Inlines.Add(new Bold(new Run(text.Substring(m.Groups[2].Index, m.Groups[2].Length))));
                else if (m.Groups[4].Success)
                    textBlock.Inlines.Add(new Span(new Run(text.Substring(m.Groups[4].Index, m.Groups[4].Length)) { Foreground = new SolidColorBrush(System.Windows.Media.Colors.Red)}));
                else if (m.Groups[6].Success)
                    textBlock.Inlines.Add(new Span(new Run(text.Substring(m.Groups[6].Index, m.Groups[6].Length)) { Foreground = new SolidColorBrush(System.Windows.Media.Colors.Green) }));
                else if (m.Groups[8].Success)
                    textBlock.Inlines.Add(new Span(new Run(text.Substring(m.Groups[8].Index, m.Groups[8].Length)) { Foreground = new SolidColorBrush(System.Windows.Media.Colors.Blue) }));
                lastidx = m.Index + m.Length;
            }
            textBlock.Inlines.Add(new Run(text.Substring(lastidx, text.Length - lastidx)));
        }

        private void Button_Click_1(object sender, RoutedEventArgs e)
        {
            timeFrom = timeFrom.Date.AddHours(20);
            timeTo = timeFrom.Date.AddHours(21);
            NotifyPropertyChanged(nameof(TimeFrom));
            NotifyPropertyChanged(nameof(TimeTo));
        }
    }
    public class TradePreparedData
    {
        public HistoricalTradeDataForAnalysis Data { get; set; }
        public string PreferredDirection { get; set; }
        public Future Future { get; set; }
        public Share Share { get; set; }
        public double TradeDeviation { get; set; }
        public double TradeDeviationPercent { get; set; }
        public double MoneyVolume { get; set; }
    }
    public class FuturePresenter:INotifyPropertyChanged
    {
        private Future future;
        public Future Future 
        { 
            get => future; 
            set 
            { 
                future = value;
                buymoney = null;
                sellmoney = null;
                allbuymoney = null;
                allsellmoney = null;
                depodemandbuy = null;
                depodemandsell = null;
                NotifyPropertyChanged();
                NotifyPropertyChanged("DisplayString");
            } 
        }
        private double? sellmoney;
        private double? buymoney;
        private double? depodemandsell;
        private double? depodemandbuy;
        private double? allsellmoney;
        private double? allbuymoney;

        public double DepoDemandBuy
        {
            get
            {
                if (!depodemandbuy.HasValue)
                    CalcAllMoney();
                return depodemandbuy.HasValue ? depodemandbuy.Value : 0;
            }
        }
        public double DepoDemandSell
        {
            get
            {
                if (!depodemandsell.HasValue)
                    CalcAllMoney();
                return depodemandsell.HasValue ? depodemandsell.Value : 0;
            }
        }
        public double BuyMoney
        {
            get
            {
                if (!buymoney.HasValue)
                    CalcAllMoney();
                return buymoney.HasValue ? buymoney.Value : 0;
            }
        }
        public double SellMoney
        {
            get
            {
                if (!sellmoney.HasValue)
                    CalcAllMoney();
                return sellmoney.HasValue ? sellmoney.Value : 0;
            }
        }
        public double AllBuyMoney
        {
            get
            {
                if (!allbuymoney.HasValue)
                    CalcAllMoney();
                return allbuymoney.HasValue ? allbuymoney.Value : 0;
            }
        }
        public double AllSellMoney
        {
            get
            {
                if (!allsellmoney.HasValue)
                    CalcAllMoney();
                return allsellmoney.HasValue ? allsellmoney.Value : 0;
            }
        }
        public void CalcAllMoney()
        {
            depodemandsell = 0;
            depodemandbuy = 0;
            allsellmoney = 0;
            allbuymoney = 0;
            sellmoney = 0;
            buymoney = 0;
            {
                TradePreparedData firsttrade = null;
                double depodemand = 0d;
                foreach (var d in DataPresenter.PreparedData.Where(u => u.Future.Ticker == future.Ticker && u.PreferredDirection == "FutureSell" && u.Data.Trade.Time.ToLocalTime() >= DataPresenter.timeFrom && u.Data.Trade.Time.ToLocalTime() <= DataPresenter.timeTo))
                {
                    allsellmoney += d.MoneyVolume;
                    if (d.TradeDeviationPercent < -DataPresenter.mindeviation)
                    {
                        if (firsttrade == null)
                        {
                            depodemand = 0d;
                            firsttrade = d;
                        }
                        else
                        {
                            if (d.Data.Trade.Time - firsttrade.Data.Trade.Time > new TimeSpan(0, 0, 1))
                            {
                                /*if (firsttrade.Data.Trade.Time.Hour == 15 && firsttrade.Data.Trade.Time.Minute > 40 && d.Future.Ticker == "GAZPF")
                                {
                                    var b = true;
                                }*/
                                double maxmoneyvolume = Math.Min(d.MoneyVolume, DataPresenter.maxdealmoney);
                                double mvol = Math.Floor(maxmoneyvolume / d.Data.Trade.Price) * d.Data.OrderBooks.First(u => u.Ticker == d.Future.Ticker).Entries.Last(u => u.Quontity < 0).Price;
                                depodemand += mvol;
                                sellmoney += mvol;
                                depodemandsell = Math.Max(depodemandsell.Value, depodemand);
                            }
                        }
                    }
                    else
                        firsttrade = null;
                }
            }
            {
                TradePreparedData firsttrade = null;
                double depodemand = 0d;
                foreach (var d in DataPresenter.PreparedData.Where(u => u.Future.Ticker == future.Ticker && u.PreferredDirection == "FutureBuy" && u.Data.Trade.Time.ToLocalTime() >= DataPresenter.timeFrom && u.Data.Trade.Time.ToLocalTime() <= DataPresenter.timeTo))
                {
                    allbuymoney += d.MoneyVolume;
                    if (d.TradeDeviationPercent > DataPresenter.mindeviation)
                    {
                        if (firsttrade == null)
                        {
                            depodemand = 0d;
                            firsttrade = d;
                        }
                        else
                        {
                            if (d.Data.Trade.Time - firsttrade.Data.Trade.Time > new TimeSpan(0, 0, 1))
                            {
                                /*if (firsttrade.Data.Trade.Time.Hour == 15 && firsttrade.Data.Trade.Time.Minute > 40 && d.Future.Ticker == "GAZPF")
                                {
                                    var b = true;
                                }*/
                                double maxmoneyvolume = Math.Min(d.MoneyVolume, DataPresenter.maxdealmoney);
                                double mvol = Math.Floor(maxmoneyvolume / d.Data.Trade.Price) * d.Data.OrderBooks.First(u => u.Ticker == d.Future.Ticker).Entries.First(u => u.Quontity > 0).Price;
                                depodemand += mvol;
                                buymoney += mvol;
                                depodemandbuy = Math.Max(depodemandbuy.Value, depodemand);
                            }
                        }
                    }
                    else
                        firsttrade = null;
                }
            }
        }
        public string DisplayName { get => $@"<b>{Future.Ticker}</b> <green>[{Future.Name}]</green>
<b>All buy</b> <blue>{AllBuyMoney:C}</blue> <b>All sell</b> <red>{AllSellMoney:C}₽</red>
<b>Profitable buy</b> <blue>{BuyMoney:C}</blue> <b>Profitable sell</b> <red>{SellMoney:C}₽</red>
<b>Depo demand buy</b> <blue>{DepoDemandBuy:C}</blue> <b>Depo demand sell</b> <red>{DepoDemandSell:C}</red> 
<b>ProfitBuy</b> <blue>{BuyMoney * Math.Max(0d, DataPresenter.mindeviation - DataPresenter.mincommision) / 100d:C}</blue> <b>ProfitSell</b> <red>{SellMoney * Math.Max(0d, DataPresenter.mindeviation - DataPresenter.mincommision) / 100d:C}</red>";  }
        public override string ToString()
        {
            return DisplayName;
        }
        public event PropertyChangedEventHandler? PropertyChanged;
        private void NotifyPropertyChanged([CallerMemberName] String propertyName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
        
    }
}
