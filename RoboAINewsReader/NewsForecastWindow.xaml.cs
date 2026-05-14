using ChatGptAnalyser;
using CommonClasses;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Caching.Memory;
using System.IO;
using System.ServiceModel.Syndication;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Xml;
using Tinkoff.InvestApi.V1;

namespace RoboAINewsReader;

/// <summary>
/// Окно прогноза по новостям. Загружает избранные акции из T-Invest,
/// читает RSS Investing.com и отправляет новости в выбранный AI-анализатор.
/// </summary>
public partial class NewsForecastWindow : Window
{
    private static readonly string token = WindowsCredentialManager.ReadSecret("InvestTestAccount") ?? "key not found";
    private static readonly string deepseekapikey = WindowsCredentialManager.ReadSecret("DeepSeekApiKey") ?? "key not found";
    private static readonly string chatgptapikey = WindowsCredentialManager.ReadSecret("ChatGPTApiKey") ?? "key not found";

    public NewsForecastWindow()
    {
        InitializeComponent();
    }

    private async void RunButton_Click(object sender, RoutedEventArgs e)
    {
        var mode = ((analyzerSelector.SelectedItem as ComboBoxItem)?.Tag as string) ?? "ChatGPT";
        var newsCount = GetNewsCount();

        runButton.IsEnabled = false;
        text.Clear();

        try
        {
            AppendText($"Запуск анализа: {mode}. Новостей: {newsCount}\r\n\r\n");

            if (mode == "ChatGPT" || mode == "Both")
                await RunChatGptAnalysisAsync(newsCount);

            if (mode == "DeepSeek" || mode == "Both")
                await RunDeepSeekAnalysisAsync(newsCount);

            AppendText("\r\nАнализ завершен.\r\n");
        }
        catch (Exception ex)
        {
            AppendText($"\r\nОшибка запуска анализа: {ex.Message}\r\n");
        }
        finally
        {
            runButton.IsEnabled = true;
        }
    }

    private int GetNewsCount()
    {
        if (int.TryParse(newsCountTextBox.Text, out var newsCount) && newsCount > 0)
            return newsCount;

        newsCountTextBox.Text = "10";
        return 10;
    }

    private async Task RunChatGptAnalysisAsync(int newsCount)
    {
        AppendText("=== ChatGPT ===\r\n");

        var client = Tinkoff.InvestApi.InvestApiClientFactory.Create(token);
        var instruments = client.Instruments.Shares().Instruments;
        var relevantInstruments = GetFavoriteShares(client, instruments)
            .Select(u => new FinancialInstrument { Ticker = u.Ticker, Sector = u.Sector, Name = u.Name })
            .ToList();

        // Callback дает анализатору свечи только по тикеру, который AI признал релевантным для новости.
        async Task<MultiTimeframeCandleData> GetCandleData(string ticker)
        {
            var instrument = instruments.First(u => u.Ticker == ticker);
            return new MultiTimeframeCandleData
            {
                H1Candles = (await client.MarketData.GetCandlesAsync(new GetCandlesRequest
                {
                    InstrumentId = instrument.Uid,
                    Interval = CandleInterval.Hour,
                    To = DateTime.Now.ToUniversalTime().ToTimestamp(),
                    From = DateTime.Now.AddDays(-5).ToUniversalTime().ToTimestamp()
                })).Candles.TakeLast(10).Select(u => new CandleStick { Open = u.Open, Close = u.Close, Low = u.Low, High = u.High, Volume = u.Volume, Date = DateTime.SpecifyKind(u.Time.ToDateTime(), DateTimeKind.Utc) }).ToList(),
                D1Candles = (await client.MarketData.GetCandlesAsync(new GetCandlesRequest
                {
                    InstrumentId = instrument.Uid,
                    Interval = CandleInterval.Day,
                    To = DateTime.Now.ToUniversalTime().ToTimestamp(),
                    From = DateTime.Now.AddDays(-20).ToUniversalTime().ToTimestamp()
                })).Candles.TakeLast(10).Select(u => new CandleStick { Open = u.Open, Close = u.Close, Low = u.Low, High = u.High, Volume = u.Volume, Date = DateTime.SpecifyKind(u.Time.ToDateTime(), DateTimeKind.Utc) }).ToList(),
                W1Candles = (await client.MarketData.GetCandlesAsync(new GetCandlesRequest
                {
                    InstrumentId = instrument.Uid,
                    Interval = CandleInterval.Week,
                    To = DateTime.Now.ToUniversalTime().ToTimestamp(),
                    From = DateTime.Now.AddDays(-7 * 20).ToUniversalTime().ToTimestamp()
                })).Candles.TakeLast(10).Select(u => new CandleStick { Open = u.Open, Close = u.Close, Low = u.Low, High = u.High, Volume = u.Volume, Date = DateTime.SpecifyKind(u.Time.ToDateTime(), DateTimeKind.Utc) }).ToList(),
                M1Candles = (await client.MarketData.GetCandlesAsync(new GetCandlesRequest
                {
                    InstrumentId = instrument.Uid,
                    Interval = CandleInterval.Month,
                    To = DateTime.Now.ToUniversalTime().ToTimestamp(),
                    From = DateTime.Now.AddMonths(-10).ToUniversalTime().ToTimestamp()
                })).Candles.TakeLast(10).Select(u => new CandleStick { Open = u.Open, Close = u.Close, Low = u.Low, High = u.High, Volume = u.Volume, Date = DateTime.SpecifyKind(u.Time.ToDateTime(), DateTimeKind.Utc) }).ToList()
            };
        }

        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        using var analyzer = new EnhancedNewsAnalyzer(
            apiKey: chatgptapikey,
            relevantInstruments: relevantInstruments,
            getCandlesFunction: GetCandleData,
            cache: memoryCache);

        await AnalyzeFeedAsync("ChatGPT", newsCount, async news => await analyzer.AnalyzeNewsAsync(ToChatGptNewsItem(news)));
    }

    private async Task RunDeepSeekAnalysisAsync(int newsCount)
    {
        AppendText("=== DeepSeek ===\r\n");

        var client = Tinkoff.InvestApi.InvestApiClientFactory.Create(token);
        var instruments = client.Instruments.Shares().Instruments;
        var relevantInstruments = GetFavoriteShares(client, instruments)
            .Select(u => new DeepSeekAnalyser.FinancialInstrument { Ticker = u.Ticker, Sector = u.Sector, Name = u.Name })
            .ToList();

        // DeepSeek-анализатор использует собственные DTO, поэтому callback возвращает типы из namespace DeepSeekAnalyser.
        async Task<DeepSeekAnalyser.MultiTimeframeCandleData> GetCandleData(string ticker)
        {
            var instrument = instruments.First(u => u.Ticker == ticker);
            return new DeepSeekAnalyser.MultiTimeframeCandleData
            {
                H1Candles = (await client.MarketData.GetCandlesAsync(new GetCandlesRequest
                {
                    InstrumentId = instrument.Uid,
                    Interval = CandleInterval.Hour,
                    To = DateTime.Now.ToUniversalTime().ToTimestamp(),
                    From = DateTime.Now.AddDays(-5).ToUniversalTime().ToTimestamp()
                })).Candles.TakeLast(10).Select(u => new DeepSeekAnalyser.CandleStick { Open = u.Open, Close = u.Close, Low = u.Low, High = u.High, Volume = u.Volume, Date = DateTime.SpecifyKind(u.Time.ToDateTime(), DateTimeKind.Utc) }).ToList(),
                D1Candles = (await client.MarketData.GetCandlesAsync(new GetCandlesRequest
                {
                    InstrumentId = instrument.Uid,
                    Interval = CandleInterval.Day,
                    To = DateTime.Now.ToUniversalTime().ToTimestamp(),
                    From = DateTime.Now.AddDays(-20).ToUniversalTime().ToTimestamp()
                })).Candles.TakeLast(10).Select(u => new DeepSeekAnalyser.CandleStick { Open = u.Open, Close = u.Close, Low = u.Low, High = u.High, Volume = u.Volume, Date = DateTime.SpecifyKind(u.Time.ToDateTime(), DateTimeKind.Utc) }).ToList(),
                W1Candles = (await client.MarketData.GetCandlesAsync(new GetCandlesRequest
                {
                    InstrumentId = instrument.Uid,
                    Interval = CandleInterval.Week,
                    To = DateTime.Now.ToUniversalTime().ToTimestamp(),
                    From = DateTime.Now.AddDays(-7 * 20).ToUniversalTime().ToTimestamp()
                })).Candles.TakeLast(10).Select(u => new DeepSeekAnalyser.CandleStick { Open = u.Open, Close = u.Close, Low = u.Low, High = u.High, Volume = u.Volume, Date = DateTime.SpecifyKind(u.Time.ToDateTime(), DateTimeKind.Utc) }).ToList(),
                M1Candles = (await client.MarketData.GetCandlesAsync(new GetCandlesRequest
                {
                    InstrumentId = instrument.Uid,
                    Interval = CandleInterval.Month,
                    To = DateTime.Now.ToUniversalTime().ToTimestamp(),
                    From = DateTime.Now.AddMonths(-10).ToUniversalTime().ToTimestamp()
                })).Candles.TakeLast(10).Select(u => new DeepSeekAnalyser.CandleStick { Open = u.Open, Close = u.Close, Low = u.Low, High = u.High, Volume = u.Volume, Date = DateTime.SpecifyKind(u.Time.ToDateTime(), DateTimeKind.Utc) }).ToList()
            };
        }

        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        using var analyzer = new DeepSeekAnalyser.EnhancedNewsAnalyzer(
            apiKey: deepseekapikey,
            relevantInstruments: relevantInstruments,
            getCandlesFunction: GetCandleData,
            cache: memoryCache);

        await AnalyzeFeedAsync("DeepSeek", newsCount, async news => await analyzer.AnalyzeNewsAsync(ToDeepSeekNewsItem(news)));
    }

    private static List<Share> GetFavoriteShares(Tinkoff.InvestApi.InvestApiClient client, IEnumerable<Share> instruments)
    {
        var groups = client.Instruments.GetFavoriteGroups(new GetFavoriteGroupsRequest()).Groups;
        var favoriteGroup = groups.First(u => u.GroupName == "Избранное");
        var favorites = client.Instruments.GetFavorites(new GetFavoritesRequest { GroupId = favoriteGroup.GroupId })
            .FavoriteInstruments
            .Where(u => u.InstrumentKind == InstrumentType.Share)
            .ToList();

        return instruments.Where(u => favorites.Select(f => f.Uid).Contains(u.Uid)).ToList();
    }

    private async Task AnalyzeFeedAsync(string engineName, int newsCount, Func<NewsEnvelope, Task<object>> analyzeNewsAsync)
    {
        foreach (var news in ReadNewsFeed().Take(newsCount))
        {
            try
            {
                var result = await analyzeNewsAsync(news);
                var json = JsonSerializer.Serialize(
                    new object[] { engineName, news, result },
                    new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                        WriteIndented = true
                    });

                AppendText(json + ",\r\n");
            }
            catch (Exception ex)
            {
                AppendText($"{engineName}: {news.Title} - ошибка: {ex.Message}\r\n");
            }
        }

        await Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() => File.AppendAllText("output.txt", text.Text + "\r\n")));
    }

    private static IEnumerable<NewsEnvelope> ReadNewsFeed()
    {
        using var reader = XmlReader.Create("https://ru.investing.com/rss/news_12.rss");
        var feed = SyndicationFeed.Load(reader);

        foreach (var item in feed.Items)
        {
            var content = item.Content?.ToString();
            var link = item.Links.FirstOrDefault(u => u.MediaType == null);
            if (link != null)
                content = "Ссылка на новость: " + link.Uri.AbsoluteUri;

            yield return new NewsEnvelope
            {
                Title = item.Title?.Text ?? "Новости России и мира",
                Content = content ?? string.Empty,
                Id = item.GetHashCode().ToString(),
                PublishDate = item.PublishDate.ToTimestamp().ToDateTime(),
                Source = "https://ru.investing.com"
            };
        }
    }

    private static NewsItem ToChatGptNewsItem(NewsEnvelope news)
    {
        return new NewsItem
        {
            Title = news.Title,
            Content = news.Content,
            Id = news.Id,
            PublishDate = news.PublishDate,
            Source = news.Source
        };
    }

    private static DeepSeekAnalyser.NewsItem ToDeepSeekNewsItem(NewsEnvelope news)
    {
        return new DeepSeekAnalyser.NewsItem
        {
            Title = news.Title,
            Content = news.Content,
            Id = news.Id,
            PublishDate = news.PublishDate,
            Source = news.Source
        };
    }

    private void AppendText(string value)
    {
        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() =>
            {
                text.Text += value;
                text.ScrollToEnd();
            }));
    }

    private sealed class NewsEnvelope
    {
        public string Id { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public string Content { get; init; } = string.Empty;
        public DateTime PublishDate { get; init; }
        public string Source { get; init; } = string.Empty;
    }
}
