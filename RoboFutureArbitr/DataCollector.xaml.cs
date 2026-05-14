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
using CommonClasses;
using Tinkoff.InvestApi.V1;
using Grpc.Core;
using static Google.Rpc.Context.AttributeContext.Types;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Google.Protobuf.WellKnownTypes;
using System.Windows.Threading;
using System.Linq;
using System.Diagnostics;
using Newtonsoft.Json;
using System.IO;
using NLog.LayoutRenderers.Wrappers;
using System.Collections.ObjectModel;
using ScottPlot;

namespace RoboFutureArbitr
{
    /// <summary>
    /// Окно сбора данных для стратегии арбитража акция-фьючерс.
    /// Загружает список ликвидных пар из T-Invest, подписывается на последние цены,
    /// свечи, стаканы и сделки, а затем сохраняет наблюдения в TradesHistory.json.
    /// </summary>
    public partial class DataCollector : Window, INotifyPropertyChanged
    {
        private static string token = WindowsCredentialManager.ReadSecret(Properties.Settings.Default.ApiKey)??"key not found";
        InvestApiClient client = InvestApiClientFactory.Create(token);
        Account acc;
        List<Share> shares = new List<Share>();
        List<Future> futures = new List<Future>();
        List<string> uidsfuture = new List<string>();
        List<string> uidsshare = new List<string>();
        List<string> uids = new List<string>();
        Dictionary<string, List<string>> sharefutures = new Dictionary<string, List<string>>();
        Dictionary<string, string> futureshare = new Dictionary<string, string>();

        AsyncDuplexStreamingCall<MarketDataRequest, MarketDataResponse> marketdatastream;
        AsyncServerStreamingCall<OrderStateStreamResponse> ordersstream;

        public Dictionary<string, CommonClasses.LastPrice> LastPrices { get; set; } = new Dictionary<string, CommonClasses.LastPrice>();
        public Dictionary<string, OrderBook> LastOrderbooks { get; set; } = new Dictionary<string, OrderBook>();
        public Dictionary<string, Queue<HistoricalCandle>> AllCandles = new Dictionary<string, Queue<HistoricalCandle>>();
        public Dictionary<string, HistoricalCandle> LastCandles = new Dictionary<string, HistoricalCandle>();
        public ObservableCollection<ItemPresenter> lastpriceslist { get; } = new ObservableCollection<ItemPresenter>();
        private DateTime LastPricesUpdateTime = DateTime.MinValue;
        private void UpdateLastPricesList()
        {
            if (DateTime.Now - LastPricesUpdateTime < new TimeSpan(0, 0, 0, 0, 500))
                return;
            var items = LastPrices.Select(u => {
                var fut = futures.FirstOrDefault(f => f.Uid == u.Key);
                var sha = shares.FirstOrDefault(s => s.Uid == u.Key);
                if (fut != null)
                {
                    double dshort = double.NaN;
                    double dlong = double.NaN;

                    if (BuyDeviations.ContainsKey(fut.Uid))
                    {
                        OrderBook fp = null;
                        OrderBook sp = null;
                        if (LastOrderbooks.ContainsKey(fut.Uid))
                            fp = LastOrderbooks[fut.Uid];
                        if (LastOrderbooks.ContainsKey(futureshare[fut.Uid]))
                            sp = LastOrderbooks[futureshare[fut.Uid]];
                        if (fp != null && sp != null && fp.Asks.Any() && sp.Asks.Any())
                            dshort = (Helper.FromQuotation(sp.Asks.First().Price) / Helper.FromQuotation(fp.Asks.First().Price) - BuyDeviations[fut.Uid])/ BuyDeviations[fut.Uid];
                        if (fp != null && sp != null && fp.Bids.Any() && sp.Bids.Any())
                            dlong = (Helper.FromQuotation(sp.Bids.First().Price) / Helper.FromQuotation(fp.Bids.First().Price) - BuyDeviations[fut.Uid]) / BuyDeviations[fut.Uid];
                    }
                    return new KeyValuePair<string, string>(fut.Ticker, $@"{fut.Ticker} {u.Value.Price} at {u.Value.Time} dShort {dshort * 100d:0.###}% dLong {dlong * 100d:0.###}%");
                }
                else
                    return new KeyValuePair<string, string>(sha.Ticker, $@"{sha.Ticker} {u.Value.Price} at {u.Value.Time}");
            }).OrderBy(u => u.Key).Select(u => new ItemPresenter(u.Key, u.Value));
            foreach (var item in items)
            {
                var entry = lastpriceslist.FirstOrDefault(u => u.Key == item.Key);
                if (entry != null)
                    entry.Value = item.Value;
                else
                    lastpriceslist.Add(item);
            }
            var itemsforremove = lastpriceslist.Where(u => !items.Select(i => i.Key).Contains(u.Key)).ToList();
            itemsforremove.ForEach(u => lastpriceslist.Remove(u));
            NotifyPropertyChanged(nameof(lastpriceslist));
            LastPricesUpdateTime = DateTime.Now;
        }
        public ObservableCollection<ItemPresenter> lastcandleslist { get; } = new ObservableCollection<ItemPresenter>();
        private DateTime LastCandlesListUpdateTime = DateTime.MinValue;
        public void UpdateLastCandlesList()
        {
            if (DateTime.Now - LastCandlesListUpdateTime < new TimeSpan(0, 0, 0, 0, 500))
                return;
            var items = LastCandles.Select(u => {
                var fut = futures.FirstOrDefault(f => f.Uid == u.Key);
                var sha = shares.FirstOrDefault(s => s.Uid == u.Key);
                var candle = u;
                if (fut != null)
                    return new KeyValuePair<string, HistoricalCandle>(fut.Ticker, candle.Value
                        );
                else
                    return new KeyValuePair<string, HistoricalCandle>(sha.Ticker, candle.Value);
            }).OrderBy(u => u.Key).Select(u => new ItemPresenter (u.Key, $@"{u.Key} {u.Value}")).ToList();
            foreach (var item in items)
            {
                var entry = lastcandleslist.FirstOrDefault(u => u.Key == item.Key);
                if (entry != null)
                    entry.Value = item.Value;
                else
                    lastcandleslist.Add(item);
            }
            var itemsforremove = lastcandleslist.Where(u => !items.Select(i => i.Key).Contains(u.Key)).ToList();
            itemsforremove.ForEach(u => lastcandleslist.Remove(u));
            NotifyPropertyChanged(nameof(lastcandleslist));
            LastCandlesListUpdateTime = DateTime.Now;
        }
        public ObservableCollection<ItemPresenter> lastorderbookslist { get; } = new ObservableCollection<ItemPresenter>();
        private DateTime LastOrderBooksListUpdateTime = DateTime.MinValue;
        public void UpdateLastOrderBooksList()
        {
            if (DateTime.Now - LastOrderBooksListUpdateTime < new TimeSpan(0, 0, 0, 0, 500))
                return;
            var items = LastOrderbooks.Select(u => {
                var fut = futures.FirstOrDefault(f => f.Uid == u.Key);
                var sha = shares.FirstOrDefault(s => s.Uid == u.Key);
                if (fut != null)
                    return new KeyValuePair<string, OrderBook>(fut.Ticker, u.Value);
                else
                    return new KeyValuePair<string, OrderBook>(sha.Ticker, u.Value);
            }).OrderBy(u => u.Key).Select(u => new ItemPresenter(u.Key, $@"{u.Key} Spread = {(u.Value.Bids.Any() && u.Value.Asks.Any() ? Math.Floor(
                (Helper.FromQuotation(u.Value.Asks.First().Price) - Helper.FromQuotation(u.Value.Bids.First().Price))/ Helper.FromQuotation(u.Value.Bids.First().Price)*10000d)/100d : double.NaN)}%")).ToList();
            foreach (var item in items)
            {
                var entry = lastorderbookslist.FirstOrDefault(u => u.Key == item.Key);
                if (entry != null)
                    entry.Value = item.Value;
                else
                    lastorderbookslist.Add(item);
            }
            var itemsforremove = lastorderbookslist.Where(u => !items.Select(i => i.Key).Contains(u.Key)).ToList();
            itemsforremove.ForEach(u => lastorderbookslist.Remove(u));
            NotifyPropertyChanged(nameof(lastorderbookslist));
            LastOrderBooksListUpdateTime = DateTime.Now;
        }
        Timer tim;
        bool TimerProcessingIsRunning = false;
        public StreamWriter sw;
        public DataCollector()
        {
            DataContext = this;
            InitializeComponent();
            /*try
            {
                var json = File.ReadAllText("TradesHistory.json");
                var res = JsonConvert.DeserializeObject<List<HistoricalTradeDataForAnalysis>>("[" + json + "]");
            }
            catch (Exception ex)
            { }*/
            sw = File.AppendText("TradesHistory.json");

            Action InitializeData = () => {
                // Для анализа нужны только пары, где есть торгуемая акция и связанные с ней фьючерсы.
                acc = client.Users.GetAccounts().Accounts.First();
                shares = client.Instruments.Shares().Instruments.Where(u => u.ApiTradeAvailableFlag && u.BuyAvailableFlag && u.SellAvailableFlag).ToList();
                futures = client.Instruments.Futures().Instruments.Where(u => u.ApiTradeAvailableFlag && u.ShortEnabledFlag && u.BuyAvailableFlag && u.SellAvailableFlag).ToList();
                Dictionary<string, double> volumes = new Dictionary<string, double>();
                futures = futures.Where(u => shares.Select(s => s.Ticker).Contains(u.BasicAsset)).ToList();
                shares = shares.Where(u => futures.Select(f => f.BasicAsset).Contains(u.Ticker)).ToList();

                futures.ForEach(u =>
                {
                    var candles = client.MarketData.GetCandles(new GetCandlesRequest() { InstrumentId = u.Uid, From = DateTime.Now.AddDays(-14).ToUniversalTime().ToTimestamp(), To = DateTime.Now.ToUniversalTime().ToTimestamp(), Interval = CandleInterval.Day }).Candles.Where(o => o.IsComplete).ToList();
                    if (candles.Any())
                    {
                        double val = candles.Average(c => c.Volume) / Helper.FromQuotation(u.MinPriceIncrement) * Helper.FromQuotation(u.MinPriceIncrementAmount) * Helper.FromQuotation(candles.Last().Close);
                        volumes.Add(u.Ticker, val);
                    }
                });
                volumes = volumes.Where(u => u.Value > 50000000).ToDictionary();
                futures = futures.Where(u => volumes.ContainsKey(u.Ticker)).ToList();
                shares = shares.Where(u => futures.Select(f => f.BasicAsset).Contains(u.Ticker)).ToList();
                uidsshare = shares.Select(u => u.Uid).ToList();
                uidsfuture = futures.Select(u => u.Uid).ToList();
                sharefutures = new Dictionary<string, List<string>>();
                shares.ForEach(s => sharefutures.Add(s.Uid, futures.Where(f => f.BasicAsset == s.Ticker).Select(f => f.Uid).ToList()));
                futureshare = new Dictionary<string, string>();
                futures.ForEach(f => futureshare.Add(f.Uid, shares.First(u => u.Ticker == f.BasicAsset).Uid));
                uids = new List<string>();
                uids.AddRange(uidsshare);
                uids.AddRange(uidsfuture);

                LastPrices = new Dictionary<string, CommonClasses.LastPrice>();
                var glpreq = new GetLastPricesRequest();
                glpreq.InstrumentId.Add(uids);
                var lplist = client.MarketData.GetLastPrices(glpreq);
                foreach (var lp in lplist.LastPrices)
                {
                    if (lp.Price == null)
                        continue;
                    var fut = futures.FirstOrDefault(f => f.Figi == lp.Figi);
                    var sha = shares.FirstOrDefault(s => s.Figi == lp.Figi);
                    if (fut != null)
                        LastPrices.Add(fut.Uid, new CommonClasses.LastPrice(Helper.FromQuotation(lp.Price), lp.Time.ToDateTime()));
                    else
                        LastPrices.Add(sha.Uid, new CommonClasses.LastPrice(Helper.FromQuotation(lp.Price), lp.Time.ToDateTime()));
                }

                AllCandles = new Dictionary<string, Queue<HistoricalCandle>>();
                LastCandles = new Dictionary<string, HistoricalCandle>();
                foreach (var uid in uids)
                {
                    var fut = futures.FirstOrDefault(f => f.Uid == uid);
                    var sha = shares.FirstOrDefault(s => s.Uid == uid);
                    var figi = fut == null ? sha.Figi : fut.Figi;
                    var ticker = fut == null ? sha.Ticker : fut.Ticker;
                    var data = new HistoricalData(ticker, figi, HistoricalTimeFrame.M1, new HistoricalData.QueryDataDelegate((figi, tf, dfrom, dto) => { 
                        return client.MarketData.GetCandles(new GetCandlesRequest { 
                            InstrumentId = uid, 
                            From = dfrom.ToUniversalTime().ToTimestamp(), 
                            Limit = Properties.Settings.Default.MaxCandlesToStore, 
                            Interval = Helper.HistoricalTimeFrameToCandleInterval(tf), To = dto.ToUniversalTime().ToTimestamp() }).Candles.Select(u => new HistoricalCandle(u)).ToList();
                    }));
                    var clist = data.GetData(DateTime.Now.AddDays(-3), DateTime.Now);
                    Queue<HistoricalCandle> hd = new Queue<HistoricalCandle>();
                    clist.ForEach(u => hd.Enqueue(u));
                    if (clist.Any())
                    {
                        AllCandles.Add(uid, hd);
                        LastCandles.Add(uid, clist.Last());
                    }
                }
                ProcessAllCandles();
                UpdateLastCandlesList();
                UpdateLastPricesList();
                UpdateLastOrderBooksList();
            };
            Action RenewSubscriptions = () => {
                // Потоковые подписки дают онлайн-данные для расчета отклонений и сохранения истории.
                MarketDataStreamPingTime = null;
                ordersstream?.Dispose();
                var orsreq = new OrderStateStreamRequest();
                orsreq.Accounts.Add(acc.Id);
                ordersstream = client.OrdersStream.OrderStateStream(orsreq);

                marketdatastream?.Dispose();
                marketdatastream = client.MarketDataStream.MarketDataStream();
                var lpreq = new SubscribeLastPriceRequest() { SubscriptionAction = SubscriptionAction.Subscribe };
                lpreq.Instruments.AddRange(uids.Select(u => new LastPriceInstrument { InstrumentId = u }));
                marketdatastream.RequestStream.WriteAsync(
                    new Tinkoff.InvestApi.V1.MarketDataRequest()
                    {
                        SubscribeLastPriceRequest = lpreq
                    }).Wait();
                var creq = new SubscribeCandlesRequest() { SubscriptionAction = SubscriptionAction.Subscribe, WaitingClose = true };
                creq.Instruments.AddRange(uids.Select(u => new CandleInstrument { InstrumentId = u, Interval = SubscriptionInterval.OneMinute }));
                marketdatastream.RequestStream.WriteAsync(
                    new Tinkoff.InvestApi.V1.MarketDataRequest()
                    {
                        SubscribeCandlesRequest = creq
                    }).Wait();
                var breq = new SubscribeOrderBookRequest { SubscriptionAction = SubscriptionAction.Subscribe };
                breq.Instruments.AddRange(uids.Select(u => new OrderBookInstrument { InstrumentId = u, Depth = 10, OrderBookType = OrderBookType.All }));
                marketdatastream.RequestStream.WriteAsync(
                    new Tinkoff.InvestApi.V1.MarketDataRequest()
                    {
                        SubscribeOrderBookRequest = breq
                    }).Wait();
                var treq = new SubscribeTradesRequest() { SubscriptionAction = SubscriptionAction.Subscribe };
                treq.Instruments.AddRange(uidsfuture.Select(u => new TradeInstrument { InstrumentId = u }));
                marketdatastream.RequestStream.WriteAsync(
                    new Tinkoff.InvestApi.V1.MarketDataRequest()
                    {
                        SubscribeTradesRequest = treq
                    }).Wait();
                marketdatastream.RequestStream.WriteAsync(
                    new Tinkoff.InvestApi.V1.MarketDataRequest()
                    {
                        GetMySubscriptions = new GetMySubscriptions()
                    }).Wait();
                ProcessMarketData();
                ProcessOrdersData();
            };
            InitializeData();
            RenewSubscriptions();
            tim = new Timer(new TimerCallback((o) => {
                if (TimerProcessingIsRunning)
                    return;
                TimerProcessingIsRunning = true;
                if (MarketDataStreamPingTime.HasValue && DateTime.Now - MarketDataStreamPingTime.Value > TimeSpan.FromSeconds(10))
                    try
                    {
                        RenewSubscriptions();
                    }
                    catch (Exception ex)
                    {
                        MarketDataStreamPingTime = null;
                        Console.WriteLine(ex.ToString());
                    }
                else if (!MarketDataStreamPingTime.HasValue)
                {
                    MarketDataStreamPingTime = DateTime.Now;
                    try
                    {
                        marketdatastream?.RequestStream.WriteAsync(new MarketDataRequest { SubscribeInfoRequest = new SubscribeInfoRequest() }).Wait();
                    }
                    catch (Exception ex)
                    {
                        MarketDataStreamPingTime = DateTime.Now.AddMinutes(-1);
                        Console.WriteLine(ex.ToString());
                    }
                }
                TimerProcessingIsRunning = false;
            }), null, 5000, 5000);
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void NotifyPropertyChanged([CallerMemberName] String propertyName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
        DateTime? MarketDataStreamPingTime;
        public void ProcessAllCandles()
        {
            foreach (var t in uids)
                ProcessCandles(t);
        }
        public void ProcessCandles(string uid)
        {
            List<string> futuresforupdate = new List<string>();
            if (uidsshare.Contains(uid))
                futuresforupdate.AddRange(sharefutures[uid]);
            else
                futuresforupdate.Add(uid);
            foreach(var f in futuresforupdate)
            {
                var s = futureshare[f];
                if (AllCandles.ContainsKey(f) && AllCandles.ContainsKey(s))
                {
                    var fcandles = AllCandles[f];
                    var scandles = AllCandles[s];
                    var pairs = Helper.GetHistoricalCandlesPairs(scandles, fcandles);
                    if (pairs.Any())
                    {
                        var deviationbuy = pairs.TakeLast(Properties.Settings.Default.AVGBuyCandlesCount).Average(u => u.Item1.Price / u.Item2.Price);
                        var deviationsell = pairs.TakeLast(Properties.Settings.Default.AVGSellCandlesCount).Average(u => u.Item1.Price / u.Item2.Price);
                        if (BuyDeviations.ContainsKey(f))
                            BuyDeviations[f] = deviationbuy;
                        else
                            BuyDeviations.Add(f, deviationbuy);
                        if (SellDeviations.ContainsKey(f))
                            SellDeviations[f] = deviationsell;
                        else
                            SellDeviations.Add(f, deviationsell);
                    }
                }
            }
        }
        public Dictionary<string, double> BuyDeviations = new Dictionary<string, double>();
        public Dictionary<string, double> SellDeviations = new Dictionary<string, double>();

        public async Task ProcessMarketData()
        {
            await foreach (var resp in marketdatastream.ResponseStream.ReadAllAsync())
            {
                await Application.Current.Dispatcher.BeginInvoke(
                    DispatcherPriority.Background,
                    new Action(() =>
                    {
                        switch (resp.PayloadCase)
                        {
                            case MarketDataResponse.PayloadOneofCase.Candle:
                                if (resp.Candle.Interval == SubscriptionInterval.OneMinute)
                                {
                                    var c = new HistoricalCandle(resp.Candle);
                                    var uid = resp.Candle.InstrumentUid;
                                    var queue = AllCandles[uid];
                                    if (queue.Count >= Properties.Settings.Default.MaxCandlesToStore)
                                        queue.Dequeue();
                                    queue.Enqueue(c);
                                    if (!LastCandles.ContainsKey(uid))
                                        LastCandles.Add(uid, c);
                                    else
                                        LastCandles[uid] = c;
                                    ProcessCandles(uid);
                                    UpdateLastCandlesList();
                                }
                                break;
                            case MarketDataResponse.PayloadOneofCase.LastPrice:
                                if (LastPrices.ContainsKey(resp.LastPrice.InstrumentUid))
                                    LastPrices[resp.LastPrice.InstrumentUid] = new CommonClasses.LastPrice(Helper.FromQuotation(resp.LastPrice.Price), resp.LastPrice.Time.ToDateTime());
                                else
                                    LastPrices.Add(resp.LastPrice.InstrumentUid, new CommonClasses.LastPrice(Helper.FromQuotation(resp.LastPrice.Price), resp.LastPrice.Time.ToDateTime()));
                                UpdateLastPricesList();
                                break;
                            case MarketDataResponse.PayloadOneofCase.Trade:
                                var future = resp.Trade.InstrumentUid;
                                if (BuyDeviations.ContainsKey(future) && SellDeviations.ContainsKey(future))
                                {
                                    var share = futureshare[future];
                                    if (LastOrderbooks.ContainsKey(future) && LastOrderbooks.ContainsKey(share))
                                    {
                                        var futureob = LastOrderbooks[future];
                                        var shareob = LastOrderbooks[share];
                                        var futurehob = new HistoricalOrderBook 
                                        { 
                                            Time = futureob.Time.ToDateTime(),
                                            Ticker = futures.First(u => u.Uid == future).Ticker
                                        };
                                        futurehob.Entries.AddRange(futureob.Bids.Select(u => new HistoricalOrderBookEntry { Price = Helper.FromQuotation(u.Price), Quontity = -u.Quantity }).OrderBy(u => u.Price));
                                        futurehob.Entries.AddRange(futureob.Asks.Select(u => new HistoricalOrderBookEntry { Price = Helper.FromQuotation(u.Price), Quontity = u.Quantity }).OrderBy(u => u.Price));
                                        var sharehob = new HistoricalOrderBook
                                        {
                                            Time = shareob.Time.ToDateTime(),
                                            Ticker = shares.First(u => u.Uid == share).Ticker
                                        };
                                        sharehob.Entries.AddRange(shareob.Bids.Select(u => new HistoricalOrderBookEntry { Price = Helper.FromQuotation(u.Price), Quontity = -u.Quantity }).OrderBy(u => u.Price));
                                        sharehob.Entries.AddRange(shareob.Asks.Select(u => new HistoricalOrderBookEntry { Price = Helper.FromQuotation(u.Price), Quontity = u.Quantity }).OrderBy(u => u.Price));
                                        

                                        var deviationshort = double.NaN;
                                        var deviationlong = double.NaN;
                                        var dsp = double.NaN;
                                        var dlp = double.NaN;
                                        var averagedeviation = BuyDeviations[future];
                                        var averagedeviationsell = SellDeviations[future];
                                        if (futurehob.Entries.Where(u => u.Quontity > 0).Any() && sharehob.Entries.Where(u => u.Quontity > 0).Any())
                                        {
                                            var priceshare = sharehob.Entries.First(u => u.Quontity > 0).Price;
                                            var pricefuture = futurehob.Entries.First(u => u.Quontity > 0).Price;
                                            deviationshort = priceshare / pricefuture;
                                            dsp = (deviationshort - averagedeviation) / averagedeviation * 100d;
                                        }
                                        if (futurehob.Entries.Where(u => u.Quontity < 0).Any() && sharehob.Entries.Where(u => u.Quontity < 0).Any())
                                        {
                                            var priceshare = sharehob.Entries.Last(u => u.Quontity < 0).Price;
                                            var pricefuture = futurehob.Entries.Last(u => u.Quontity < 0).Price;
                                            deviationlong = priceshare / pricefuture;
                                            dlp = (deviationlong - averagedeviation) / averagedeviation * 100d;
                                        }
                                        var data = new HistoricalTradeDataForAnalysis
                                        {
                                            Trade = new HistoricalTrade
                                            {
                                                IsBuy = resp.Trade.Direction == TradeDirection.Buy,
                                                Price = Helper.FromQuotation(resp.Trade.Price),
                                                Quontity = resp.Trade.Quantity,
                                                Ticker = futures.First(u => u.Uid == future).Ticker,
                                                Time = resp.Trade.Time.ToDateTime()
                                            },
                                            DeviationLong = deviationlong,
                                            DeviationShort = deviationshort,
                                            DSP = dsp,
                                            DLP = dlp,
                                            AverageDeviation = averagedeviation,
                                            AverageDeviationSell = averagedeviationsell
                                        };
                                        data.OrderBooks.Add(futurehob);
                                        data.OrderBooks.Add(sharehob);

                                        sw.WriteLine(JsonConvert.SerializeObject(data) + ",");
                                        sw.Flush();
                                    }
                                }
                                break;
                            case MarketDataResponse.PayloadOneofCase.Orderbook:
                                if (LastOrderbooks.ContainsKey(resp.Orderbook.InstrumentUid))
                                    LastOrderbooks[resp.Orderbook.InstrumentUid] = resp.Orderbook;
                                else
                                    LastOrderbooks.Add(resp.Orderbook.InstrumentUid, resp.Orderbook);
                                UpdateLastOrderBooksList();
                                break;
                            default:
                                break;
                        }
                        MarketDataStreamPingTime = null;
                        Console.WriteLine(resp);
                    }));
            }
        }
        public async Task ProcessOrdersData()
        {
            await foreach (var resp in ordersstream.ResponseStream.ReadAllAsync())
            {
                switch (resp.PayloadCase)
                {
                    case OrderStateStreamResponse.PayloadOneofCase.OrderState:
                        break;
                    default:
                        break;
                }
                MarketDataStreamPingTime = null;
                Console.WriteLine(resp);
            }
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {
            marketdatastream.RequestStream.WriteAsync(
            new Tinkoff.InvestApi.V1.MarketDataRequest()
            {
                //Ping = new PingRequest(),
                GetMySubscriptions = new GetMySubscriptions()
            }).Wait();
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            sw.Close();
        }
        public class ItemPresenter: INotifyPropertyChanged
        {
            private string _value = "";

            public string Key { get; set; } = "";
            public string Value { get => _value; set { if (_value != value) { _value = value; NotifyPropertyChanged(); } } }
            public ItemPresenter(string key, string value)
            {
                this.Key = key;
                this.Value = value;
            }
            public override string ToString()
            {
                return $"[{Key}] {Value}";
            }
            public event PropertyChangedEventHandler? PropertyChanged;
            private void NotifyPropertyChanged([CallerMemberName] String propertyName = "")
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }

        }
    }
}
