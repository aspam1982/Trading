using CommonClasses;
using Google.Protobuf.WellKnownTypes;
using ScottPlot;
using System.Windows;
using TechnicalForecaster;
using Tinkoff.InvestApi.V1;

namespace RoboAINewsReader
{
    /// <summary>
    /// Окно технического прогноза по свечам.
    /// Пользователь выбирает тикер акции из списка T-Invest, после чего кнопка
    /// "Рассчитать" загружает свечи, достраивает кэш прогнозов и выводит график.
    /// </summary>
    public partial class CandleForecasterWindow : Window
    {
        private static readonly string token = WindowsCredentialManager.ReadSecret("InvestTestAccount") ?? "key not found";
        private static readonly string chatgptapikey = WindowsCredentialManager.ReadSecret("ChatGPTApiKey") ?? "key not found";
        private const string DefaultDir = "CandleData";
        private readonly Tinkoff.InvestApi.InvestApiClient client;
        private List<Share> shares = new List<Share>();

        public CandleForecasterWindow()
        {
            InitializeComponent();
            client = Tinkoff.InvestApi.InvestApiClientFactory.Create(token);
            LoadShares();
        }

        private void LoadShares()
        {
            // В окно подставляются все акции, доступные через API; расчет не запускается до нажатия кнопки.
            shares = client.Instruments.Shares().Instruments
                .Where(u => u.ApiTradeAvailableFlag)
                .OrderBy(u => u.Ticker)
                .ToList();

            tickerComboBox.ItemsSource = shares;
            tickerComboBox.SelectedItem = shares.FirstOrDefault(u => u.Ticker == "SBER") ?? shares.FirstOrDefault();
        }

        private async void CalculateButton_Click(object sender, RoutedEventArgs e)
        {
            if (tickerComboBox.SelectedItem is not Share share)
            {
                statusTextBlock.Text = "Выберите акцию";
                return;
            }

            calculateButton.IsEnabled = false;
            statusTextBlock.Text = $"Расчет {share.Ticker}...";

            try
            {
                await Task.Run(() => CalculateForecast(share));
                statusTextBlock.Text = $"Готово: {share.Ticker}";
            }
            catch (Exception ex)
            {
                statusTextBlock.Text = $"Ошибка: {ex.Message}";
            }
            finally
            {
                calculateButton.IsEnabled = true;
            }
        }

        private void CalculateForecast(Share share)
        {
            var tfday = HistoricalTimeFrame.D1;
            var tfweek = HistoricalTimeFrame.W1;
            var tfmonth = HistoricalTimeFrame.MN;

            // HistoricalData сначала читает локальный кэш, а недостающий диапазон догружает через T-Invest.
            var sharedaydata = HistoricalData.ReadHistoricalData(share.Ticker, share.Figi, tfday, false, new HistoricalData.QueryDataDelegate((figi, frame, from, to) =>
            {
                var candles = client.MarketData.GetCandles(new GetCandlesRequest { InstrumentId = share.Uid, Interval = CandleInterval.Day, From = from.ToUniversalTime().ToTimestamp(), To = to.ToUniversalTime().ToTimestamp() });
                return candles.Candles.Where(u => u.IsComplete).Select(u => u.ToHistoricalCandle()).ToList();
            }));
            var shareweekdata = HistoricalData.ReadHistoricalData(share.Ticker, share.Figi, tfweek, false, new HistoricalData.QueryDataDelegate((figi, frame, from, to) =>
            {
                var candles = client.MarketData.GetCandles(new GetCandlesRequest { InstrumentId = share.Uid, Interval = CandleInterval.Week, From = from.ToUniversalTime().ToTimestamp(), To = to.ToUniversalTime().ToTimestamp() });
                return candles.Candles.Where(u => u.IsComplete).Select(u => u.ToHistoricalCandle()).ToList();
            }));
            var sharemonthdata = HistoricalData.ReadHistoricalData(share.Ticker, share.Figi, tfmonth, false, new HistoricalData.QueryDataDelegate((figi, frame, from, to) =>
            {
                var candles = client.MarketData.GetCandles(new GetCandlesRequest { InstrumentId = share.Uid, Interval = CandleInterval.Month, From = from.ToUniversalTime().ToTimestamp(), To = to.ToUniversalTime().ToTimestamp() });
                return candles.Candles.Where(u => u.IsComplete).Select(u => u.ToHistoricalCandle()).ToList();
            }));

            var dfrom = new DateTime(2020, 01, 01);
            var dto = DateTime.Now;
            var daycandles = sharedaydata.GetData(dfrom, dto).TakeLast(1000).ToList();
            if (sharedaydata.DataHasChanges)
                sharedaydata.SaveHistoricalData(DefaultDir);

            var weekcandles = shareweekdata.GetData(dfrom, dto).ToList();
            if (shareweekdata.DataHasChanges)
                shareweekdata.SaveHistoricalData(DefaultDir);

            var monthcandles = sharemonthdata.GetData(dfrom, dto);
            if (sharemonthdata.DataHasChanges)
                sharemonthdata.SaveHistoricalData(DefaultDir);

            // Прогнозы сохраняются по дате свечи, поэтому повторный запуск продолжает расчет с новых точек.
            var forecasts = StoredForecastData.ReadForecasts(DefaultDir, share.Ticker, tfday);
            GenerateForecastsAsync(daycandles, weekcandles, monthcandles, forecasts, share.Ticker, tfday, chatgptapikey, DefaultDir).GetAwaiter().GetResult();

            // Переводим прогноз процента роста в синтетические свечи, чтобы вывести их на том же ценовом графике.
            var forecastvalues = BuildForecastCandles(daycandles, forecasts);
            Dispatcher.Invoke(() => RenderForecast(share.Ticker, daycandles, forecastvalues));
        }

        private static Dictionary<string, List<HistoricalCandle>> BuildForecastCandles(List<HistoricalCandle> daycandles, StoredForecastData forecasts)
        {
            var forecastvalues = new Dictionary<string, List<HistoricalCandle>>();
            foreach (var kv in forecasts.Forecasts.OrderBy(u => u.Key))
            {
                var c = daycandles.FirstOrDefault(u => u.Time == kv.Key);
                if (c == null)
                    continue;

                foreach (var fc in kv.Value.Forecast.Where(u => u.Period != "5d" || Math.Abs(u.GrowthPct) > 2.0M))
                {
                    if (!forecastvalues.ContainsKey(fc.Period))
                        forecastvalues.Add(fc.Period, new List<HistoricalCandle>());

                    var mul = (100d + Convert.ToDouble(fc.GrowthPct)) / 100d;
                    var time = fc.Period switch
                    {
                        "1d" => c.Time.AddDays(1),
                        "5d" => c.Time.AddDays(5),
                        "10d" => c.Time.AddDays(10),
                        "15d" => c.Time.AddDays(15),
                        "20d" => c.Time.AddDays(20),
                        "25d" => c.Time.AddDays(25),
                        "30d" => c.Time.AddDays(30),
                        _ => c.Time
                    };

                    forecastvalues[fc.Period].Add(new HistoricalCandle
                    {
                        Open = c.Open * mul,
                        Close = c.Close * mul,
                        Low = c.Low * mul,
                        High = c.High * mul,
                        Time = time,
                        Volume = c.Volume
                    });
                }
            }

            return forecastvalues;
        }

        private void RenderForecast(string ticker, List<HistoricalCandle> daycandles, Dictionary<string, List<HistoricalCandle>> forecastvalues)
        {
            plot.Multiplot.Reset();
            plot.Multiplot.AddPlots(1);
            var mainplot = plot.Multiplot.GetPlot(0);
            mainplot.Title($"Свечной прогноз {ticker}");

            var scottcandles = daycandles
                .Select(u => new OHLC(u.Open, u.High, u.Low, u.Close, u.Time, TimeSpan.FromDays(1)))
                .ToList();
            mainplot.Add.Candlestick(scottcandles);

            // Сейчас основной визуальный слой - 5-дневный прогноз: он был исходной рабочей гипотезой окна.
            foreach (var kv in forecastvalues.Where(u => u.Key == "5d"))
            {
                var values = kv.Value.Select(u => u.HL2).ToArray();
                var times = kv.Value.Select(u => u.Time).ToArray();
                mainplot.Add.Scatter(times, values, ScottPlot.Colors.Blue);
            }

            mainplot.Axes.DateTimeTicksBottom();
            plot.Refresh();
        }

        // Статический троттлер для всех запросов к OpenAI, чтобы не упереться в rate limit.
        private static readonly SemaphoreSlim _openAiThrottle = new SemaphoreSlim(1, 1);
        private static DateTime _lastCallUtc = DateTime.MinValue;
        private const int MinIntervalMs = 1500;

        public async Task GenerateForecastsAsync(
            List<HistoricalCandle> daycandles,
            List<HistoricalCandle> weekcandles,
            List<HistoricalCandle> monthcandles,
            StoredForecastData forecasts,
            string symbol,
            HistoricalTimeFrame tf,
            string chatgptapikey,
            string defaultDir,
            CancellationToken cancellationToken = default)
        {
            var hasNewForecasts = false;

            using var cf = new TechnicalForecaster.CandleForecaster(chatgptapikey);
            var recordsadded = 0;
            for (var i = 20; i < daycandles.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var candle = daycandles[i];
                if (forecasts.Forecasts.ContainsKey(candle.Time))
                    continue;

                hasNewForecasts = true;

                // Для каждой исторической даты модель видит только свечи, которые были известны на тот момент.
                var lastCandles = daycandles
                    .Take(i)
                    .TakeLast(50)
                    .ToList();

                var req = new TechnicalForecaster.ForecastRequest
                {
                    Daily = lastCandles.Select(u => new TechnicalForecaster.Candle
                    {
                        C = Convert.ToDecimal(u.Close),
                        O = Convert.ToDecimal(u.Open),
                        L = Convert.ToDecimal(u.Low),
                        H = Convert.ToDecimal(u.High),
                        V = Convert.ToInt64(u.Volume),
                        T = u.Time.ToShortDateString()
                    }).ToList(),
                    Weekly = weekcandles.Where(u => u.Time + TimeSpan.FromDays(7) < candle.Time).TakeLast(50).Select(u => new TechnicalForecaster.Candle
                    {
                        C = Convert.ToDecimal(u.Close),
                        O = Convert.ToDecimal(u.Open),
                        L = Convert.ToDecimal(u.Low),
                        H = Convert.ToDecimal(u.High),
                        V = Convert.ToInt64(u.Volume),
                        T = u.Time.ToShortDateString()
                    }).ToList(),
                    Monthly = monthcandles.Where(u => u.Time + TimeSpan.FromDays(31) < candle.Time).TakeLast(50).Select(u => new TechnicalForecaster.Candle
                    {
                        C = Convert.ToDecimal(u.Close),
                        O = Convert.ToDecimal(u.Open),
                        L = Convert.ToDecimal(u.Low),
                        H = Convert.ToDecimal(u.High),
                        V = Convert.ToInt64(u.Volume),
                        T = u.Time.ToShortDateString()
                    }).ToList(),
                    Ticker = symbol,
                    Timeframe = tf.ToString(),
                };

                TechnicalForecaster.ForecastOnly? cfResult = null;
                var attempt = 0;
                const int maxRetries = 5;

                while (attempt <= maxRetries && cfResult == null)
                {
                    attempt++;

                    // Глобальный троттлинг: не чаще, чем раз в MinIntervalMs.
                    _openAiThrottle.WaitAsync(cancellationToken).GetAwaiter().GetResult();
                    try
                    {
                        var diff = DateTime.UtcNow - _lastCallUtc;
                        if (diff.TotalMilliseconds < MinIntervalMs && diff.TotalMilliseconds >= 0)
                            await Task.Delay(MinIntervalMs - (int)diff.TotalMilliseconds, cancellationToken);

                        _lastCallUtc = DateTime.UtcNow;
                    }
                    finally
                    {
                        _openAiThrottle.Release();
                    }

                    try
                    {
                        cfResult = cf.GetForecastAsync(req, cancellationToken).GetAwaiter().GetResult();
                    }
                    catch (InvalidOperationException ex) when (ex.Message.Contains("rate_limit_exceeded"))
                    {
                        if (attempt > maxRetries)
                            break;

                        var backoffMs = (int)(200 * Math.Pow(2, attempt - 1));
                        await Task.Delay(backoffMs, cancellationToken);
                    }
                    catch
                    {
                        break;
                    }
                }

                if (cfResult != null)
                {
                    lock (forecasts.Forecasts)
                    {
                        if (!forecasts.Forecasts.ContainsKey(candle.Time))
                        {
                            forecasts.Forecasts.TryAdd(candle.Time, cfResult);
                            recordsadded++;
                            if (recordsadded > 10)
                            {
                                recordsadded = 0;
                                forecasts.SaveForecasts(defaultDir);
                            }
                        }
                    }
                }
            }

            if (hasNewForecasts)
                forecasts.SaveForecasts(defaultDir);
        }
    }
}
