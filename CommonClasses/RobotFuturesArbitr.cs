using CommonClasses;
using Google.Protobuf.Collections;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client.Balancer;
using Newtonsoft.Json;
using NLog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Tinkoff.InvestApi;
using Tinkoff.InvestApi.V1;
using static Google.Protobuf.Compiler.CodeGeneratorResponse.Types;
using static System.Net.Mime.MediaTypeNames;

namespace RobotFuturesArbitr
{
    /// <summary>
    /// Робот арбитража акция-фьючерс. Поддерживает потоковые подписки T-Invest,
    /// рассчитывает отклонения между базовой акцией и фьючерсом, отслеживает позиции,
    /// стаканы и заявки, а также предоставляет статистику для UI RoboFutureArbitr.
    /// </summary>
    public class RobotFuturesArbitr : INotifyPropertyChanged, IDisposable
    {
        public Risk DefaultRisk = Risk.Low;
        private static bool PlaceOrders = true; // Should robot place any orders for real?

        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

        public event PropertyChangedEventHandler PropertyChanged;
        private void NotifyPropertyChanged([CallerMemberName] String propertyName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private string ApiKey = "";
        public double StartTradeDeviationPercent = 0.4d;
        public double CloseTradeDeviationPercent = 0.05d;
        public double FutureMinAverageDayVolume = 50000000d;
        public double SecurityGap = 0.3d;
        public int MaximumFutureExpirationDelayDays = 7;
        public int AvoidDividendsDays = 3;
        public int MaxDealMoneyToBook = 50000;
        public int AVGBuyCandlesCount = 30;
        public int AVGSellCandlesCount = 120;
        public int MaxCandlesToStore = 400;
        InvestApiClient Client;
        private AsyncDuplexStreamingCall<MarketDataRequest, MarketDataResponse> marketdatastream;
        private AsyncServerStreamingCall<OrderStateStreamResponse> ordersstream;
        private AsyncServerStreamingCall<PortfolioStreamResponse> portfoliostream;
        private AsyncServerStreamingCall<PositionsStreamResponse> positionsstream;
        private AsyncServerStreamingCall<TradesStreamResponse> usertradesstream;

        Timer tim;
        bool IsRunning = false;
        bool IsProcessing = false;
        private static long MaxDelayBeforeReloadStreams = 10; //Maximum delay in seconds after last stream response,after which itwill be restarted

        private DateTime MarketDataStreamPingTime = DateTime.MinValue;
        private DateTime OrdersStreamPingTime = DateTime.MinValue;
        private DateTime PositionsStreamPingTime = DateTime.MinValue;
        private DateTime PortfolioStreamPingTime = DateTime.MinValue;
        private DateTime UserTradesStreamPingTime = DateTime.MinValue;

        public RobotFuturesArbitr(
            string ApiKey,
            double StartTradeDeviationPercent,
            double CloseTradeDeviationPercent,
            double FutureMinAverageDayVolume,
            double SecurityGap,
            int MaximumFutureExpirationDelayDays,
            int AvoidDividendsDays,
            int MaxDealMoneyToBook,
            int AVGBuyCandlesCount,
            int AVGSellCandlesCount,
            int MaxCandlesToStore)
        {
            this.ApiKey = ApiKey;
            this.StartTradeDeviationPercent = StartTradeDeviationPercent;
            this.CloseTradeDeviationPercent = CloseTradeDeviationPercent;
            this.FutureMinAverageDayVolume = FutureMinAverageDayVolume;
            this.MaximumFutureExpirationDelayDays = MaximumFutureExpirationDelayDays;
            this.AvoidDividendsDays = AvoidDividendsDays;
            this.SecurityGap = SecurityGap;
            this.MaxDealMoneyToBook = MaxDealMoneyToBook;
            this.AVGBuyCandlesCount = AVGBuyCandlesCount;
            this.AVGSellCandlesCount = AVGSellCandlesCount;
            this.MaxCandlesToStore = MaxCandlesToStore;
            Client = InvestApiClientFactory.Create(ApiKey);
            // Таймер следит за жизнью потоков и перезапускает подписки, если они перестали отвечать.
            tim = new Timer(new TimerCallback((o) =>
            {
                if (IsRunning)
                {
                    if (!BasicDataIsUpdating)
                    {
                        try
                        {
                            UpdateAllBasicData();
                            if (marketdatastream == null || DateTime.Now - MarketDataStreamPingTime > TimeSpan.FromSeconds(MaxDelayBeforeReloadStreams))
                                UpdateMarketDataSubscriptions();
                            else
                                marketdatastream.RequestStream.WriteAsync(new Tinkoff.InvestApi.V1.MarketDataRequest() { Ping = new PingRequest { Time = DateTime.Now.ToUniversalTime().ToTimestamp() } }).Wait();
                            if (ordersstream == null || DateTime.Now - OrdersStreamPingTime > TimeSpan.FromSeconds(MaxDelayBeforeReloadStreams))
                                UpdateOrdersSubscriptions();
                            if (portfoliostream == null || DateTime.Now - PortfolioStreamPingTime > TimeSpan.FromSeconds(MaxDelayBeforeReloadStreams))
                                UpdatePortfolioSubscriptions();
                            if (positionsstream == null || DateTime.Now - PositionsStreamPingTime > TimeSpan.FromSeconds(MaxDelayBeforeReloadStreams))
                                UpdatePositionsSubscriptions();
                            if (usertradesstream == null || DateTime.Now - UserTradesStreamPingTime > TimeSpan.FromSeconds(MaxDelayBeforeReloadStreams))
                                UpdateUserTradesSubscriptions();
                        }
                        catch (Exception ex)
                        { }
                    }
                }
            }), null, 1000, 1000);
        }
        Dictionary<string, Share> AllShares = new Dictionary<string, Share>(); //Все акции
        Dictionary<string, Future> AllFutures = new Dictionary<string, Future>(); //Все фьючерсы
        Dictionary<string, Future> LiquidFutures = new Dictionary<string, Future>(); //Торгуемые фьючерсы
        Dictionary<string, Share> LiquidShares = new Dictionary<string, Share>(); //Торгуемые акции
        Dictionary<string, Share> FutureShare = new Dictionary<string, Share>(); //Словарь Акций по тикерам фьючерсов
        Dictionary<string, Dictionary<string, Future>> ShareFutures = new Dictionary<string, Dictionary<string, Future>>(); //Словарь Фьючерсов по тикерам акций
        Dictionary<string, HistoricalOrderBook> LastOrderbooks = new Dictionary<string, HistoricalOrderBook>(); //Последние стаканы по тикеру фьючерса
        Dictionary<string, Queue<HistoricalCandle>> LastCandles = new Dictionary<string, Queue<HistoricalCandle>>(); //Последние свечи фьючерса для рассчета формулы среднего отклоенения
        Dictionary<string, Tinkoff.InvestApi.V1.LastPrice> LastPrices = new Dictionary<string, Tinkoff.InvestApi.V1.LastPrice>();
        Dictionary<string, bool> TickerTradingIsAllowed = new Dictionary<string, bool>();//Можно ли торговать инструментом
        Dictionary<string, string> TickersUids = new Dictionary<string, string>();
        Dictionary<string, string> TickersFigis = new Dictionary<string, string>();
        Dictionary<string, string> UidsTickers = new Dictionary<string, string>();
        Dictionary<string, List<Dividend>> Dividends = new Dictionary<string, List<Dividend>>();
        Dictionary<string, double> AverageDeviationsBuy = new Dictionary<string, double>();
        Dictionary<string, double> AverageDeviationsSell = new Dictionary<string, double>();

        List<OrderState> LastOrders = new List<OrderState>();
        List<String> LiquidTickers = new List<string>();
        public Dictionary<string, Trade> LastBuyTrades = new Dictionary<string, Trade>();
        public Dictionary<string, Trade> LastSellTrades = new Dictionary<string, Trade>();
        public bool BasicDataIsUpdating = false;
        private DateTime LastBasicDataUpdateTime = DateTime.MinValue;

        private List<PositionsSecurities> MyShares { get; set; } = null;
        private List<PositionsFutures> MyFutures { get; set; } = null;
        private List<PositionsMoney> MyMoney { get; set; } = null;
        private bool PortfolioIsLoaded()
        {
            return MyShares != null && MyFutures != null && MyMoney != null;
        }
        private ObservableCollection<FutureStatisticsPresenter> futurestatistics = new ObservableCollection<FutureStatisticsPresenter>();
        public ObservableCollection<FutureStatisticsPresenter> FutureStatistics
        {
            get
            {
                if (LiquidFutures.Count() != futurestatistics.Count())
                {
                    futurestatistics.Clear();
                    LiquidFutures.OrderBy(u => u.Value.BasicAsset).ThenBy(u => u.Key).Select(u => new FutureStatisticsPresenter { FutureTicker = u.Key, Robot = this }).ToList().ForEach(u => futurestatistics.Add(u));
                }
                else
                    futurestatistics.ToList().ForEach(u => u.DataHasChanged());
                return futurestatistics;
            }
        }
        /// <summary>
        /// Оценивает доступный капитал с учетом денег, акций, фьючерсов и требуемого обеспечения.
        /// Возвращает общий лимит и детализацию по акциям/фьючерсам.
        /// </summary>
        private (double, Dictionary<string, Tuple<double, double>>, Dictionary<string, Tuple<double, double>>) GetAvailableMoney()
        {
            if (MyMoney == null) return (0, new Dictionary<string, Tuple<double, double>>(), new Dictionary<string, Tuple<double, double>>());
            var avail = MyMoney.FirstOrDefault(u => u.AvailableValue.Currency.ToUpper() == "RUB");
            double res = avail == null ? 0d : Helper.FromMoneyValue(avail.AvailableValue);
            Dictionary<string, Tuple<double, double>> sharesgarant = new Dictionary<string, Tuple<double, double>>();
            foreach (var s in MyShares.Where(u => UidsTickers.ContainsKey(u.InstrumentUid) && AllShares.ContainsKey(UidsTickers[u.InstrumentUid])))
            {
                var share = AllShares[UidsTickers[s.InstrumentUid]];
                if (!LastPrices.ContainsKey(share.Ticker))
                    return (0, new Dictionary<string, Tuple<double, double>>(), new Dictionary<string, Tuple<double, double>>());
                var quantity = s.Balance + s.Blocked;
                var money = quantity * Helper.FromQuotation(LastPrices[share.Ticker].Price);
                res += money;
                var garant = Math.Abs(money) * Helper.Margin(share, quantity > 0, DefaultRisk);
                sharesgarant.Add(share.Ticker, new Tuple<double, double>(money, garant));
            }

            Dictionary<string, Tuple<double, double>> futuresgarant = new Dictionary<string, Tuple<double, double>>();
            foreach (var f in MyFutures)
            {
                var future = AllFutures[UidsTickers[f.InstrumentUid]];
                if (!LastPrices.ContainsKey(future.Ticker))
                    return (0, new Dictionary<string, Tuple<double, double>>(), new Dictionary<string, Tuple<double, double>>());
                var quantity = f.Balance + f.Blocked;
                var money = quantity * Helper.FromQuotation(LastPrices[future.Ticker].Price) / Helper.FromQuotation(future.MinPriceIncrement) * Helper.FromQuotation(future.MinPriceIncrementAmount, 1);
                var garant = Math.Abs(money) * Helper.Margin(future, quantity > 0, DefaultRisk);
                futuresgarant.Add(future.Ticker, new Tuple<double, double>(money, garant));
            }
            return (res * (1d - SecurityGap), sharesgarant, futuresgarant);
        }
        public RepeatedField<Operation> GetLastOperations(DateTime datefrom, DateTime dateto)
        {
            if (datefrom.Kind != DateTimeKind.Utc)
                datefrom = datefrom.ToUniversalTime();
            if (dateto.Kind != DateTimeKind.Utc)
                dateto = dateto.ToUniversalTime();
            var ops = Client.Operations.GetOperations(new OperationsRequest { AccountId = Account.Id, From = datefrom.ToTimestamp(), To = dateto.ToTimestamp() }).Operations;
            return ops;
        }
        public (Dictionary<string, Future>, Dictionary<string, Share>) GetAllInstruments()
        {
            var allfutureslist = Client.Instruments.Futures().Instruments.Where(
                u => u.CountryOfRisk == "RU" &&
                u.Currency.ToUpper() == "RUB" &&
                u.ApiTradeAvailableFlag &&
                u.BuyAvailableFlag &&
                u.SellAvailableFlag &&
                u.ShortEnabledFlag).ToList();

            var futuresshares = allfutureslist.Select(u => u.BasicAsset).Distinct().ToList();
            var allshareslist = Client.Instruments.Shares().Instruments.Where(
                u => futuresshares.Contains(u.Ticker) &&
                u.ApiTradeAvailableFlag &&
                u.ShortEnabledFlag &&
                u.BuyAvailableFlag &&
                u.SellAvailableFlag &&
                u.CountryOfRisk == "RU" &&
                u.Currency.ToUpper() == "RUB"/* && new string[] { "SBER", "VTBR", "GAZP" }.Contains(u.Ticker)*/).ToList();

            allfutureslist = allfutureslist.Where(u => allshareslist.Select(s => s.Ticker).Contains(u.BasicAsset)).ToList();
            var AllFutures = allfutureslist.ToDictionary(u => u.Ticker);
            futuresshares = allfutureslist.Select(u => u.BasicAsset).Distinct().ToList();
            var AllShares = allshareslist.ToDictionary(u => u.Ticker);
            return (AllFutures, AllShares);
        }
        private (long, long) GetMaxFuturesLongShort(string ticker)
        {
            var future = AllFutures[ticker];
            var share = FutureShare[ticker];
            if (!LastPrices.ContainsKey(ticker) || !LastPrices.ContainsKey(share.Ticker))
                return (0, 0);
            var availmoney = GetAvailableMoney();
            var allmoney = availmoney.Item1;
            foreach (var f in availmoney.Item3.Where(u => u.Key != ticker))
            {
                var currfuture = AllFutures[f.Key];
                var money = f.Value.Item1;
                var garant = f.Value.Item2;
                var currshare = FutureShare[f.Key];
                var shareprice = Helper.FromQuotation(LastPrices[currshare.Ticker].Price);
                var sharegarantlong = Helper.MarginLong(currshare, DefaultRisk) * shareprice;
                var sharegarantshort = Helper.MarginShort(currshare, DefaultRisk) * shareprice;
                var cnt = Math.Ceiling(Math.Abs(money) / (shareprice * currshare.Lot)) * currshare.Lot;
                var sharegarant = money > 0 ? sharegarantshort * cnt : sharegarantlong * cnt;
                allmoney -= garant + sharegarant;
            }
            allmoney = Math.Abs(allmoney);
            var fprice = Helper.FromQuotation(LastPrices[ticker].Price) / Helper.FromQuotation(future.MinPriceIncrement) * Helper.FromQuotation(future.MinPriceIncrementAmount, 1);
            var fgarantlong = fprice * Helper.MarginLong(future, DefaultRisk);
            var fgarantshort = fprice * Helper.MarginShort(future, DefaultRisk);

            var sprice = Helper.FromQuotation(LastPrices[share.Ticker].Price) * share.Lot;
            var sgarantlong = Helper.MarginLong(share, DefaultRisk) * sprice;
            var sgarantshort = Helper.MarginShort(share, DefaultRisk) * sprice;

            var mul = fprice / sprice;
            var fcntlong = Convert.ToInt64(Math.Min(Math.Floor(allmoney / 2d / fgarantlong), Math.Floor(allmoney / 2d / (sgarantshort * mul))));
            var fcntshort = Convert.ToInt64(Math.Min(Math.Floor(allmoney / 2d / fgarantshort), Math.Floor(allmoney / 2d / (sgarantlong * mul))));
            var futurepos = MyFutures.FirstOrDefault(u => UidsTickers[u.InstrumentUid] == ticker);
            if (futurepos != null)
            {
                fcntlong = Math.Max(0, fcntlong - (futurepos.Balance + futurepos.Blocked));
                fcntshort = Math.Max(0, fcntshort + (futurepos.Balance + futurepos.Blocked));
            }
            return (fcntlong, fcntshort);
        }
        public void Start()
        {
            new Task(() =>
            {
                if (!IsRunning)
                {
                    Logger.Info($"Robot has started");
                    IsRunning = true;
                    UpdateAllBasicData();
                }
            }).Start();
        }
        public void Stop()
        {
            new Task(() =>
            {
                if (IsRunning)
                {
                    while (BasicDataIsUpdating)
                        Task.Delay(100);
                    marketdatastream?.Dispose();
                    marketdatastream = null;
                    ordersstream?.Dispose();
                    ordersstream = null;
                    portfoliostream?.Dispose();
                    portfoliostream = null;
                    positionsstream?.Dispose();
                    positionsstream = null;
                    IsRunning = false;
                    Logger.Info("Robot has been stopped");
                }
            }).Start();
        }
        private void UpdateAllBasicData()
        {
            if (BasicDataIsUpdating)
                return;
            if (DateTime.Now - LastBasicDataUpdateTime < TimeSpan.FromHours(1))
            {
                NotifyPropertyChanged(nameof(FutureStatistics));
                UpdateTradingIsAllowed();
                LoadCurrentPortfolio();
                LevelFuturesWithShares();
                ProcessCorrelationChanges();
                return;
            }
            try
            {
                BasicDataIsUpdating = true;
                Account = Client.Users.GetAccounts().Accounts.First(u => u.Status == AccountStatus.Open);
                var allinstruments = GetAllInstruments();
                AllFutures = allinstruments.Item1;
                AllShares = allinstruments.Item2;

                FutureShare = AllFutures.Select(u => (u.Key, AllShares[u.Value.BasicAsset])).ToDictionary();
                ShareFutures = AllShares.Select(u => (u.Key, AllFutures.Where(f => f.Value.BasicAsset == u.Key).ToDictionary())).ToDictionary();
                UidsTickers = AllFutures.Select(u => new KeyValuePair<string, string>(u.Value.Uid, u.Key)).Union(AllShares.Select(u => new KeyValuePair<string, string>(u.Value.Uid, u.Key))).ToDictionary();
                TickersUids = AllFutures.Select(u => new KeyValuePair<string, string>(u.Key, u.Value.Uid)).Union(AllShares.Select(u => new KeyValuePair<string, string>(u.Key, u.Value.Uid))).ToDictionary();
                TickersFigis = AllFutures.Select(u => new KeyValuePair<string, string>(u.Key, u.Value.Figi)).Union(AllShares.Select(u => new KeyValuePair<string, string>(u.Key, u.Value.Figi))).ToDictionary();
                UpdateLiquidInstruments();
                UpdateTradingIsAllowed();
                UpdateDividends();
                UpdateLastCandles();
                UpdateAverageDeviations();
                UpdateLastTrades();
                UpdateLastOrderBooks();
                UpdateLastPrices();

                UpdateMarketDataSubscriptions();
                UpdateOrdersSubscriptions();
                UpdatePositionsSubscriptions();
                UpdatePortfolioSubscriptions();
                UpdateUserTradesSubscriptions();
                LastBasicDataUpdateTime = DateTime.Now;
            }
            catch (Exception ex) when (false)
            {
                IsRunning = false;
            }
            finally
            {
                BasicDataIsUpdating = false;
            }
        }

        private void UpdateDividends()
        {
            DateTime dfrom = DateTime.Now.ToUniversalTime().AddDays(-30);
            DateTime dto = DateTime.Now.ToUniversalTime().AddDays(30);
            foreach (var share in LiquidShares)
            {
                var divs = Client.Instruments.GetDividends(new GetDividendsRequest { From = dfrom.ToTimestamp(), InstrumentId = share.Value.Uid, To = dto.ToTimestamp() }).Dividends.ToList();
                if (divs.Any())
                {
                    if (Dividends.ContainsKey(share.Key))
                        Dividends[share.Key] = divs;
                    else
                        Dividends.Add(share.Key, divs);
                }
            }
        }

        public List<Tinkoff.InvestApi.V1.LastPrice> GetLastPrices()
        {
            var req = new GetLastPricesRequest { };
            req.InstrumentId.AddRange(UidsTickers.Keys);
            return Client.MarketData.GetLastPrices(req).LastPrices.ToList();
        }
        private void UpdateLastPrices()
        {
            var prices = GetLastPrices();
            foreach (var p in prices)
            {
                var ticker = UidsTickers[p.InstrumentUid];
                if (LastPrices.ContainsKey(ticker))
                    LastPrices[ticker] = p;
                else
                    LastPrices.Add(ticker, p);
            }
        }

        private void UpdateLastOrderBooks()
        {
            foreach (var i in LiquidTickers)
            {
                var ob = Client.MarketData.GetOrderBook(new GetOrderBookRequest() { InstrumentId = TickersUids[i], Depth = 10 });
                HistoricalOrderBook hob = new HistoricalOrderBook { Entries = new List<HistoricalOrderBookEntry>(), Ticker = i, Time = DateTime.Now };
                hob.Entries.AddRange(ob.Bids.Select(u => new HistoricalOrderBookEntry { Price = Helper.FromQuotation(u.Price), Quontity = -u.Quantity }).OrderBy(u => u.Price));
                hob.Entries.AddRange(ob.Asks.Select(u => new HistoricalOrderBookEntry { Price = Helper.FromQuotation(u.Price), Quontity = u.Quantity }).OrderBy(u => u.Price));
                if (LastOrderbooks.ContainsKey(i))
                    LastOrderbooks[i] = hob;
                else
                    LastOrderbooks.Add(i, hob);
            }
        }

        private void UpdateLiquidInstruments()
        {
            LiquidFutures = AllFutures.Where(u => u.Value.ExpirationDate.ToDateTime() - DateTime.Now.ToUniversalTime() > TimeSpan.FromDays(MaximumFutureExpirationDelayDays)).ToDictionary();
            foreach (var f in LiquidFutures.Select(u => u.Value).ToList())
            {
                var candles = Client.MarketData.GetCandles(new GetCandlesRequest
                {
                    InstrumentId = f.Uid,
                    From = DateTime.Now.AddDays(-14).ToUniversalTime().ToTimestamp(),
                    To = DateTime.Now.ToUniversalTime().ToTimestamp(),
                    Interval = CandleInterval.Day
                }).Candles;
                var avg = 0d;
                if (candles.Any())
                {
                    avg = candles.Select(c => c.Volume * Helper.FromQuotation(c.Close)).Average();
                    avg = avg / Helper.FromQuotation(f.MinPriceIncrement, 1) * Helper.FromQuotation(f.MinPriceIncrementAmount, 1);
                }
                if (avg > FutureMinAverageDayVolume)
                {
                    if (!LiquidFutures.ContainsKey(f.Ticker))
                        LiquidFutures.Add(f.Ticker, f);
                }
                else
                {
                    if (LiquidFutures.ContainsKey(f.Ticker))
                        LiquidFutures.Remove(f.Ticker);
                }
            }
            //LiquidFutures = LiquidFutures.Where(u => u.Value.BasicAsset == "MGNT").ToDictionary();
            LiquidShares = AllShares.Where(u => LiquidFutures.Values.Select(s => s.BasicAsset).Contains(u.Key)).ToDictionary();
            LiquidTickers = LiquidShares.Keys.Union(LiquidFutures.Keys).ToList();
        }

        private void UpdateTradingIsAllowed()
        {
            try
            {
                var req = new GetTradingStatusesRequest();
                req.InstrumentId.AddRange(AllShares.Select(u => u.Value.Uid).Union(AllFutures.Select(u => u.Value.Uid)));
                var s = Client.MarketData.GetTradingStatuses(req).TradingStatuses;
                TickerTradingIsAllowed = s.Select(u => new KeyValuePair<string, bool>(UidsTickers[u.InstrumentUid], (u.ApiTradeAvailableFlag && u.TradingStatus == SecurityTradingStatus.NormalTrading))).ToDictionary();
            }
            catch (Exception ex)
            {
            }
        }

        private void UpdateLastCandles()
        {
            LastCandles = new Dictionary<string, Queue<HistoricalCandle>>();
            {
                foreach (var e in LiquidTickers)
                {
                    var data = new HistoricalData(e, TickersFigis[e], HistoricalTimeFrame.M1, new HistoricalData.QueryDataDelegate((figi, tf, dfrom, dto) =>
                    {
                        return Client.MarketData.GetCandles(new GetCandlesRequest
                        {
                            InstrumentId = TickersUids[e],
                            From = dfrom.ToUniversalTime().ToTimestamp(),
                            Interval = Helper.HistoricalTimeFrameToCandleInterval(tf),
                            To = dto.ToUniversalTime().ToTimestamp()
                        }).Candles.Select(u => new HistoricalCandle(u)).ToList();
                    }));
                    var clist = data.GetData(DateTime.Now.AddDays(-3), DateTime.Now).TakeLast(MaxCandlesToStore);

                    //var clist = Client.MarketData.GetCandles(new GetCandlesRequest { InstrumentId = TickersUids[e], From = DateTime.Now.AddDays(-1).ToUniversalTime().ToTimestamp(), Limit = MaxCandlesToStore, Interval = CandleInterval._1Min, To = DateTime.Now.ToUniversalTime().ToTimestamp() }).Candles.Where(u => u.IsComplete).Select(u => new HistoricalCandle(u));
                    if (LastCandles.ContainsKey(e))
                        LastCandles[e] = new Queue<HistoricalCandle>(clist);
                    else
                        LastCandles.Add(e, new Queue<HistoricalCandle>(clist));

                }
            }
        }
        private void UpdateAverageDeviations()
        {
            foreach (var f in LiquidFutures)
                UpdateInstrumentAverageDeviations(f.Key);
        }

        private void UpdateInstrumentAverageDeviations(string ticker)
        {
            List<Future> futures = new List<Future>();
            if (AllShares.ContainsKey(ticker))
                futures.AddRange(ShareFutures[ticker].Values);
            else
                futures.Add(AllFutures[ticker]);
            foreach (var f in futures)
            {
                var share = FutureShare[f.Ticker].Ticker;
                if (!(LastCandles.ContainsKey(share) && LastCandles.ContainsKey(f.Ticker)))
                    continue;
                var clist = LastCandles[share].Select(u => new HistoricalCandle { Open = u.Open, Close = u.Close, High = u.High, Low = u.Low, Time = u.Time, Volume = u.Volume }).ToList();
                if (Dividends.ContainsKey(share))
                    foreach (var div in Dividends[share].Where(u => u.LastBuyDate.ToDateTime() < DateTime.Now.ToUniversalTime()))
                    {
                        //var lastprice = Helper.FromMoneyValue(div.ClosePrice);
                        //var percent = Helper.FromQuotation(div.YieldValue);
                        var delta = Helper.FromMoneyValue(div.DividendNet);// lastprice * percent / 100d;
                        foreach (var c in clist)
                        {
                            if (c.Time < div.LastBuyDate.ToDateTime())
                            {
                                c.Open -= delta;
                                c.Close -= delta;
                                c.Low -= delta;
                                c.High -= delta;
                            }
                        }
                    }
                var averagedeviationbuy = CalculateAverageDeviation(clist, LastCandles[f.Ticker], AVGBuyCandlesCount);
                var averagedeviationsell = CalculateAverageDeviation(clist, LastCandles[f.Ticker], AVGSellCandlesCount);
                if (averagedeviationbuy.HasValue)
                {
                    if (AverageDeviationsBuy.ContainsKey(f.Ticker))
                        AverageDeviationsBuy[f.Ticker] = averagedeviationbuy.Value;
                    else
                        AverageDeviationsBuy.Add(f.Ticker, averagedeviationbuy.Value);
                }
                if (averagedeviationsell.HasValue)
                {
                    if (AverageDeviationsSell.ContainsKey(f.Ticker))
                        AverageDeviationsSell[f.Ticker] = averagedeviationsell.Value;
                    else
                        AverageDeviationsSell.Add(f.Ticker, averagedeviationsell.Value);
                }
            }
        }

        /*        private void UpdateAllCoeffs()
                {
                    foreach (var f in LiquidFutures)
                        UpdateInstrumentCoeffs(f.Key);
                }*/
        /*        private void UpdateInstrumentCoeffs(string ticker)
                {
                    List<Future> futures = new List<Future>();
                    if (AllShares.ContainsKey(ticker))
                        futures.AddRange(ShareFutures[ticker].Values);
                    else
                        futures.Add(AllFutures[ticker]);
                    foreach (var f in futures)
                    {
                        var share = FutureShare[f.Ticker].Ticker;
                        if (share == "VTBR")
                        {
                            bool vtbr = true;
                        }
                        if (!(LastCandles.ContainsKey(share) && LastCandles.ContainsKey(f.Ticker)))
                            continue;
                        DateTime now = DateTime.Now.ToUniversalTime();
                        DateTime midday = new DateTime(now.Year, now.Month, now.Day, 7, 0, 0, DateTimeKind.Utc);
                        var clist = LastCandles[share].Where(u => u.Time < midday).Select(u => new HistoricalCandle { Open = u.Open, Close = u.Close, High = u.High, Low = u.Low, Time = u.Time, Volume = u.Volume }).ToList();
                        if (Dividends.ContainsKey(share))
                            foreach(var div in Dividends[share].Where(u => u.LastBuyDate.ToDateTime() < DateTime.Now.ToUniversalTime()))
                            {
                                //var lastprice = Helper.FromMoneyValue(div.ClosePrice);
                                //var percent = Helper.FromQuotation(div.YieldValue);
                                var delta = Helper.FromMoneyValue(div.DividendNet);// lastprice * percent / 100d;
                                foreach(var c in clist)
                                {
                                    if (c.Time < div.LastBuyDate.ToDateTime())
                                    {
                                        c.Open -= delta;
                                        c.Close -= delta;
                                        c.Low -= delta;
                                        c.High -= delta;
                                    }
                                }
                            }
                        var li = LinearInterpolator.FromCandles(clist, LastCandles[f.Ticker].Where(u => u.Time < midday), 200, 20);
                        if (li != null)
                        {
                            if (AllCoeffs.ContainsKey(f.Ticker))
                                AllCoeffs[f.Ticker] = li;
                            else
                                AllCoeffs.Add(f.Ticker, li);
                        }
                    }
                }*/

        private void UpdateLastTrades()
        {
            foreach (var i in LiquidTickers)
            {
                var trades = Client.MarketData.GetLastTrades(new GetLastTradesRequest() { InstrumentId = TickersUids[i], From = DateTime.Now.AddMinutes(-10).ToUniversalTime().ToTimestamp(), To = DateTime.Now.ToUniversalTime().ToTimestamp() }).Trades;
                var tbuy = trades.LastOrDefault(u => u.Direction == TradeDirection.Buy);
                var tsell = trades.LastOrDefault(u => u.Direction == TradeDirection.Sell);
                if (tbuy != null)
                {
                    if (LastBuyTrades.ContainsKey(i))
                        LastBuyTrades[i] = tbuy;
                    else
                        LastBuyTrades.Add(i, tbuy);
                }
                if (tsell != null)
                {
                    if (LastSellTrades.ContainsKey(i))
                        LastSellTrades[i] = tsell;
                    else
                        LastSellTrades.Add(i, tsell);
                }
            }
        }


        Account Account;
        private void UpdatePortfolioSubscriptions()
        {
            portfoliostream?.Dispose();
            portfoliostream = null;
            var psreq = new PortfolioStreamRequest { PingSettings = new PingDelaySettings { PingDelayMs = 5000 } };
            psreq.Accounts.Add(Account.Id);
            portfoliostream = Client.OperationsStream.PortfolioStream(psreq);
            PropcessPortfolioStreamResponse();
            PortfolioStreamPingTime = DateTime.Now;
        }
        private async void PropcessPortfolioStreamResponse()
        {
            try
            {
                await foreach (var resp in portfoliostream.ResponseStream.ReadAllAsync())
                {
                    if (!IsRunning || BasicDataIsUpdating)
                        continue;
                    switch (resp.PayloadCase)
                    {
                        case PortfolioStreamResponse.PayloadOneofCase.Portfolio:
                            break;
                        case PortfolioStreamResponse.PayloadOneofCase.Ping:
                            break;
                        case PortfolioStreamResponse.PayloadOneofCase.Subscriptions:
                            break;
                        default:
                            break;
                    }
                    PortfolioStreamPingTime = DateTime.Now;
                }
            }
            catch (Exception ex)
            { }
        }
        private void UpdatePositionsSubscriptions()
        {
            positionsstream?.Dispose();
            positionsstream = null;
            var psreq = new PositionsStreamRequest { WithInitialPositions = true, PingSettings = new PingDelaySettings { PingDelayMs = 5000 } };
            psreq.Accounts.Add(Account.Id);
            positionsstream = Client.OperationsStream.PositionsStream(psreq);
            ProcessPositionsStreamResponse();
            PositionsStreamPingTime = DateTime.Now;
        }
        private async void ProcessPositionsStreamResponse()
        {
            try
            {
                await foreach (var resp in positionsstream.ResponseStream.ReadAllAsync())
                {
                    if (!IsRunning || BasicDataIsUpdating)
                        continue;
                    switch (resp.PayloadCase)
                    {
                        case PositionsStreamResponse.PayloadOneofCase.InitialPositions:
                            break;
                        case PositionsStreamResponse.PayloadOneofCase.Position:
                            break;
                        case PositionsStreamResponse.PayloadOneofCase.Ping:
                            break;
                        case PositionsStreamResponse.PayloadOneofCase.Subscriptions:
                            break;
                        default:
                            break;
                    }
                    PositionsStreamPingTime = DateTime.Now;
                }
            }
            catch (Exception ex)
            { }
        }
        DateTime LastLevellingTime = DateTime.MinValue;
        private string traceLog;

        bool IsLevelling = false;
        public void AddLog(string message, [CallerMemberName] string memberName = "", [CallerFilePath] string sourceFilePath = "", [CallerLineNumber] int sourceLineNumber = 0)
        {
            Logger.Info("Message: " + message);
            Logger.Info("Member/Function name: " + memberName);
            Logger.Info("Source file path: " + sourceFilePath);
            Logger.Info("Source line number: " + sourceLineNumber);
        }
        private void LevelFuturesWithShares()
        {
            if (DateTime.Now - LastLevellingTime < new TimeSpan(0, 0, 0, 1))
                return;
            if (IsLevelling)
                return;
            IsLevelling = true;
            try
            {
                var NotLiquidFuturesMoney = MyFutures.Where(u => !LiquidFutures.ContainsKey(UidsTickers[u.InstrumentUid])).ToList();
                MyFutures = MyFutures.Where(u => LiquidFutures.ContainsKey(UidsTickers[u.InstrumentUid])).ToList();
                List<string> _futures = MyFutures.Select(u => UidsTickers[u.InstrumentUid]).ToList();
                foreach (var f in NotLiquidFuturesMoney)
                {
                    var f_orders = Client.Orders.GetOrders(new GetOrdersRequest { AccountId = Account.Id }).Orders;
                    foreach (var order in f_orders.Where(u => u.InstrumentUid == f.InstrumentUid))
                    {
                        Client.Orders.CancelOrder(new CancelOrderRequest { AccountId = Account.Id, OrderId = order.OrderId });
                        LastLevellingTime = DateTime.Now;
                    }
                    var qty = f.Balance + f.Blocked;
                    if (PlaceOrders)
                    {
                        try
                        {
                            var ticker = UidsTickers[f.InstrumentUid];
                            var dir = qty > 0 ? OrderDirection.Sell : OrderDirection.Buy;
                            Logger.Info($"Market order {dir.ToString().ToLower()} for [{ticker}]  Quontity={Math.Abs(qty)} - get rid off not liquid futures");
                            Client.Orders.PostOrder(new PostOrderRequest
                            {
                                Direction = dir,
                                AccountId = Account.Id,
                                InstrumentId = f.InstrumentUid,
                                OrderType = OrderType.Market,
                                Quantity = Math.Abs(qty),
                                TimeInForce = TimeInForceType.TimeInForceDay,
                                PriceType = PriceType.Point,
                                OrderId = $"FUTARB_{Guid.NewGuid().ToShortGuid()}"
                            });
                            LastLevellingTime = DateTime.Now;
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn(ex.Message);
                            Logger.Error(ex.StackTrace);
                        }
                    }
                    else
                        AddLog("Placed market order");
                }
                List<string> _shares = MyShares.Select(u => UidsTickers[u.InstrumentUid]).ToList();
                foreach (var s in _shares)
                    foreach (var f in ShareFutures[s])
                        if (LiquidTickers.Contains(f.Key))
                            _futures.Add(f.Key);
                foreach (var f in _futures)
                    if (LiquidTickers.Contains(FutureShare[f].Ticker))
                        _shares.Add(FutureShare[f].Ticker);
                _futures = _futures.Distinct().ToList();
                _shares = _shares.Distinct().ToList();
                var flist = AllFutures.Where(u => _futures.Contains(u.Key)).Select(u => u.Value);
                var slist = AllShares.Where(u => _shares.Contains(u.Key)).Select(u => u.Value);
                foreach (var f in flist)
                    if (!LastPrices.ContainsKey(f.Ticker))
                    {
                        IsLevelling = false;
                        return;
                    }
                foreach (var s in slist)
                    if (!LastPrices.ContainsKey(s.Ticker))
                    {
                        IsLevelling = false;
                        return;
                    }

                foreach (var s in slist)
                {
                    if (!TickerTradingIsAllowed[s.Ticker])
                        continue;
                    var moneyshares = 0d;
                    var mshare = MyShares.FirstOrDefault(u => u.InstrumentUid == s.Uid);
                    if (mshare != null)
                        moneyshares += (mshare.Balance + mshare.Blocked) * Helper.FromQuotation(LastPrices[s.Ticker].Price);
                    var moneyfutures = 0d;
                    foreach (var f in flist.Where(u => u.BasicAsset == s.Ticker))
                    {
                        var mfuture = MyFutures.FirstOrDefault(u => u.InstrumentUid == f.Uid);
                        if (mfuture != null)
                            moneyfutures += (mfuture.Balance + mfuture.Blocked) / Helper.FromQuotation(f.MinPriceIncrement) * Helper.FromQuotation(f.MinPriceIncrementAmount) * Helper.FromQuotation(LastPrices[f.Ticker].Price);
                    }
                    var delta = -moneyshares - moneyfutures;
                    long qty = Convert.ToUInt32(Math.Floor(Math.Abs(delta) / Helper.FromQuotation(LastPrices[s.Ticker].Price) / s.Lot));
                    if (LastOrderbooks.ContainsKey(s.Ticker))
                    {
                        var maxbid = LastOrderbooks[s.Ticker].Entries.LastOrDefault(u => u.Quontity < 0);
                        var minask = LastOrderbooks[s.Ticker].Entries.FirstOrDefault(u => u.Quontity > 0);
                        if (delta == 0 || delta < 0 && maxbid == null || delta > 0 && minask == null)
                            qty = 0;
                        else
                            qty = Math.Min(delta < 0 ? Math.Abs(maxbid.Quontity) : Math.Abs(minask.Quontity), qty);
                    }
                    if (qty != 0)
                        if (PlaceOrders)
                        {
                            try
                            {
                                var dir = delta < 0 ? OrderDirection.Sell : OrderDirection.Buy;
                                Logger.Info($"Order market {dir.ToString().ToLower()} for [{s.Ticker}] Quontity={qty} levelling share with futures");
                                Client.Orders.PostOrder(new PostOrderRequest
                                {
                                    AccountId = Account.Id,
                                    Direction = dir,
                                    InstrumentId = s.Uid,
                                    OrderType = OrderType.Market,
                                    Quantity = qty,
                                    TimeInForce = TimeInForceType.TimeInForceDay,
                                    PriceType = PriceType.Currency,
                                    OrderId = $"FUTARB_{Guid.NewGuid().ToShortGuid()}"
                                });
                                LastLevellingTime = DateTime.Now;
                            }
                            catch (Exception ex)
                            {
                                Logger.Warn(ex.Message);
                                Logger.Error(ex.StackTrace);
                            }
                        }
                        else
                            AddLog("Placed market order");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex.Message);
                Logger.Error(ex.StackTrace);
            }
            finally
            {
                IsLevelling = false;
            }
        }
        public (List<PositionsMoney>, List<PositionsSecurities>, List<PositionsFutures>) GetLastPortfolio()
        {
            var resp = Client.Operations.GetPositions(new PositionsRequest { AccountId = Account.Id });
            return (new List<PositionsMoney>(resp.Money.Select(u => new PositionsMoney { AvailableValue = u, BlockedValue = resp.Blocked.FirstOrDefault(b => b.Currency == u.Currency) })),
                new List<PositionsSecurities>(resp.Securities.Where(u => u.InstrumentType == "share")),
                new List<PositionsFutures>(resp.Futures));
        }

        private void LoadCurrentPortfolio()
        {
            var resp = GetLastPortfolio(); Client.Operations.GetPositions(new PositionsRequest { AccountId = Account.Id });
            MyFutures = resp.Item3;
            MyShares = resp.Item2;
            MyMoney = resp.Item1;
        }

        private void UpdateMarketDataSubscriptions()
        {
            marketdatastream?.Dispose();
            marketdatastream = null;
            marketdatastream = Client.MarketDataStream.MarketDataStream();

            var creq = new SubscribeCandlesRequest() { SubscriptionAction = SubscriptionAction.Subscribe, WaitingClose = true };
            creq.Instruments.AddRange(LiquidTickers.Select(u => new CandleInstrument { InstrumentId = TickersUids[u], Interval = SubscriptionInterval.OneMinute }));

            var breq = new SubscribeOrderBookRequest { SubscriptionAction = SubscriptionAction.Subscribe };
            breq.Instruments.AddRange(LiquidTickers.Select(u => new OrderBookInstrument { InstrumentId = TickersUids[u], Depth = 10, OrderBookType = OrderBookType.All }));

            var treq = new SubscribeTradesRequest() { SubscriptionAction = SubscriptionAction.Subscribe };
            treq.Instruments.AddRange(LiquidTickers.Select(u => new TradeInstrument { InstrumentId = TickersUids[u] }));
            var preq = new SubscribeLastPriceRequest() { SubscriptionAction = SubscriptionAction.Subscribe };
            preq.Instruments.AddRange(LiquidTickers.Select(u => new LastPriceInstrument { InstrumentId = TickersUids[u] }));

            marketdatastream.RequestStream.WriteAsync(new Tinkoff.InvestApi.V1.MarketDataRequest() { SubscribeCandlesRequest = creq }).Wait();
            marketdatastream.RequestStream.WriteAsync(new Tinkoff.InvestApi.V1.MarketDataRequest() { SubscribeOrderBookRequest = breq }).Wait();
            marketdatastream.RequestStream.WriteAsync(new Tinkoff.InvestApi.V1.MarketDataRequest() { SubscribeTradesRequest = treq }).Wait();
            marketdatastream.RequestStream.WriteAsync(new Tinkoff.InvestApi.V1.MarketDataRequest() { SubscribeLastPriceRequest = preq }).Wait();
            ProcessMarketData();
            MarketDataStreamPingTime = DateTime.Now;
        }
        private async void ProcessMarketData()
        {
            try
            {
                await foreach (var resp in marketdatastream.ResponseStream.ReadAllAsync())
                {
                    if (!IsRunning || BasicDataIsUpdating)
                        continue;
                    switch (resp.PayloadCase)
                    {
                        case MarketDataResponse.PayloadOneofCase.LastPrice:
                            {
                                var ticker = UidsTickers[resp.LastPrice.InstrumentUid];
                                if (LastPrices.ContainsKey(ticker))
                                    LastPrices[ticker] = resp.LastPrice;
                                else
                                    LastPrices.Add(ticker, resp.LastPrice);
                                break;
                            }
                        case MarketDataResponse.PayloadOneofCase.Candle:
                            {
                                var ticker = UidsTickers[resp.Candle.InstrumentUid];
                                var c = new HistoricalCandle(resp.Candle);
                                if (!LastCandles.ContainsKey(ticker))
                                    LastCandles.Add(ticker, new Queue<HistoricalCandle>());
                                var queue = LastCandles[ticker];
                                while (queue.Count >= MaxCandlesToStore)
                                    queue.Dequeue();
                                queue.Enqueue(c);
                                UpdateInstrumentAverageDeviations(ticker);
                                break;
                            }

                        case MarketDataResponse.PayloadOneofCase.Trade:
                            {
                                if (!PortfolioIsLoaded())
                                    break;
                                var ticker = UidsTickers[resp.Trade.InstrumentUid];
                                if (resp.Trade.Direction == TradeDirection.Buy)
                                {
                                    if (LastBuyTrades.ContainsKey(ticker))
                                        LastBuyTrades[ticker] = resp.Trade;
                                    else
                                        LastBuyTrades.Add(ticker, resp.Trade);
                                }
                                else if (resp.Trade.Direction == TradeDirection.Sell)
                                {
                                    if (LastSellTrades.ContainsKey(ticker))
                                        LastSellTrades[ticker] = resp.Trade;
                                    else
                                        LastSellTrades.Add(ticker, resp.Trade);
                                }
                                break;
                            }
                        case MarketDataResponse.PayloadOneofCase.Orderbook:
                            {
                                var ticker = UidsTickers[resp.Orderbook.InstrumentUid];
                                var ob = resp.Orderbook;
                                HistoricalOrderBook hob = new HistoricalOrderBook { Entries = new List<HistoricalOrderBookEntry>(), Ticker = ticker, Time = ob.Time.ToDateTime() };
                                hob.Entries.AddRange(ob.Bids.Select(u => new HistoricalOrderBookEntry { Price = Helper.FromQuotation(u.Price), Quontity = -u.Quantity }).OrderBy(u => u.Price));
                                hob.Entries.AddRange(ob.Asks.Select(u => new HistoricalOrderBookEntry { Price = Helper.FromQuotation(u.Price), Quontity = u.Quantity }).OrderBy(u => u.Price));
                                if (LastOrderbooks.ContainsKey(ticker))
                                    LastOrderbooks[ticker] = hob;
                                else
                                    LastOrderbooks.Add(ticker, hob);
                                break;
                            }
                        case MarketDataResponse.PayloadOneofCase.Ping:
                            break;
                        default:
                            break;
                    }
                    MarketDataStreamPingTime = DateTime.Now;
                }
            }
            catch (Exception ex)
            { }
        }
        private double? CalculateAverageDeviation(IEnumerable<HistoricalCandle> scandles, IEnumerable<HistoricalCandle> fcandles, int MALen = 60)
        {
            if (scandles == null || !scandles.Any() || fcandles == null || !fcandles.Any())
                return null;
            var pairs = Helper.GetHistoricalCandlesPairs(scandles, fcandles);
            if (!pairs.Any())
                return null;
            return pairs.TakeLast(MALen).Average(u => u.Item1.Price / u.Item2.Price);
        }
        private void ProcessCorrelationChanges()
        {
            if (DateTime.Now - LastLevellingTime < new TimeSpan(0, 0, 1))
                return;
            LastOrders = Client.Orders.GetOrders(new GetOrdersRequest() { AccountId = Account.Id }).Orders.ToList();
            var futurestoprocess = new List<string>();
            foreach (var future in LiquidFutures.Values)
            {
                var fticker = future.Ticker;
                var share = FutureShare[fticker];
                var sticker = share.Ticker;
                if (!TickerTradingIsAllowed[fticker] || !TickerTradingIsAllowed[sticker])
                    continue;
                if (!(AverageDeviationsBuy.ContainsKey(fticker) && AverageDeviationsSell.ContainsKey(fticker)))
                    continue;
                if (!(LastOrderbooks.ContainsKey(fticker) && LastOrderbooks.ContainsKey(sticker)))
                    continue;
                if (LastBuyTrades.ContainsKey(fticker))
                    if (DateTime.Now.ToUniversalTime() - LastBuyTrades[fticker].Time.ToDateTime().ToUniversalTime() < new TimeSpan(0, 1, 0))
                        futurestoprocess.Add(fticker);
                if (LastSellTrades.ContainsKey(fticker))
                    if (DateTime.Now.ToUniversalTime() - LastSellTrades[fticker].Time.ToDateTime().ToUniversalTime() < new TimeSpan(0, 1, 0))
                        futurestoprocess.Add(fticker);
                if (LastOrders.Any(u => u.InstrumentUid == future.Uid))
                    futurestoprocess.Add(fticker);
            }
            futurestoprocess = futurestoprocess.Distinct().ToList();
            
            var ltlist = futurestoprocess.Select(u =>
            {
                var future = AllFutures[u];
                var fticker = future.Ticker;
                var share = FutureShare[fticker];
                var sticker = share.Ticker;
                var minpriceincrement = Helper.FromQuotation(future.MinPriceIncrement);
                var minpriceincrementamount = Helper.FromQuotation(future.MinPriceIncrementAmount);
                var futurebook = LastOrderbooks[fticker];
                var sharebook = LastOrderbooks[sticker];
                var futuremaxbid = futurebook.MaxBid;
                var futureminask = futurebook.MinAsk;
                var sharemaxbid = sharebook.MaxBid;
                var shareminask = sharebook.MinAsk;
                if (!(futuremaxbid.HasValue && futureminask.HasValue && sharemaxbid.HasValue && shareminask.HasValue))
                    return null;
                var averagedeviationbuy = AverageDeviationsBuy[fticker];
                var averagedeviationsell = AverageDeviationsSell[fticker];

                var limitbuydeviation = sharemaxbid.Value / (futuremaxbid.Value + minpriceincrement);
                var limitselldeviation = shareminask.Value / (futureminask.Value - minpriceincrement);
                var balancepositiveoutdeviation = shareminask.Value / futuremaxbid.Value;
                var balancenegativeoutdeviation = sharemaxbid.Value / futureminask.Value;
                long currentfuturebalance = 0;
                try
                {
                    var fbalancelist = MyFutures.Where(u => u.InstrumentUid == future.Uid);
                    currentfuturebalance = fbalancelist.Sum(u => u.Balance + u.Blocked);
                }
                catch
                { }
                bool OpenPositionLimitBuy = false;
                bool OpenPositionLimitSell = false;
                bool CloseLongPosition = false;
                bool CloseShortPosition = false;
                bool HasOpenedOrders = false;
                double Diff = 0d;
                if (currentfuturebalance != 0)
                {
                    if (currentfuturebalance > 0)
                    {
                        var dev = (balancepositiveoutdeviation - averagedeviationsell) / averagedeviationsell * 100d;
                        if (dev < -CloseTradeDeviationPercent)
                        {
                            CloseLongPosition = true;
                            Diff = dev;
                        }
                        else
                            CloseLongPosition = false;
                    }
                    else
                    {
                        var dev = (balancenegativeoutdeviation - averagedeviationsell) / averagedeviationsell * 100d;
                        if (dev > CloseTradeDeviationPercent)
                        {
                            CloseShortPosition = true;
                            Diff = dev;
                        }
                        else
                            CloseShortPosition = false;
                    }
                }
                else
                {
                    var val1 = (limitbuydeviation - averagedeviationbuy) / averagedeviationbuy * 100d;
                    var val2 = (limitselldeviation - averagedeviationbuy) / averagedeviationbuy * 100d;
                    if (val1 > StartTradeDeviationPercent)
                        OpenPositionLimitBuy = true;
                    if (val2 < -StartTradeDeviationPercent)
                        OpenPositionLimitSell = true;
                    if (OpenPositionLimitBuy && OpenPositionLimitSell)
                    {
                        if (Math.Abs(val1) > Math.Abs(val2))
                            OpenPositionLimitSell = false;
                        else
                            OpenPositionLimitBuy = false;
                    }
                    if (OpenPositionLimitBuy)
                        Diff = val1;
                    if (OpenPositionLimitSell)
                        Diff = val2;
                    if (Dividends.ContainsKey(share.Ticker))
                    {
                        foreach (var div in Dividends[share.Ticker])
                            if ((DateTime.Now.ToUniversalTime() - div.LastBuyDate.ToDateTime()).Duration() < TimeSpan.FromDays(AvoidDividendsDays))
                            {
                                OpenPositionLimitBuy = false;
                                OpenPositionLimitSell = false;
                                break;
                            }
                    }
                }
                if (LastOrders.Any(u => u.InstrumentUid == future.Uid))
                    HasOpenedOrders = true;

                if (HasOpenedOrders && !(OpenPositionLimitBuy || OpenPositionLimitSell))
                {
                    List<OrderState> ordersforremove = new List<OrderState>();
                    foreach (var order in LastOrders.Where(u => u.InstrumentUid == future.Uid))
                        RemoveOrder(future, ordersforremove, order);
                    foreach (var order in ordersforremove)
                        LastOrders.Remove(order);
                    HasOpenedOrders = false;
                }
                if (!(CloseLongPosition || CloseShortPosition || OpenPositionLimitSell || OpenPositionLimitBuy))
                    return null;
                return new
                {
                    Trade = u,
                    Future = future,
                    Share = share,
                    CurrentFutureBalance = currentfuturebalance,
                    MinPriceIncrement = minpriceincrement,
                    MinPriceIncrementAmount = minpriceincrementamount,
                    FutureBook = futurebook,
                    ShareBook = sharebook,
                    FutureMaxBid = futuremaxbid.Value,
                    FutureMinAsk = futureminask.Value,
                    ShareMaxBid = sharemaxbid.Value,
                    ShareMinAsk = shareminask.Value,
                    OpenPositionLimitBuy = OpenPositionLimitBuy,
                    OpenPositionLimitSell = OpenPositionLimitSell,
                    CloseLongPosition = CloseLongPosition,
                    CloseShortPosition = CloseShortPosition,
                    Diff = Diff
                };
            }).Where(u => u != null).OrderByDescending(u => Math.Abs(u.CurrentFutureBalance)).ThenByDescending(u => Math.Abs(u.Diff)).ToList();

            foreach (var te in ltlist.Where(u => u.CloseLongPosition || u.CloseShortPosition))
            {
                var balance = te.CurrentFutureBalance;
                bool isbuy = balance < 0;
                var maxquontity = Math.Abs(isbuy ? te.FutureBook.Entries.First(u => u.Quontity > 0).Quontity : te.FutureBook.Entries.Last(u => u.Quontity < 0).Quontity);
                maxquontity = Math.Min(maxquontity, Math.Abs(te.CurrentFutureBalance));
                if (maxquontity != 0)
                {
                    try
                    {
                        Logger.Info($"Market {(isbuy ? "buy" : "sell")} [{te.Future.Ticker}] Quontity={maxquontity} Deviation {te.Diff:F2}% - reverse futures!");
                        Client.Orders.PostOrder(new PostOrderRequest
                        {
                            AccountId = Account.Id,
                            Direction = isbuy ? OrderDirection.Buy : OrderDirection.Sell,
                            InstrumentId = te.Future.Uid,
                            OrderType = OrderType.Market,
                            Quantity = maxquontity,
                            TimeInForce = TimeInForceType.TimeInForceDay,
                            OrderId = $"FUTARB_{Guid.NewGuid().ToShortGuid()}"
                        });
                        LastLevellingTime = DateTime.Now;
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn(ex.Message);
                        Logger.Error(ex.StackTrace);
                    }
                }
            }
            foreach (var te in ltlist.Where(u => !(u.CloseLongPosition || u.CloseShortPosition) && (u.OpenPositionLimitBuy || u.OpenPositionLimitSell)))
            {
                bool islimitbuy = te.OpenPositionLimitBuy;
                var balancelst = MyFutures.Where(u => u.InstrumentUid == te.Future.Uid).ToList();
                var balance = 0d;
                if (balancelst.Any())
                    balance = balancelst.Sum(u => u.Balance + u.Blocked);
                var maxlots = GetMaxFuturesLongShort(te.Future.Ticker);
                long maxlotnumber = islimitbuy ? maxlots.Item1 : maxlots.Item2;
                double targetprice = (islimitbuy ? te.FutureMaxBid + te.MinPriceIncrement : te.FutureMinAsk - te.MinPriceIncrement);
                double lotprice = targetprice / te.MinPriceIncrement * te.MinPriceIncrementAmount;
                maxlotnumber = Math.Min(Convert.ToInt32(Math.Floor(MaxDealMoneyToBook / lotprice)), maxlotnumber);
                bool orderplaced = false;
                List<OrderState> ordersforremove = new List<OrderState>();
                foreach (var order in LastOrders.Where(u => u.InstrumentUid == te.Future.Uid))
                {
                    bool orderisbuy = order.Direction == OrderDirection.Buy;
                    if (order.OrderType != OrderType.Limit || islimitbuy != orderisbuy)
                    {
                        ordersforremove.Add(order);
                        continue;
                    }
                    if (maxlotnumber > 0)
                    {
                        try
                        {
                            if (Math.Round(Helper.FromMoneyValue(order.InitialSecurityPrice), 2) != Math.Round(targetprice, 2) || order.LotsExecuted > 0)
                            {
                                if (PlaceOrders)
                                {
                                    var dir = (orderisbuy ? OrderDirection.Buy : OrderDirection.Sell);
                                    Logger.Info($"Moved limit order {dir.ToString().ToLower()} for [{te.Future.Ticker}] Quontity={maxlotnumber} Price={targetprice} Deviation {te.Diff:F2}% - take futures");
                                    var res = Client.Orders.ReplaceOrder(new ReplaceOrderRequest
                                    {
                                        AccountId = Account.Id,
                                        OrderId = order.OrderId,
                                        PriceType = PriceType.Point,
                                        IdempotencyKey = $"FUTARB_{Guid.NewGuid().ToShortGuid()}",
                                        Price = Helper.ToQuotation(targetprice),
                                        Quantity = maxlotnumber
                                    });
                                    LastLevellingTime = DateTime.Now;
                                }
                                else
                                    AddLog("Replaced order");
                            }
                            orderplaced = true;
                            continue;
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn(ex.Message);
                            Logger.Error(ex.StackTrace);
                        }
                    }
                    if (!orderplaced)
                        ordersforremove.Add(order);
                }
                foreach (var order in ordersforremove)
                    RemoveOrder(te.Future, ordersforremove, order);
                if (!orderplaced)
                {
                    try
                    {
                        if (maxlotnumber > 0)
                        {
                            if (PlaceOrders)
                            {
                                var dir = te.OpenPositionLimitBuy ? OrderDirection.Buy : OrderDirection.Sell;
                                Logger.Info($"Order limit {dir.ToString().ToLower()} for [{te.Future.Ticker}] Quontity={maxlotnumber} Price={targetprice} Deviation {te.Diff:F2}% - take futures");
                                try
                                {
                                    Client.Orders.PostOrder(new PostOrderRequest
                                    {
                                        AccountId = Account.Id,
                                        Direction = dir,
                                        InstrumentId = te.Future.Uid,
                                        OrderType = OrderType.Limit,
                                        Price = Helper.ToQuotation(targetprice),
                                        Quantity = maxlotnumber,
                                        PriceType = PriceType.Point,
                                        TimeInForce = TimeInForceType.TimeInForceDay,
                                        OrderId = $"FUTARB_{Guid.NewGuid().ToShortGuid()}"
                                    });
                                    LastLevellingTime = DateTime.Now;
                                    orderplaced = true;
                                }
                                catch (Exception ex)
                                {
                                    Logger.Warn(ex.Message);
                                    Logger.Error(ex.StackTrace);
                                }
                            }
                            else
                                AddLog("Placed limit order");
                        }
                    }
                    catch (Exception ex) { }
                }
            }
        }
        private void RemoveOrder(Future future, List<OrderState> ordersforremove, OrderState order)
        {
            try
            {
                var am = GetAvailableMoney();
                Client.Orders.CancelOrder(new CancelOrderRequest { AccountId = Account.Id, OrderId = order.OrderId });
                Logger.Info($"Canceled deprecated order for [{future.Ticker}] instrument not in traiding range");
                ordersforremove.Add(order);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex.Message);
                Logger.Error(ex.StackTrace);
            }
        }

        private void UpdateOrdersSubscriptions()
        {
            ordersstream?.Dispose();
            ordersstream = null;
            var orsreq = new OrderStateStreamRequest() { PingDelayMillis = 5000 };
            orsreq.Accounts.Add(Account.Id);
            ordersstream = Client.OrdersStream.OrderStateStream(orsreq);
            ProcessOrdersData();
            OrdersStreamPingTime = DateTime.Now;
        }
        private async void ProcessOrdersData()
        {
            try
            {
                await foreach (var resp in ordersstream.ResponseStream.ReadAllAsync())
                {
                    if (!IsRunning || BasicDataIsUpdating)
                        continue;
                    switch (resp.PayloadCase)
                    {
                        case OrderStateStreamResponse.PayloadOneofCase.Ping:
                            break;
                        case OrderStateStreamResponse.PayloadOneofCase.OrderState:
                            break;
                        default:
                            break;
                    }
                    OrdersStreamPingTime = DateTime.Now;
                }
            }
            catch (Exception ex)
            { }
        }
        private void UpdateUserTradesSubscriptions()
        {
            usertradesstream?.Dispose();
            usertradesstream = null;
            var trsreq = new TradesStreamRequest() { PingDelayMs = 5000 };
            trsreq.Accounts.Add(Account.Id);
            usertradesstream = Client.OrdersStream.TradesStream(trsreq);
            ProcessUserTradesData();
            UserTradesStreamPingTime = DateTime.Now;
        }
        private async void ProcessUserTradesData()
        {
            try
            {
                await foreach (var resp in usertradesstream.ResponseStream.ReadAllAsync())
                {
                    if (!IsRunning || BasicDataIsUpdating)
                        continue;
                    switch (resp.PayloadCase)
                    {
                        case TradesStreamResponse.PayloadOneofCase.Ping:
                            break;
                        case TradesStreamResponse.PayloadOneofCase.OrderTrades:
                            break;
                        default:
                            break;
                    }
                    MarketDataStreamPingTime = DateTime.Now;
                }
            }
            catch (Exception ex)
            { }
        }

        public void Dispose()
        {
            Stop();
        }
        public class FutureStatisticsPresenter : INotifyPropertyChanged
        {
            public event PropertyChangedEventHandler PropertyChanged;
            private void NotifyPropertyChanged([CallerMemberName] String propertyName = "")
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }
            public string FutureTicker { get; set; }
            public RobotFuturesArbitr Robot { get; set; }
            public string DisplayInfo { get => ToString(); }
            public void DataHasChanged()
            {
                NotifyPropertyChanged(nameof(DisplayInfo));
            }
            public override string ToString()
            {
                if (Robot == null)
                    return "[no robot assigned]";
                if (!Robot.AllFutures.ContainsKey(FutureTicker))
                    return "[nof future found]";
                var future = Robot.AllFutures[FutureTicker];
                var share = Robot.FutureShare[FutureTicker];
                var ShareTicker = share.Ticker;
                HistoricalOrderBook obf = null;
                HistoricalOrderBook obs = null;
                if (Robot.LastOrderbooks.ContainsKey(FutureTicker))
                    obf = Robot.LastOrderbooks[FutureTicker];
                if (Robot.LastOrderbooks.ContainsKey(ShareTicker))
                    obs = Robot.LastOrderbooks[ShareTicker];
                double? deviationbuy = null;
                double? deviationbuyout = null;
                double? deviationsell = null;
                double? deviationsellout = null;
                Trade lbt = null;
                Trade lst = null;
                if (Robot.LastBuyTrades.ContainsKey(FutureTicker))
                    lbt = Robot.LastBuyTrades[FutureTicker];
                if (Robot.LastSellTrades.ContainsKey(FutureTicker))
                    lst = Robot.LastSellTrades[FutureTicker];


                if (obs != null && obf != null && obs.Entries.Any(u => u.Quontity < 0) && obs.Entries.Any(u => u.Quontity > 0) && obf.Entries.Any(u => u.Quontity < 0) && obf.Entries.Any(u => u.Quontity > 0))
                {
                    var minpriceincrement = Helper.FromQuotation(future.MinPriceIncrement);
                    var sharemaxbid = obs.Entries.Last(u => u.Quontity < 0).Price;
                    var shareminask = obs.Entries.First(u => u.Quontity > 0).Price;
                    var futuremaxbid = obf.Entries.Last(u => u.Quontity < 0).Price;
                    var futureminask = obf.Entries.First(u => u.Quontity > 0).Price;
                    var limitbuydeviation = sharemaxbid / (futuremaxbid + minpriceincrement);
                    var limitselldeviation = shareminask / (futureminask - minpriceincrement);
                    var balancepositiveoutdeviation = shareminask / futuremaxbid;
                    var balancenegativeoutdeviation = sharemaxbid / futureminask;

                    deviationbuy = Robot.AverageDeviationsBuy.ContainsKey(FutureTicker) ? (limitbuydeviation - Robot.AverageDeviationsBuy[FutureTicker]) / Robot.AverageDeviationsBuy[FutureTicker] * 100d : null;
                    deviationbuyout = Robot.AverageDeviationsSell.ContainsKey(FutureTicker) ? (balancepositiveoutdeviation - Robot.AverageDeviationsSell[FutureTicker]) / Robot.AverageDeviationsSell[FutureTicker] * 100d : null;
                    deviationsell = Robot.AverageDeviationsBuy.ContainsKey(FutureTicker) ? (limitselldeviation - Robot.AverageDeviationsBuy[FutureTicker]) / Robot.AverageDeviationsBuy[FutureTicker] * 100d : null;
                    deviationsellout = Robot.AverageDeviationsSell.ContainsKey(FutureTicker) ? (balancenegativeoutdeviation - Robot.AverageDeviationsSell[FutureTicker]) / Robot.AverageDeviationsSell[FutureTicker] * 100d : null;
                }
                double? flp = null;
                double? slp = null;
                if (Robot.LastPrices.ContainsKey(FutureTicker))
                    flp = Helper.FromQuotation(Robot.LastPrices[FutureTicker].Price);
                if (Robot.LastPrices.ContainsKey(ShareTicker))
                    slp = Helper.FromQuotation(Robot.LastPrices[ShareTicker].Price);
                double? futuremoney = null;
                long? futurequontity = null;
                double? sharemoney = null;
                long? sharequontity = null;
                double? summ = null;
                if (flp.HasValue && Robot.MyFutures != null)
                    foreach (var f in Robot.MyFutures.Where(u => u.InstrumentUid == Robot.TickersUids[FutureTicker]))
                    {
                        futurequontity = f.Balance + f.Blocked;
                        futuremoney = futurequontity.Value / Helper.FromQuotation(future.MinPriceIncrement) * Helper.FromQuotation(future.MinPriceIncrementAmount, 1) * flp.Value;
                    }
                if (slp.HasValue && Robot.MyShares != null)
                    foreach (var s in Robot.MyShares.Where(u => u.InstrumentUid == Robot.TickersUids[ShareTicker]))
                    {
                        sharequontity = s.Balance + s.Blocked;
                        sharemoney = sharequontity.Value * slp.Value;
                    }
                if (futuremoney.HasValue && sharemoney.HasValue)
                    summ = futuremoney + sharemoney;
                string dividends = "";
                if (Robot.Dividends.ContainsKey(ShareTicker))
                    foreach (var div in Robot.Dividends[ShareTicker].Where(u => (u.LastBuyDate.ToDateTime() - DateTime.Now.ToUniversalTime()).Duration() < TimeSpan.FromDays(Robot.AvoidDividendsDays)))
                        dividends += $"\r\nДивиденты {div.LastBuyDate.ToDateTime().ToLocalTime():d} Сумма {Helper.FromMoneyValue(div.DividendNet):C} {Helper.FromQuotation(div.YieldValue)}%";
                if (!string.IsNullOrWhiteSpace(dividends))
                    dividends = $"<red>{dividends}</red>";
                return @$"<blue>[{FutureTicker}] {future.Name} </blue> Pair sum <blue>{summ:C}</blue>
Deviation buy <green>{deviationbuy:F2}%</green> out <green>{deviationbuyout:F2}%</green> sell <red>{deviationsell:F2}%</red> out <red>{deviationsellout:F2}%</red>
FLP <blue>{Helper.FromQuotation(Robot.LastPrices[FutureTicker].Price):F0}</blue> SLP <blue>{Helper.FromQuotation(Robot.LastPrices[ShareTicker].Price):C}</blue>
Future Quontity <green>{futurequontity}</green> Money <green>{futuremoney:C}</green> 
Share Quontity <green>{sharequontity}</green> Money <green>{sharemoney:C}</green>{dividends}";
            }
        }
    }

}
