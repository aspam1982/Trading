using Google.Protobuf.WellKnownTypes;
using Newtonsoft.Json;
using NLog.Targets;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using Tinkoff.InvestApi.V1;

namespace CommonClasses
{
    /// <summary>
    /// Локальный кэш исторических свечей с ленивой догрузкой недостающих интервалов.
    /// Класс также содержит расчет технических индикаторов, используемых роботами
    /// и backtest-экранами: MA, EMA, RSI, ATR, Heiken Ashi, Supertrend.
    /// </summary>
    public class HistoricalData
    {
        private static string DefaultDir = Path.Combine(Directory.GetCurrentDirectory(), "CandleData");
        public string Ticker { get; set; }
        public string Figi { get; set; }
        public HistoricalTimeFrame TimeFrame { get; set; }
        [Newtonsoft.Json.JsonIgnore]
        public bool DataHasChanges { get; private set; } = false;
        public List<HistoricalCandle> Candles { get; set; } = new List<HistoricalCandle>();
        public DateInterval Interval { get; set; } = null;
        public bool Locked { get; set; } = false;
        public HistoricalData()
        {

        }
        private ConcurrentDictionary<uint, ConcurrentDictionary<HistoricalCandle, double>> _MA = new ConcurrentDictionary<uint, ConcurrentDictionary<HistoricalCandle, double>>();
        private ConcurrentDictionary<uint, ConcurrentDictionary<HistoricalCandle, double>> _EMA = new ConcurrentDictionary<uint, ConcurrentDictionary<HistoricalCandle, double>>();
        private ConcurrentDictionary<uint, ConcurrentDictionary<HistoricalCandle, double>> _RSI = new ConcurrentDictionary<uint, ConcurrentDictionary<HistoricalCandle, double>>();
        private ConcurrentDictionary<uint, ConcurrentDictionary<HistoricalCandle, double>> _ATR = new ConcurrentDictionary<uint, ConcurrentDictionary<HistoricalCandle, double>>();
        private ConcurrentDictionary<uint, ConcurrentDictionary<HistoricalCandle, double>> _ATRW = new ConcurrentDictionary<uint, ConcurrentDictionary<HistoricalCandle, double>>();
        private ConcurrentDictionary<HistoricalCandle, HistoricalCandle> _HeikenAishi = new ConcurrentDictionary<HistoricalCandle, HistoricalCandle>();
        /// <summary>
        /// Возвращает Heiken Ashi-свечу для исходной свечи, кэшируя последовательный расчет.
        /// </summary>
        public HistoricalCandle GetHeikenAishi(HistoricalCandle candle)
        {
            if (!_HeikenAishi.ContainsKey(candle))
            {
                int idx = Candles.IndexOf(candle);
                int start = idx;
                for (int i = idx - 1; i >= 0; i--)
                {
                    start = i;
                    if (_HeikenAishi.ContainsKey(Candles[i]))
                        break;
                }
                for (int i = start; i < idx; i++)
                    GetHeikenAishi(Candles[i]);
                HistoricalCandle prevHeikenAishi = idx <= 0 ? new HistoricalCandle(candle) : GetHeikenAishi(Candles[idx - 1]);
                var close = (candle.Open + candle.Close + candle.Low + candle.High) / 4d;
                var open = (prevHeikenAishi.Open + prevHeikenAishi.Close) / 2d;
                var high = Math.Max(Math.Max(open, close), candle.High);
                var low = Math.Min(Math.Min(open, close), candle.Low);
                _HeikenAishi.TryAdd(candle, new HistoricalCandle
                {
                    Close = close,
                    Open = open,
                    High = high,
                    Low = low,
                    Time = candle.Time,
                    Volume = candle.Volume
                });
            }
            return _HeikenAishi[candle];
        }
        public double GetMA(HistoricalCandle candle, uint maorder)
        {
            if (!_MA.ContainsKey(maorder))
                _MA.TryAdd(maorder, new ConcurrentDictionary<HistoricalCandle, double>());
            var newdic = _MA[maorder];
            if (!newdic.ContainsKey(candle))
            {
                int idx = Candles.IndexOf(candle);
                int cnt = 0;
                double summ = 0;
                for (int i = 0; i < maorder; i++)
                {
                    int idxnew = idx - i;
                    if (idxnew < 0)
                        break;

                    cnt++;
                    summ += Candles[idxnew].Close;
                }
                newdic.TryAdd(candle, summ / cnt);
            }
            return newdic[candle];
        }
        public double GetATR(HistoricalCandle candle, uint maorder)
        {
            if (!_ATR.ContainsKey(maorder))
                _ATR.TryAdd(maorder, new ConcurrentDictionary<HistoricalCandle, double>());
            var newdic = _ATR[maorder];
            if (!newdic.ContainsKey(candle))
            {
                int idx = Candles.IndexOf(candle);
                int cnt = 0;
                double summ = 0;
                for (int i = 1; i < maorder; i++)
                {
                    int idxnew = idx - i;
                    if (idxnew < 0)
                        break;

                    cnt++;
                    summ += (Candles[idxnew].High - Candles[idxnew].Low);
                }
                if (cnt == 0)
                    newdic.TryAdd(candle, candle.High - candle.Low);
                else
                    newdic.TryAdd(candle, summ / cnt);
            }
            return newdic[candle];
        }
        public double GetATRW(HistoricalCandle candle, uint maorder)
        {
            if (!_ATRW.ContainsKey(maorder))
                _ATRW.TryAdd(maorder, new ConcurrentDictionary<HistoricalCandle, double>());
            var newdic = _ATRW[maorder];
            if (!newdic.ContainsKey(candle))
            {
                int idx = Candles.IndexOf(candle);
                int cnt = 0;
                double summ = 0;
                for (int i = 1; i < maorder; i++)
                {
                    int idxnew = idx - i;
                    if (idxnew < 0)
                        break;

                    cnt++;
                    summ += (Candles[idxnew].High - Candles[idxnew].Low) / Candles[idxnew].Open * 100d;
                }
                if (cnt == 0)
                    newdic.TryAdd(candle, (candle.High - candle.Low) / candle.Open);
                else
                    newdic.TryAdd(candle, summ / cnt);
            }
            return newdic[candle];
        }
        private ConcurrentDictionary<uint, object> _emaLocks = new();

        /// <summary>
        /// Возвращает EMA для свечи и периода. Значения считаются итеративно и кэшируются,
        /// чтобы повторные запросы из стратегий не пересчитывали весь ряд.
        /// </summary>
        public double GetEMA(HistoricalCandle candle, uint emaorder)
        {
            // Получаем или добавляем словарь для emaorder
            var emadic = _EMA.GetOrAdd(emaorder, _ => new ConcurrentDictionary<HistoricalCandle, double>());

            // Если значение для свечи ещё не рассчитано (что может быть только до полного расчёта)
            if (!emadic.ContainsKey(candle))
            {
                // Получаем lock для этого emaorder
                object emaLock = _emaLocks.GetOrAdd(emaorder, _ => new object());

                lock (emaLock)
                {
                    // Повторная проверка: возможно, другой поток уже рассчитал всё
                    if (!emadic.ContainsKey(candle))
                    {
                        // Предварительный итеративный расчёт для всех свечей (аналогично исходной логике, но без рекурсии)
                        double prevEMA = 0;
                        for (int i = 0; i < Candles.Count; i++)
                        {
                            HistoricalCandle curr = Candles[i];
                            // Пропускаем, если уже рассчитано (на случай частичного заполнения)
                            if (emadic.ContainsKey(curr)) continue;

                            double emaValue;
                            if (i == 0)
                            {
                                emaValue = curr.Open; // Начальное значение
                            }
                            else
                            {
                                // Берём предыдущее из кэша (гарантированно существует)
                                prevEMA = emadic[Candles[i - 1]];
                                emaValue = (curr.Open - prevEMA) * 2.0 / (emaorder + 1) + prevEMA;
                            }
                            emadic.TryAdd(curr, emaValue);
                        }
                    }
                }
            }

            return emadic[candle];
        }

        // Кэширующие словари (оставляем как было)
        private ConcurrentDictionary<uint, ConcurrentDictionary<HistoricalCandle, double>> _avgGain = new();
        private ConcurrentDictionary<uint, ConcurrentDictionary<HistoricalCandle, double>> _avgLoss = new();

        private ConcurrentDictionary<uint, object> _periodLocks = new(); // Добавляем для синхронизации по period

        /// <summary>
        /// Возвращает RSI по сглаживанию Уайлдера. Расчет синхронизирован по периоду,
        /// потому что роботы могут запрашивать индикаторы из таймеров/фоновых потоков.
        /// </summary>
        public double GetRSI(HistoricalCandle candle, uint period)
        {
            // Получаем или добавляем словари для данного period (thread-safe)
            var rsidic = _RSI.GetOrAdd(period, _ => new ConcurrentDictionary<HistoricalCandle, double>());
            var gaindic = _avgGain.GetOrAdd(period, _ => new ConcurrentDictionary<HistoricalCandle, double>());
            var lossdic = _avgLoss.GetOrAdd(period, _ => new ConcurrentDictionary<HistoricalCandle, double>());

            if (!rsidic.ContainsKey(candle))
            {
                // Получаем lock для этого period
                object periodLock = _periodLocks.GetOrAdd(period, _ => new object());

                lock (periodLock)
                {
                    // Проверяем снова внутри lock, чтобы избежать race
                    if (!rsidic.ContainsKey(candle))
                    {
                        int idx = Candles.IndexOf(candle);
                        if (idx < 0)
                            throw new ArgumentException("Candle not found in Candles list.");

                        // Итеративно рассчитываем все от 0 до idx, пропуская уже рассчитанные
                        for (int j = 0; j <= idx; j++)
                        {
                            HistoricalCandle curr = Candles[j];
                            if (rsidic.ContainsKey(curr)) continue;

                            if (j < (int)period)
                            {
                                rsidic[curr] = 50; // Недостаточно данных
                                continue;
                            }

                            // Расчет AvgGain/AvgLoss
                            double currAvgGain, currAvgLoss;

                            if (j == (int)period)
                            {
                                // 1. Расчет первых AvgGain/AvgLoss (SMA)
                                double sumGain = 0, sumLoss = 0;
                                for (int k = 1; k <= (int)period; k++)
                                {
                                    double delta = Candles[j - (int)period + k].Close - Candles[j - (int)period + k - 1].Close;
                                    sumGain += delta > 0 ? delta : 0;
                                    sumLoss += delta < 0 ? -delta : 0;
                                }
                                currAvgGain = sumGain / period;
                                currAvgLoss = sumLoss / period;
                            }
                            else
                            {
                                // 2. Сглаживание по Уайлдеру (Wilder Smoothing)
                                HistoricalCandle prev = Candles[j - 1];
                                // Поскольку итеративно, prev гарантированно рассчитан
                                double prevAvgGain = gaindic[prev];
                                double prevAvgLoss = lossdic[prev];
                                double delta = curr.Close - prev.Close;

                                currAvgGain = (prevAvgGain * (period - 1) + (delta > 0 ? delta : 0)) / period;
                                currAvgLoss = (prevAvgLoss * (period - 1) + (delta < 0 ? -delta : 0)) / period;
                            }

                            gaindic[curr] = currAvgGain;
                            lossdic[curr] = currAvgLoss;

                            // 3. Расчет RSI
                            if (currAvgLoss == 0)
                                rsidic[curr] = 100;
                            else
                            {
                                double rs = currAvgGain / currAvgLoss;
                                rsidic[curr] = 100 - (100 / (1 + rs));
                            }
                        }
                    }
                }
            }

            return rsidic[candle];
        }
        ConcurrentDictionary<(uint, uint), ConcurrentDictionary<int, double>> _Supertrend_UpperBands = new ConcurrentDictionary<(uint, uint), ConcurrentDictionary<int, double>>();
        ConcurrentDictionary<(uint, uint), ConcurrentDictionary<int, double>> _Supertrend_LowerBands = new ConcurrentDictionary<(uint, uint), ConcurrentDictionary<int, double>>();
        ConcurrentDictionary<(uint, uint), ConcurrentDictionary<int, double>> _Supertrend_Trends = new ConcurrentDictionary<(uint, uint), ConcurrentDictionary<int, double>>();
        ConcurrentDictionary<(uint, uint), ConcurrentDictionary<int, bool>> _Supertrend_TrendDirections = new ConcurrentDictionary<(uint, uint), ConcurrentDictionary<int, bool>>();

        private double GetUpperBand(int idx, uint atrlen, uint atrmultiplier)
        {
            var upperbands = _Supertrend_UpperBands.GetOrAdd((atrlen, atrmultiplier), _ => new ConcurrentDictionary<int, double>());

            return upperbands.GetOrAdd(idx, _ =>
            {
                var candle = Candles[idx];
                var prevcandle = idx == 0 ? candle : Candles[idx - 1];
                var atr = GetATR(candle, atrlen);
                var basicupperband = candle.HL2 + atrmultiplier * atr;
                var prevupperband = idx == 0 ? basicupperband : GetUpperBand(idx - 1, atrlen, atrmultiplier);
                var upperband = basicupperband < prevupperband || prevcandle.Close > prevupperband ? basicupperband : prevupperband;
                return upperband;
            });
        }

        private double GetLowerBand(int idx, uint atrlen, uint atrmultiplier)
        {
            var lowerbands = _Supertrend_LowerBands.GetOrAdd((atrlen, atrmultiplier), _ => new ConcurrentDictionary<int, double>());

            return lowerbands.GetOrAdd(idx, _ =>
            {
                var candle = Candles[idx];
                var prevcandle = idx == 0 ? candle : Candles[idx - 1];
                var atr = GetATR(candle, atrlen);
                var basiclowerband = candle.HL2 - atrmultiplier * atr;
                var prevlowerband = idx == 0 ? basiclowerband : GetLowerBand(idx - 1, atrlen, atrmultiplier);
                var lowerband = basiclowerband > prevlowerband || prevcandle.Close < prevlowerband ? basiclowerband : prevlowerband;
                return lowerband;
            });
        }

        public (double Trend, bool Direction) GetSupertrend(HistoricalCandle candle, uint atrlen, uint atrmultiplier)
        {
            var idx = Candles.IndexOf(candle);
            var trends = _Supertrend_Trends.GetOrAdd((atrlen, atrmultiplier), _ => new ConcurrentDictionary<int, double>());
            var directions = _Supertrend_TrendDirections.GetOrAdd((atrlen, atrmultiplier), _ => new ConcurrentDictionary<int, bool>());

            // Если значение уже есть, возвращаем его
            if (trends.TryGetValue(idx, out var existingTrend) && directions.TryGetValue(idx, out var existingDirection))
            {
                return (existingTrend, existingDirection);
            }

            // Вычисляем предыдущие значения если нужно
            int loopidx = idx;
            while (loopidx > 0 && !trends.ContainsKey(loopidx - 1))
                loopidx--;

            for (int i = loopidx; i < idx; i++)
                GetSupertrend(Candles[i], atrlen, atrmultiplier);

            var upperband = GetUpperBand(idx, atrlen, atrmultiplier);
            var lowerband = GetLowerBand(idx, atrlen, atrmultiplier);
            var trenddirection = false;
            var supertrend = upperband;

            if (idx > 0)
            {
                var prevcandle = Candles[idx - 1];
                var prevresult = GetSupertrend(prevcandle, atrlen, atrmultiplier);
                if (prevresult.Trend == upperband)
                    trenddirection = candle.Close > upperband;
                else
                    trenddirection = !(candle.Close < lowerband);
                supertrend = trenddirection ? lowerband : upperband;
            }

            // Потокобезопасное добавление
            trends[idx] = supertrend;
            directions[idx] = trenddirection;

            return (supertrend, trenddirection);
        }

        public HistoricalData(string ticker, string figi, HistoricalTimeFrame timeFrame, QueryDataDelegate onQueryData = null)
        {
            Ticker = ticker;
            Figi = figi;
            TimeFrame = timeFrame;
            OnQueryData = onQueryData;
        }

        public static HistoricalData ReadHistoricalData(string Ticker, string Figi, HistoricalTimeFrame TimeFrame, bool Locked, QueryDataDelegate onQueryData = null)
        {
            return ReadHistoricalData(DefaultDir, Ticker, Figi, TimeFrame, Locked, onQueryData);
        }
        public static HistoricalData ReadHistoricalData(string Dir, string Ticker, string Figi, HistoricalTimeFrame TimeFrame, bool Locked, QueryDataDelegate onQueryData = null)
        {
            string FullFileName = Path.Combine(Dir, String.Format("{0}_{1}_{2}.json", Ticker, Figi, TimeFrame));
            HistoricalData res = null;
            if (File.Exists(FullFileName))
                res = JsonConvert.DeserializeObject<HistoricalData>(File.ReadAllText(FullFileName, Encoding.UTF8));
            else
                res = new HistoricalData(Ticker, Figi, TimeFrame);
            res.Locked = Locked;
            res.OnQueryData = onQueryData;
            return res;
        }
        public void SaveHistoricalData(string Dir)
        {
            string FullFileName = Path.Combine(Dir, String.Format("{0}_{1}_{2}.json", this.Ticker, this.Figi, this.TimeFrame));
            if (!Directory.Exists(Dir))
                Directory.CreateDirectory(Dir);
            File.WriteAllText(FullFileName, JsonConvert.SerializeObject(this));
        }
        public void SaveHistoricalData()
        {
            DataHasChanges = false;
            SaveHistoricalData(DefaultDir);
        }
        public delegate List<HistoricalCandle> QueryDataDelegate(string Figi, HistoricalTimeFrame TimeFrame, DateTime DateStart, DateTime DateEnd);
        public event QueryDataDelegate OnQueryData;
        /// <summary>
        /// Возвращает свечи за период. Если запрошенный интервал выходит за границы кэша,
        /// метод догружает недостающие участки через OnQueryData и помечает данные измененными.
        /// </summary>
        public List<HistoricalCandle> GetData(DateTime DateStart, DateTime DateEnd)
        {
            DateInterval downloadinterval1 = null;
            DateInterval downloadinterval2 = null;
            if (DateStart.Kind == DateTimeKind.Local)
                DateStart = DateStart.ToUniversalTime();
            if (DateEnd.Kind == DateTimeKind.Local)
                DateEnd = DateEnd.ToUniversalTime();

            if (Interval == null)
                downloadinterval1 = new DateInterval(DateStart, DateEnd);
            else
            {
                if (DateStart > Interval.End)
                    downloadinterval2 = new DateInterval(Interval.End, DateEnd);
                else if (DateEnd < Interval.Start)
                    downloadinterval1 = new DateInterval(DateStart, Interval.End);
                else if (DateStart < Interval.Start && DateEnd > Interval.End)
                {
                    downloadinterval1 = new DateInterval(DateStart, Interval.Start);
                    downloadinterval2 = new DateInterval(Interval.End, DateEnd);
                }
                else if (DateStart < Interval.Start && DateEnd <= Interval.End)
                    downloadinterval1 = new DateInterval(DateStart, Interval.Start);
                else if (DateEnd > Interval.End && DateStart >= Interval.Start)
                    downloadinterval2 = new DateInterval(Interval.End, DateEnd);
            }
            bool hasnewcandles = false;
            if (downloadinterval1 != null)
            {
                var data = GetIntervalData(downloadinterval1.Start, downloadinterval1.End);
                if (data != null && data.Count > 0)
                {
                    var firstdate = Candles.Count > 0 ? Candles.First().Time : DateTime.MaxValue;
                    hasnewcandles = true;
                    Candles.InsertRange(0, data.Where((u) => u.Time < firstdate));
                }
            }
            if (downloadinterval2 != null)
            {
                var data = GetIntervalData(downloadinterval2.Start, downloadinterval2.End);
                if (data != null && data.Count > 0)
                {
                    hasnewcandles = true;
                    var lastdate = Candles.Last().Time;
                    Candles.AddRange(data.Where((u) => u.Time > lastdate));
                }
            }
            if (hasnewcandles)
            {
                Interval = new DateInterval(Candles.First().Time, Candles.Last().Time);
                DataHasChanges = true;
            }
            return Candles.Where(u => u.Time >= DateStart && u.Time <= DateEnd).ToList();
        }
        private List<HistoricalCandle> GetIntervalData(DateTime DateStart, DateTime DateEnd)
        {
            if (OnQueryData != null)
            {
                List<HistoricalCandle> res = new List<HistoricalCandle>();
                int maxdays;
                switch (TimeFrame)
                {
                    case HistoricalTimeFrame.M1: maxdays = 1; break;
                    case HistoricalTimeFrame.M2: maxdays = 1; break;
                    case HistoricalTimeFrame.M3: maxdays = 1; break;
                    case HistoricalTimeFrame.M5: maxdays = 7; break;
                    case HistoricalTimeFrame.M10: maxdays = 7; break;
                    case HistoricalTimeFrame.M15: maxdays = 21; break;
                    case HistoricalTimeFrame.M30: maxdays = 21; break;
                    case HistoricalTimeFrame.H1: maxdays = 80; break;
                    case HistoricalTimeFrame.H2: maxdays = 80; break;
                    case HistoricalTimeFrame.H4: maxdays = 80; break;
                    case HistoricalTimeFrame.D1: maxdays = 364 * 1; break;
                    case HistoricalTimeFrame.W1: maxdays = 364 * 5; break;
                    case HistoricalTimeFrame.MN: maxdays = 364 * 10; break;
                    default: maxdays = 1; break;
                }
                var periodstart = DateStart;
                var lastdate = DateStart;
                var candleslastdate = DateTime.MinValue;
                // T-Invest ограничивает глубину одного запроса свечей в зависимости от таймфрейма,
                // поэтому большой период режется на допустимые куски.
                while (lastdate < DateEnd)
                {
                    periodstart = lastdate;
                    var periodend = lastdate.AddDays(maxdays);
                    lastdate = periodend;
                    periodend = periodend > DateEnd ? DateEnd : periodend;
                    if (periodend - periodstart >= Helper.HistoricalTimeFrameTimeSpan(TimeFrame) && !Locked)
                    {
                        var resdata = OnQueryData(Figi, TimeFrame, periodstart, periodend);
                        if (resdata != null && resdata.Count > 0)
                        {
                            res.AddRange(resdata.Where((u) => u.Time > candleslastdate));
                            candleslastdate = resdata.Last().Time;
                        }
                    }
                }
                ;
                return res;
            }
            return null;
        }
    }
    public class DateInterval
    {
        public DateTime Start { get; set; }
        public DateTime End { get; set; }
        public DateInterval(DateTime start, DateTime end)
        {
            Start = start;
            End = end;
        }
        public DateInterval()
        {

        }
    }
    /// <summary>
    /// Унифицированная OHLCV-свеча для всех проектов решения.
    /// </summary>
    public class HistoricalCandle
    {
        public double Open { get; set; }
        public double Close { get; set; }
        public double Low { get; set; }
        public double High { get; set; }
        public double Volume { get; set; }
        public DateTime Time { get; set; }
        [Newtonsoft.Json.JsonIgnore]
        public double Price { get { return (Open + Close) / 2; } }
        [Newtonsoft.Json.JsonIgnore]
        public double HL2 { get { return (High + Low) / 2; } }
        public HistoricalCandle () {}
        public HistoricalCandle(Candle candle)
        {
            Low = Helper.FromQuotation(candle.Low);
            High = Helper.FromQuotation(candle.High);
            Open = Helper.FromQuotation(candle.Open);
            Close = Helper.FromQuotation(candle.Close);
            Volume = candle.Volume;
            Time = candle.Time.ToDateTime().ToUniversalTime();
        }
        public HistoricalCandle(HistoricCandle candle)
        {
            Low = Helper.FromQuotation(candle.Low);
            High = Helper.FromQuotation(candle.High);
            Open = Helper.FromQuotation(candle.Open);
            Close = Helper.FromQuotation(candle.Close);
            Volume = candle.Volume;
            Time = candle.Time.ToDateTime().ToUniversalTime();
        }
        public HistoricalCandle(HistoricalCandle candle)
        {
            Low = candle.Low;
            High = candle.High;
            Open = candle.Open;
            Close = candle.Close;
            Time = candle.Time;
            Volume = candle.Volume;
        }
        public override string ToString()
        {
            return $@"{Time:dd.MM.yyyy HH:mm:ss} O={Open:0.###} C={Close:0.###} L={Low:0.###} H={High:0.###} Vol={Volume}";
        }
    }
    public class LastPrice
    {
        public double Price { get; set; }
        public DateTime Time { get; set; }
        public LastPrice (double Price, DateTime Time)
        {
            this.Price = Price;
            this.Time = Time;
        }
    }
    public class HistoricalTrade
    {
        public string Ticker { get; set; } = "";
        public long Quontity { get; set; }
        public double Price { get; set; }
        public DateTime Time { get; set; }
        public bool IsBuy { get; set; }
    }
    /// <summary>
    /// Снимок сделки и связанных стаканов для последующего анализа арбитражных отклонений.
    /// </summary>
    public class HistoricalTradeDataForAnalysis
    {
        public HistoricalTrade Trade { get; set; }
        public List<HistoricalOrderBook> OrderBooks { get; set; } = new List<HistoricalOrderBook>();
        public double DeviationShort { get; set; }
        public double DeviationLong { get; set; }
        public double AverageDeviation { get; set; }
        public double AverageDeviationSell { get; set; }
        public double DSP { get; set; }
        public double DLP { get; set; }
    }
    public class HistoricalOrderBookEntry
    {
        public double Price { get; set; }
        public long Quontity { get; set; }
        public override string ToString()
        {
            return $@"Price={Price:C} {(Quontity > 0?"Ask":"Bid")} Quontity={Quontity} ";
        }
    }
    /// <summary>
    /// Упрощенное представление стакана: положительное количество используется для ask,
    /// отрицательное - для bid.
    /// </summary>
    public class HistoricalOrderBook 
    {
        public string Ticker { get; set; } = "";
        public DateTime Time { get; set; }
        public List<HistoricalOrderBookEntry> Entries { get; set; } = new List<HistoricalOrderBookEntry>();
        public override string ToString()
        {
            return $@"{Ticker} Time={ Time.ToLocalTime()} Bid={Entries.Where(u => u.Quontity < 0).Sum(u => Math.Abs(u.Quontity))} Ask={Entries.Where(u => u.Quontity > 0).Sum(u => Math.Abs(u.Quontity))}";
        }
        [Newtonsoft.Json.JsonIgnore]
        public double?MaxBid
        {
            get
            {
                var bids = Entries.Where(u => u.Quontity < 0);
                return bids.Any() ? bids.Max(u => u.Price) : null;
            }
        }
        [Newtonsoft.Json.JsonIgnore]
        public double? MinAsk
        {
            get
            {
                var asks = Entries.Where(u => u.Quontity > 0);
                return asks.Any() ? asks.Min(u => u.Price) : null;
            }
        }
    }

    /// <summary>
    /// Внутренний список таймфреймов, который маппится на CandleInterval T-Invest API.
    /// </summary>
    public enum HistoricalTimeFrame
    {
        M1 = 0,
        M2 = 1,
        M3 = 2,
        M5 = 3,
        M10 = 4,
        M15 = 5,
        M30 = 6,
        H1 = 7,
        H2 = 8,
        H4 = 9,
        D1 = 10,
        W1 = 11,
        MN = 12
    }

    /// <summary>
    /// Модель связи цены акции и фьючерса. Используется для оценки справедливого
    /// соотношения и арбитражного отклонения между инструментами.
    /// </summary>
    public class LinearInterpolator
    {
        private const double timedivider = 1000000d;

        public double a { get; set; }
        public double b { get; set; }
        public double c { get; set; }
        public DateTime Time { get; set; }
        public LinearInterpolator(double a, double b, double c, DateTime time)
        {
            this.a = a;
            this.b = b;
            this.c = c;
            this.Time = time;
        }
        public static (double m, double b) LinearLeastSquares(double[] x, double[] y)
        {
            // Проверка входных данных
            if (x == null || y == null || x.Length != y.Length || x.Length < 2)
            {
                throw new ArgumentException("Некорректные входные данные: массивы x и y должны быть непустыми, одинаковой длины и содержать не менее 2 точек");
            }

            int n = x.Length;

            // Вычисление необходимых сумм
            double sum_x = x.Sum();
            double sum_y = y.Sum();
            double sum_xx = x.Select(val => val * val).Sum();
            double sum_xy = x.Zip(y, (a, b) => a * b).Sum();

            // Вычисление знаменателя
            double denominator = n * sum_xx - sum_x * sum_x;

            // Проверка деления на ноль (все x одинаковы)
            if (Math.Abs(denominator) < 1e-10)
            {
                throw new InvalidOperationException("Невозможно построить прямую: все значения x одинаковы");
            }

            // Вычисление наклона m
            double m = (n * sum_xy - sum_x * sum_y) / denominator;

            // Вычисление пересечения b
            double b = (sum_y - m * sum_x) / n;

            return (m, b);
        }
        private static (double a, double b, double c) FitQuadratic(double[] x, double[] y)
        {
            if (x.Length != y.Length || x.Length < 3)
                throw new ArgumentException("Массивы должны быть одинаковой длины и содержать минимум 3 точки");

            int n = x.Length;
            double sumX = 0, sumX2 = 0, sumX3 = 0, sumX4 = 0, sumY = 0, sumXY = 0, sumX2Y = 0;

            // Вычисление сумм
            for (int i = 0; i < n; i++)
            {
                double x2 = x[i] * x[i];
                double x3 = x2 * x[i];
                double x4 = x3 * x[i];
                double xy = x[i] * y[i];
                double x2y = x2 * y[i];

                sumX += x[i];
                sumX2 += x2;
                sumX3 += x3;
                sumX4 += x4;
                sumY += y[i];
                sumXY += xy;
                sumX2Y += x2y;
            }

            // Формирование матрицы системы уравнений
            double[,] A = new double[3, 3]
            {
            { sumX4, sumX3, sumX2 },
            { sumX3, sumX2, sumX },
            { sumX2, sumX, n }
            };

            double[] B = new double[3] { sumX2Y, sumXY, sumY };

            // Решение системы уравнений методом Гаусса
            double[] coefficients = SolveLinearSystem(A, B);

            return (a: coefficients[0], b: coefficients[1], c: coefficients[2]);
        }

        // Метод для решения системы линейных уравнений методом Гаусса
        private static double[] SolveLinearSystem(double[,] A, double[] B)
        {
            int n = B.Length;
            double[,] matrix = new double[n, n + 1];

            // Формирование расширенной матрицы
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++)
                {
                    matrix[i, j] = A[i, j];
                }
                matrix[i, n] = B[i];
            }

            // Прямой ход метода Гаусса
            for (int i = 0; i < n; i++)
            {
                // Поиск главного элемента
                double maxElement = Math.Abs(matrix[i, i]);
                int maxRow = i;

                for (int k = i + 1; k < n; k++)
                {
                    if (Math.Abs(matrix[k, i]) > maxElement)
                    {
                        maxElement = Math.Abs(matrix[k, i]);
                        maxRow = k;
                    }
                }

                // Перестановка строк
                for (int k = i; k <= n; k++)
                {
                    double tmp = matrix[maxRow, k];
                    matrix[maxRow, k] = matrix[i, k];
                    matrix[i, k] = tmp;
                }

                // Приведение к треугольному виду
                for (int k = i + 1; k < n; k++)
                {
                    double c = -matrix[k, i] / matrix[i, i];
                    for (int j = i; j <= n; j++)
                    {
                        if (i == j)
                            matrix[k, j] = 0;
                        else
                            matrix[k, j] += c * matrix[i, j];
                    }
                }
            }

            // Обратный ход
            double[] result = new double[n];
            for (int i = n - 1; i >= 0; i--)
            {
                result[i] = matrix[i, n] / matrix[i, i];
                for (int k = i - 1; k >= 0; k--)
                {
                    matrix[k, n] -= matrix[k, i] * result[i];
                }
            }

            return result;
        }
        private static double Predict(double x, double a, double b, double c)
        {
            return a * x * x + b * x + c;
        }
        public static long first_tick = new DateTime(2025, 1, 1).Ticks;
        public static LinearInterpolator? FromCandles(IEnumerable<HistoricalCandle> ShareCandles, IEnumerable<HistoricalCandle> FutureCandles, int maxcandles, int minpairs)
        {
            if (!FutureCandles.Any() || !ShareCandles.Any())
                return null;
            var pairs = Helper.GetHistoricalCandlesPairs(ShareCandles, FutureCandles);
            if (pairs.Count() > minpairs)
            {
                pairs = pairs.TakeLast(minpairs).ToList();
                var time = pairs.Last().Item1.Time;
                /*var a = 0d;
                var b = 0d;
                var c = 0d;
                for (int i = 1; i < pairs.Count; i++)
                {
                    var prevpair = pairs[i - 1];
                    var pair = pairs[i];
                    c += (pair.Item1.Time.Ticks - prevpair.Item1.Time.Ticks) * pair.Item1.Price / pair.Item2.Price;
                }
                c /= pairs.Last().Item1.Time.Ticks - pairs.First().Item1.Time.Ticks;*/
                /*var (a, b, c) = FitQuadratic(
                    pairs.Select(u => Convert.ToDouble(u.Item1.Time.Ticks - first_tick) / timedivider).ToArray(),
                    pairs.Select(u => u.Item1.Price / u.Item2.Price).ToArray());*/
                var a = 0d;
                var (b, c) = LinearLeastSquares(
                    pairs.Select(u => Convert.ToDouble(u.Item1.Time.Ticks - first_tick) / timedivider).ToArray(),
                    pairs.Select(u => u.Item1.Price / u.Item2.Price).ToArray());
                return new LinearInterpolator(a, b, c, time);
            }
            else
                return null;
        }
        public double GetAverageCorrelationAtTime(DateTime time)
        {
            if (time.Kind != DateTimeKind.Utc)
                time = time.ToUniversalTime();
            return Predict(Convert.ToDouble(time.Ticks - first_tick) / timedivider, a, b, c);
        }
        public double CalculateCorrelationDifference(double PriceShare, double PriceFuture, DateTime time)
        {
            var val = GetAverageCorrelationAtTime(time);
            //var val = a * time.ToTimestamp().Seconds + b;
            var diff = (PriceShare / PriceFuture - val) / val;
            return diff;
        }
    }
    public class MarginAttributes
    {
        public double LiquidPortfolio { get; set; }
        public double StartingMargin { get; set; }
        public double CorrectedMargin { get; set; }
        public double AmountOfMissingFunds { get; set; }
        public double FundsSufficiencyLevel { get; set; }
        public double MinimalMargin { get; set; }
        public MarginAttributes()
        { }
        public MarginAttributes(GetMarginAttributesResponse response)
        {
            LiquidPortfolio = Helper.FromMoneyValue(response.LiquidPortfolio);
            StartingMargin = Helper.FromMoneyValue(response.StartingMargin);
            CorrectedMargin = Helper.FromMoneyValue(response.CorrectedMargin);
            AmountOfMissingFunds = Helper.FromMoneyValue(response.AmountOfMissingFunds);
            FundsSufficiencyLevel = Helper.FromQuotation(response.FundsSufficiencyLevel);
            MinimalMargin = Helper.FromMoneyValue(response.MinimalMargin);
        }
        public override string ToString()
        {
            return
$@"Ликвидный портфель {LiquidPortfolio:C}
Начальная маржа {StartingMargin:C}
Скорректированная маржа {CorrectedMargin:C}
Минимальная маржа {MinimalMargin:C}
Недостающие средства {AmountOfMissingFunds:C}
Уровень достаточности средств {FundsSufficiencyLevel:F2}";
        }
    }

}
