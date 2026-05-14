using Google.Protobuf.WellKnownTypes;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Tinkoff.InvestApi;
using Tinkoff.InvestApi.V1;
using CommonClasses;
using NLog;

namespace RobotMovingAverageTrading
{
    /// <summary>
    /// Торговый робот по направлению набора EMA. Работает с выбранной акцией,
    /// подбирает ближайший подходящий фьючерс на базовый актив и приводит позицию
    /// к рассчитанному направлению с учетом гарантийного обеспечения.
    /// </summary>
    public class RobotMovingAverage : RobotBase
    {
        private static readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();
        public override Logger Logger { get => _logger; }
        public override RobotBaseSettings Settings 
        { 
            get => RobotSettings;
            set
            {
                RobotSettings = value as MovingAverageSettings;
                NotifyPropertyChanged(nameof(TitleText));
                NotifyPropertyChanged(nameof(Status));
                NotifyPropertyChanged(nameof(Margin));
                NotifyPropertyChanged(nameof(Positions));
            }
        }
        private MovingAverageSettings RobotSettings { get => MovingAverageSettings.Default; set => MovingAverageSettings.Default = value; }
        public override string TitleText { get => $"{this.GetType().Name} {Settings.Ticker} {Settings.TimeFrame}"; }
        public HistoricalTimeFrame TimeFrame { get; set; }
        private Tinkoff.InvestApi.V1.CandleInterval _candleinterval { get; set; }
        public uint MALen { get; set; } = 4;
        public uint MAStart { get; set; } = 4;
        public TimeSpan FutureSwitchPeriod { get; set; } = TimeSpan.FromDays(15);
        private uint calculatedMAStep = 8;
        public uint CalculatedMAStep
        {
            get => calculatedMAStep;
            private set
            {
                if (calculatedMAStep != value)
                {
                    calculatedMAStep = value;
                    NotifyPropertyChanged();
                    NotifyPropertyChanged(nameof(Status));
                }
            }
        }
        public uint MAStep { get; set; }
        public uint ATRLength
        {
            get; set;
        } = 900;
        public double DepoUsage { get; set; } = 0.8d;
        public double MaxRiskMultiplicator { get; set; } = 4d;
        public TimeSpan CheckInterval { get; set; } = new TimeSpan(0, 7, 0);
        HistoricalData BaseData { get; set; }
        InvestApiClient Client;
        List<Tinkoff.InvestApi.V1.Future> AllFutures;
        Tinkoff.InvestApi.V1.Future Future;
        Tinkoff.InvestApi.V1.Share Share;
        Account Account;
        private Timer _tim;

        public override void Start()
        {
            try
            {
                // Значения копируются из persisted-настроек в рабочие поля перед запуском таймера.
                MAStart = RobotSettings.MAStart;
                MAStep = RobotSettings.MAStep;
                MALen = RobotSettings.MALen;
                DepoUsage = RobotSettings.DepoUsage;
                MaxRiskMultiplicator = RobotSettings.MaxRiskMultiplicator;
                FutureSwitchPeriod = RobotSettings.FutureSwitchPeriod;

                Logger.Info($"Starting trader for {RobotSettings.Ticker} on {RobotSettings.TimeFrame}");
                TimeFrame = RobotSettings.TimeFrame;
                TimeSpan ts = new TimeSpan(1, 0, 0);
                Client = InvestApiClientFactory.Create(WindowsCredentialManager.ReadSecret(RobotSettings.ApiKey) ?? "key not found");
                _candleinterval = Helper.HistoricalTimeFrameToCandleInterval(TimeFrame);
                Client.Users.GetAccounts();
                var req = new GetAccountsRequest();
                Account = Client.Users.GetAccounts().Accounts.First();
                Share = Client.Instruments.Shares().Instruments.FirstOrDefault(u => u.Ticker == RobotSettings.Ticker);
                if (Share != null)
                {
                    if (BaseData == null || BaseData.Figi != Share.Figi)
                        BaseData = HistoricalData.ReadHistoricalData(Share.Ticker, Share.Figi, RobotSettings.TimeFrame, false, new HistoricalData.QueryDataDelegate((figi, timeframe, from, to) =>
                        {
                            var candles = Client.MarketData.GetCandles(new Tinkoff.InvestApi.V1.GetCandlesRequest { InstrumentId = Share.Uid, Interval = Helper.HistoricalTimeFrameToCandleInterval(timeframe), From = from.ToUniversalTime().ToTimestamp(), To = to.ToUniversalTime().ToTimestamp() });
                            return candles.Candles.Where(u => u.IsComplete).Select(u => new HistoricalCandle
                            {
                                Low = Helper.FromQuotation(u.Low),
                                High = Helper.FromQuotation(u.High),
                                Open = Helper.FromQuotation(u.Open),
                                Close = Helper.FromQuotation(u.Close),
                                Volume = u.Volume,
                                Time = u.Time.ToDateTime()
                            }).ToList();
                        }));
                    BaseData.GetData(DateTime.Now.AddDays(-365), DateTime.Now);
                    if (BaseData.DataHasChanges)
                        BaseData.SaveHistoricalData();
                    TimerCallback tm = new TimerCallback(Process);
                    _tim = new Timer(tm, this, 0, 2000);
                    NotifyPropertyChanged(nameof(IsRunning));
                }
                else
                    _tim = null;
            }

            catch (Exception ex)
            {
                Logger.Warn(ex.Message);
                Logger.Error(ex.InnerException);
            }
        }
        public override bool IsRunning
        {
            get { return _tim != null; }
        }
        public override void Stop()
        {
            if (IsRunning)
            {
                if (BaseData.DataHasChanges)
                    BaseData.SaveHistoricalData();
                _tim.Dispose();
                _tim = null;
                BalanceAchieved = false;
                LastCommonDataRequestTime = DateTime.MinValue;
                NotifyPropertyChanged(nameof(IsRunning));
                Logger.Info($"Trader stopped");
            }
        }

        /// <summary>
        /// Оценивает направление по последовательности EMA: чем больше соседних средних
        /// подтверждают один наклон, тем сильнее итоговый сигнал.
        /// </summary>
        private double GetDirection(HistoricalData data)
        {
            HistoricalCandle candle = data.Candles.Last();
            if (Share.Ticker == "SBER")
            {//SBER
                var atr = data.GetATRW(candle, 900);// / c.Open * 290d;
                atr = atr == 0 ? 1 : atr;
                CalculatedMAStep = Convert.ToUInt16(Math.Round(MAStep / atr));
            }
            else if (Share.Ticker == "GAZP")
            {//GAZP
                CalculatedMAStep = MAStep;
            }
            else
            {
                CalculatedMAStep = MAStep;
            }
            List<uint> uplist = new List<uint>();
            List<uint> dnlist = new List<uint>();
            for (uint i = 0; i < MALen; i++)
            {
                uplist.Add(i);
                dnlist.Add(i);
            }
            for (uint i = 0; i < MALen; i++)
            {
                var diff = data.GetEMA(candle, Convert.ToUInt16((MAStart + i) * CalculatedMAStep)) - data.GetEMA(candle, Convert.ToUInt16((MAStart + i - 1) * CalculatedMAStep));
                if (diff > 0)
                {
                    if (uplist.Contains(i))
                        uplist.Remove(i);
                    if (uplist.Contains(i - 1))
                        uplist.Remove(i - 1);
                }
                else if (diff < 0)
                {
                    if (dnlist.Contains(i))
                        dnlist.Remove(i);
                    if (dnlist.Contains(i - 1))
                        dnlist.Remove(i - 1);
                }
            }
            return (uplist.Count > dnlist.Count ? uplist.Count : (uplist.Count == dnlist.Count ? 0d : -dnlist.Count))/MALen;
        }
        bool BalanceAchieved = false;
        public double GetDeposit()
        {
            return 0;
        }
        private List<OrderState> GetOrders()
        {
            return Client.Orders.GetOrders(new Tinkoff.InvestApi.V1.GetOrdersRequest() {  AccountId = Account.Id }).Orders.Where(u => AllFutures.Select(f => f.Uid).Contains(u.InstrumentUid) && u.LotsRequested != u.LotsExecuted).ToList();
        }
        private void CancelOrders(IEnumerable<OrderState> orders)
        {
            foreach (var order in orders)
                Client.Orders.CancelOrder(new CancelOrderRequest() { OrderId = order.OrderId, AccountId = Account.Id });
        }
        Dictionary<string, double> FuturesMarginSell = new Dictionary<string, double>();
        Dictionary<string, double> FuturesMarginBuy = new Dictionary<string, double>();
        private double lastDir = 0;

        public double LastDir 
        { 
            get => lastDir; 
            set 
            { 
                lastDir = value; 
                NotifyPropertyChanged();
                NotifyPropertyChanged(nameof(Status));
            } 
        }
        public DateTime LastDirTime = DateTime.MinValue;
        public List<string> Positions { get; set; } = new List<string>();
        public MarginAttributes Margin { get; set; } = new MarginAttributes();
        public override string Status
        {
            get => $"{Settings.DisplayName} - Current signal {LastDir} at {LastDirTime.ToLocalTime()}; Step = {CalculatedMAStep}\r\nParameters: {Settings.ToString()}";
        }
        private bool isprocessing = false;
        private DateTime LastCommonDataRequestTime = DateTime.MinValue;
        private Dictionary<string, bool> LastTradingStatuses = new Dictionary<string, bool>();
        public void Process(object? o)
        {
            if (isprocessing)
                return;
            try
            {
                isprocessing = true;
                if (DateTime.Now - LastCommonDataRequestTime > TimeSpan.FromMinutes(1))
                {
                    // Редкие справочные запросы обновляют список фьючерсов и статусы торгов,
                    // а быстрый цикл ниже работает уже с последними ценами и позициями.
                    AllFutures = Client.Instruments.Futures().Instruments.Where(u => u.BasicAsset == Share.Ticker).ToList();
                    Future = AllFutures.Where(u => u.ExpirationDate.ToDateTime() - DateTime.Now > FutureSwitchPeriod).OrderBy(u => u.ExpirationDate).FirstOrDefault();
                    GetTradingStatusesRequest lasttradingstatusesrequest = new GetTradingStatusesRequest();
                    lasttradingstatusesrequest.InstrumentId.AddRange(AllFutures.Select(u => u.Uid));
                    lasttradingstatusesrequest.InstrumentId.Add(Share.Uid);
                    LastTradingStatuses = Client.MarketData.GetTradingStatuses(lasttradingstatusesrequest).TradingStatuses.Select(u => new KeyValuePair<string, bool>(u.InstrumentUid, u.TradingStatus == SecurityTradingStatus.NormalTrading)).ToDictionary();
                    LastCommonDataRequestTime = DateTime.Now;
                }
                if (Future == null)
                    return;
                FuturesMarginSell = new Dictionary<string, double>();
                FuturesMarginBuy = new Dictionary<string, double>();

                var pricereq = new GetLastPricesRequest();
                pricereq.InstrumentId.AddRange(AllFutures.Select(u => u.Uid));
                var lastprices = Client.MarketData.GetLastPrices(pricereq).LastPrices.Select(u => new KeyValuePair<string,double> (u.InstrumentUid, Helper.FromQuotation(u.Price))).ToDictionary();

                foreach (var f in AllFutures)
                {
                    var lastpriceamount = lastprices[f.Uid] / Helper.FromQuotation(f.MinPriceIncrement) * (f.MinPriceIncrementAmount == null ? 1d : Helper.FromQuotation(f.MinPriceIncrementAmount));
                    var marginbuy = Math.Max(Helper.FromQuotation(f.DlongClient)/DepoUsage,1d/MaxRiskMultiplicator);
                    var marginsell = Math.Max(Helper.FromQuotation(f.DshortClient)/DepoUsage,1d/MaxRiskMultiplicator);
                    FuturesMarginBuy.Add(f.Uid,  lastpriceamount*marginbuy);
                    FuturesMarginSell.Add(f.Uid, lastpriceamount*marginsell);
                }
                CancelOrders(GetOrders());
                var accountpositions = Client.Operations.GetPositions(new PositionsRequest() { AccountId = Account.Id });
                var positions = accountpositions.Futures.Where(u => AllFutures.Select(f => f.Uid).Contains(u.InstrumentUid)).ToList();
                var balances = AllFutures.Join(positions, futures => futures.Uid, positions => positions.InstrumentUid, (f, k) => new { Future = f, Position = k.Balance }).ToList();
                var shareposition = accountpositions.Securities.FirstOrDefault(u => u.InstrumentUid == Share.Uid);
                long sharequontity = 0;
                if (shareposition != null)
                    sharequontity = shareposition.Balance;
                Positions = balances.OrderBy(u => u.Future.Ticker).Select(u => $"[{u.Future.Ticker}] {u.Future.Name}\r\n\tQuontity = {u.Position} Sum = {u.Position / Helper.FromQuotation(u.Future.MinPriceIncrement) * Helper.FromQuotation(u.Future.MinPriceIncrementAmount) * lastprices[u.Future.Uid]:C}").ToList();
                Margin = new MarginAttributes(Client.Users.GetMarginAttributes(new GetMarginAttributesRequest { AccountId = Account.Id }));
                AddDepoValue(Margin.LiquidPortfolio, DateTime.Now);
                BaseData.GetData(BaseData.Candles.Last().Time, DateTime.Now);
                var cdata = new HistoricalData();
                cdata.Candles = new List<HistoricalCandle>(BaseData.Candles.TakeLast(1000).Select(u => new HistoricalCandle(u)).ToList());
                
                var dividendts = Client.Instruments.GetDividends(new GetDividendsRequest { InstrumentId = Share.Uid, From = cdata.Candles.First().Time.ToUniversalTime().ToTimestamp(), To = cdata.Candles.Last().Time.ToUniversalTime().ToTimestamp() }).Dividends;
                foreach (var c in cdata.Candles)
                {
                    var addon = dividendts.Where(u => c.Time < u.LastBuyDate.ToDateTime().AddDays(1)).Sum(u => Helper.FromQuotation(u.YieldValue) / 100d);
                    c.Low /= (1 + addon);
                    c.High /= (1 + addon);
                    c.Open /= (1 + addon);
                    c.Close /= (1 + addon);
                }
                double dir = GetDirection(cdata);

                if (dir != LastDir)
                {
                    BalanceAchieved = false;
                    LastDirTime = DateTime.Now;
                    LastDir = dir;
                }
                else if (balances.Count(u => u.Future.Uid != Future.Uid) > 0)
                    BalanceAchieved = false;
                else if (Margin.AmountOfMissingFunds > 0)
                    BalanceAchieved = false;
                else if (sharequontity != 0)
                    BalanceAchieved = false;
                bool prelevellingdone = false;
                if (!BalanceAchieved)
                {
                    if (sharequontity != 0)
                    {
                        if (LastTradingStatuses[Share.Uid])
                        {
                            var orderbook = Client.MarketData.GetOrderBook(new GetOrderBookRequest() { InstrumentId = Share.Uid, Depth = 10 });
                            long maxquontity = 0;
                            if (sharequontity < 0)
                                maxquontity = orderbook.Bids.Take(1).Sum(u => u.Quantity);
                            else
                                maxquontity = orderbook.Asks.Take(1).Sum(u => u.Quantity);
                            var quontity = Math.Abs(Math.Min(Math.Abs(sharequontity / Share.Lot), maxquontity));
                            if (quontity > 0)
                            {
                                var direction = (sharequontity < 0 ? OrderDirection.Buy : OrderDirection.Sell);
                                Logger.Info($"Order {Share.Ticker} signal:{dir} quantity: {quontity} directction: {direction}");
                                Client.Orders.PostOrder(
                                        new PostOrderRequest()
                                        {
                                            AccountId = Account.Id,
                                            Direction = direction,
                                            InstrumentId = Share.Uid,
                                            OrderType = OrderType.Market,
                                            Quantity = quontity,
                                            OrderId = $"RT_{Share.Ticker}_{Guid.NewGuid().ToShortGuid()}"
                                        });
                                prelevellingdone = true;
                            }
                        }
                    }
                    foreach (var b in balances)
                    {
                        if (Math.Sign(dir) != Math.Sign(b.Position) || b.Future.Uid != Future.Uid)
                        {
                            if (LastTradingStatuses[b.Future.Uid])
                            {
                                var orderbook = Client.MarketData.GetOrderBook(new GetOrderBookRequest() { InstrumentId = b.Future.Uid, Depth = 10 });
                                long maxquontity = 0;
                                if (b.Position < 0)
                                    maxquontity = orderbook.Bids.Take(1).Sum(u => u.Quantity);
                                else
                                    maxquontity = orderbook.Asks.Take(1).Sum(u => u.Quantity);
                                var quontity = Math.Abs(Math.Min(Math.Abs(b.Position), maxquontity));
                                if (quontity > 0)
                                {
                                    var direction = (b.Position < 0 ? OrderDirection.Buy : OrderDirection.Sell);
                                    Logger.Info($"Order {b.Future.Ticker} signal:{dir} quantity: {quontity} directction: {direction}");
                                    Client.Orders.PostOrder(
                                            new PostOrderRequest()
                                            {
                                                AccountId = Account.Id,
                                                Direction = direction,
                                                InstrumentId = b.Future.Uid,
                                                OrderType = OrderType.Market,
                                                Quantity = quontity,
                                                OrderId = $"RT_{Share.Ticker}_{Guid.NewGuid().ToShortGuid()}"
                                            });
                                }
                            }
                            prelevellingdone = true;
                            break;
                        }
                    }
                    if (!prelevellingdone)
                    {
                        double Depo = Margin.LiquidPortfolio;
                        long Positions = Math.Abs(balances.Sum(u => u.Position));
                        long NeededPositions = Convert.ToInt64(Math.Floor((Depo / (dir > 0 ? FuturesMarginBuy[Future.Uid] : (dir < 0 ? FuturesMarginSell[Future.Uid] : 0)) * Math.Abs(dir))));
                        long Delta = NeededPositions - Math.Abs(Positions);
                        if (Delta == 0)
                            BalanceAchieved = true;
                        else if (LastTradingStatuses[Future.Uid])
                        {
                            if (dir < 0)
                                Delta = -Delta;
                            var orderbook = Client.MarketData.GetOrderBook(new GetOrderBookRequest() { InstrumentId = Future.Uid, Depth = 10 });
                            long maxquantity = 0;
                            if (Delta < 0)
                                maxquantity = orderbook.Bids.Take(1).Sum(u => u.Quantity);
                            else
                                maxquantity = orderbook.Asks.Take(1).Sum(u => u.Quantity);
                            var quontity = Math.Abs(Math.Min(Math.Abs(Delta), maxquantity));
                            if (quontity > 0)
                            {
                                var direction = (Delta < 0 ? OrderDirection.Sell : OrderDirection.Buy);
                                Logger.Info($"Order {Future.Ticker} signal:{dir} quantity: {quontity} directction: {direction}");
                                Client.Orders.PostOrder(
                                                new PostOrderRequest()
                                                {
                                                    AccountId = Account.Id,
                                                    Direction = direction,
                                                    InstrumentId = Future.Uid,
                                                    OrderType = OrderType.Market,
                                                    Quantity = quontity,
                                                    OrderId = $"RT_{Share.Ticker}_{Guid.NewGuid().ToShortGuid()}"
                                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex.Message);
                Logger.Error(ex.StackTrace);
            }
            finally
            {
                isprocessing = false;
                NotifyPropertyChanged("Positions");
                NotifyPropertyChanged("Margin");
            }
        }
    }
}
