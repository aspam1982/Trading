using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using RestSharp;

namespace ChatGptAnalyser;

/// <summary>
/// AI-анализатор новостей на базе OpenAI.
/// Работает в два этапа: сначала определяет релевантные тикеры из разрешенного списка,
/// затем по каждому тикеру строит оценку влияния новости с учетом свечных данных.
/// </summary>
public class EnhancedNewsAnalyzer : IDisposable
{
    private readonly string _openAiApiKey;
    private readonly RestClient _restClient;
    private readonly IMemoryCache _cache;
    private readonly ILogger<EnhancedNewsAnalyzer> _logger;
    private readonly Func<string, Task<MultiTimeframeCandleData>> _getCandlesFunction;
    private readonly List<FinancialInstrument> _relevantInstruments;

    private readonly TimeSpan _cacheDuration = TimeSpan.FromHours(4);
    private const string OpenAiApiUrl = "https://api.openai.com/v1/";
    private const string ModelName = "gpt-5";

    private HttpClient _httpClient;

    public EnhancedNewsAnalyzer(
        string apiKey,
        List<FinancialInstrument> relevantInstruments,
        Func<string, Task<MultiTimeframeCandleData>> getCandlesFunction,
        IMemoryCache cache,
        ILogger<EnhancedNewsAnalyzer> logger = null)
    {
        _openAiApiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _relevantInstruments = relevantInstruments ?? throw new ArgumentNullException(nameof(relevantInstruments));
        _getCandlesFunction = getCandlesFunction ?? throw new ArgumentNullException(nameof(getCandlesFunction));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _logger = logger;

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://api.openai.com/v1/")
        };
        _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _openAiApiKey);


    }

    public async Task<List<InstrumentAnalysisResult>> AnalyzeNewsAsync(NewsItem news)
    {
        var cacheKey = $"news_analysis_v5_{news.Id}_{news.GetHashCode()}";

        if (_cache.TryGetValue(cacheKey, out List<InstrumentAnalysisResult> cachedResult))
        {
            _logger?.LogInformation("Returning cached analysis for news: {NewsId}", news.Id);
            return cachedResult;
        }

        try
        {
            // Этап 1: сужаем анализ до тикеров, на которые новость реально может повлиять.
            var relevantInstruments = await IdentifyRelevantInstruments(news);

            if (!relevantInstruments.Any())
            {
                _logger?.LogInformation("No relevant instruments found for news: {NewsId}", news.Id);
                return new List<InstrumentAnalysisResult>();
            }

            // Этап 2: по каждому найденному инструменту добавляем свечной контекст и просим полный JSON-анализ.
            var analysisResults = await AnalyzeInstrumentsParallel(news, relevantInstruments);
            var validResults = analysisResults.Where(ValidateAnalysisResponse).ToList();

            _cache.Set(cacheKey, validResults, _cacheDuration);

            _logger?.LogInformation("Analysis completed for news: {NewsId}, Instruments analyzed: {Count}",
                news.Id, validResults.Count);

            return validResults;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error analyzing news with new architecture: {NewsId}", news.Id);
            return new List<InstrumentAnalysisResult>();
        }
    }

    private async Task<List<FinancialInstrument>> IdentifyRelevantInstruments(NewsItem news)
    {
        var cacheKey = $"relevant_instruments_{news.Id}";

        if (_cache.TryGetValue(cacheKey, out List<FinancialInstrument> cachedInstruments))
            return cachedInstruments;

        var prompt = $$"""
        НОВОСТЬ ДЛЯ АНАЛИЗА:
        - Заголовок: {{news.Title}}
        - Дата публикации: {{news.PublishDate:yyyy-MM-dd HH:mm}}
        - Источник: {{news.Source}}
        - Текст: {{news.Content}}

        ИНСТРУКЦИИ:
        1. Проанализируй новость и определи все финансовые инструменты (акции, облигации, ETF), 
           на которые эта новость может оказать значительное влияние.
        2. Верни список тикеров релевантных финансовых инструментов в заданном JSON формате.
        3. Указывай только тикеры инструментов в верхнем регистре
        4. Используй встроенный веб-поиск, если информации в тексте недостаточно.
        5. Если в содержимом есть только ссылка на новость - проанализируй содержимое страницы.
        6. Включай только инструменты, которые могут испытать движение цены более 3% под влиянием события, на которое указывает новость.
        """;

        var messages = new[]
        {
            new { role = "system", content = GetInstrumentAnalysisSystemPrompt(false) },
            new { role = "user", content = prompt }
        };

        try
        {
            var response = CallOpenAiApi(messages, ResponseFormatRelevantInstruments()).GetAwaiter().GetResult();

            if (response?.Choices?[0]?.Message?.Content == null)
                return new List<FinancialInstrument>();

            var result = JsonSerializer.Deserialize<InstrumentIdentificationResponse>(
                response.Choices[0].Message.Content,
                new JsonSerializerOptions 
                { 
                    PropertyNameCaseInsensitive = true, 
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase 
                }
            );

            // Модель может вернуть произвольные тикеры, поэтому оставляем только инструменты из локального universe.
            var relevantInstruments = _relevantInstruments
                .Where(i => result?.RelevantInstruments?
                    .Any(ri => ri.Equals(i.Ticker, StringComparison.OrdinalIgnoreCase)) ?? false)
                .ToList();

            _cache.Set(cacheKey, relevantInstruments, TimeSpan.FromMinutes(30));
            return relevantInstruments;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error identifying relevant instruments for news: {NewsId}", news.Id);
            return new List<FinancialInstrument>();
        }
    }

    private async Task<List<InstrumentAnalysisResult>> AnalyzeInstrumentsParallel(
        NewsItem news, List<FinancialInstrument> instruments)
    {
        List<InstrumentAnalysisResult> results = new List<InstrumentAnalysisResult>();
        foreach (var instrument in instruments)
            results.Add(AnalyzeInstrumentWithNews(news, instrument).GetAwaiter().GetResult());
        return results;
    }

    private async Task<InstrumentAnalysisResult> AnalyzeInstrumentWithNews(
        NewsItem news, FinancialInstrument instrument)
    {
        try
        {
            // Свечные данные добавляют технический контекст к фундаментальной новости.
            var candleData = _getCandlesFunction(instrument.Ticker).GetAwaiter().GetResult();
            var candleJson = JsonSerializer.Serialize(candleData, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            });
            var prompt = $$"""
            НОВОСТЬ:
            - Заголовок: {{news.Title}}
            - Дата публикации: {{news.PublishDate:yyyy-MM-dd HH:mm}}
            - Источник: {{news.Source}}
            - Текст: {{news.Content}}

            ИНСТРУМЕНТ: {{instrument.Ticker}} - {{instrument.Name}}
            СЕКТОР: {{instrument.Sector}}

            СВЕЧНЫЕ ДАННЫЕ JSON:
            {{candleJson}}

            ИНСТРУКЦИИ:
            1. Проанализируй влияние новости на {{instrument.Ticker}}.
            2. Если в новости есть ссылка — используй веб-поиск.
            3. Учитывай техническую картину и фундаментальный фон.
            4. Дай конкретные рекомендации для трейдинга.
            5. Ответ строго в JSON-формате без рассуждений.
            """;

            var messages = new[]
            {
                new { role = "system", content = GetInstrumentAnalysisSystemPrompt(true) },
                new { role = "user", content = prompt }
            };

            var response = CallOpenAiApi(messages, ResponseFormatComplex()).GetAwaiter().GetResult();

            return ParseInstrumentAnalysisResponse(response, news, instrument, candleData != null);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error analyzing instrument {Ticker} for news: {NewsId}",
                instrument.Ticker, news.Id);

            return CreateFallbackInstrumentResult(news, instrument,
                $"Ошибка анализа: {ex.Message}");
        }
    }

    private string GetInstrumentAnalysisSystemPrompt(bool usedescription = false)
    {
        var addon = $@"
        с использованием следующих описаний полей.
        - instrumentTicker: тикер инструмента в верхнем регистре, например 'AAPL'
        - volatilityImpact: как новость влияет на волатильность
        - expectedMovementPercent: прогноз движения цены в %
        - expectedDirection: UP / DOWN / NEUTRAL
        - timeframeHours: горизонт прогноза в часах
        - confidenceLevel: уверенность (0–1)
        - fundamentalImpact: фундаментальный эффект
        - recommendedAction: BUY_CALL_OPTION / BUY_PUT_OPTION / HOLD / AVOID
        - riskLevel: LOW / MEDIUM / HIGH
        - reasoning: обоснование прогноза
        - analyzedReferences: список ссылок, которые были использованы
        - newsResume: краткое резюме новости
";
        return $@"
        Ты финансовый аналитик с большим опытом работы в хедж-фондах. 
        Рассматривай все новости с точки зрения волатильности для заработка на опционах.
        Всегда отвечай строго в заданном JSON формате
        {(usedescription ? addon : "")}";
    }

    private InstrumentAnalysisResult ParseInstrumentAnalysisResponse(
        OpenAiResponse response, NewsItem news, FinancialInstrument instrument, bool usedCandleData)
    {
        try
        {
            if (response?.Choices?[0]?.Message?.Content == null)
                throw new Exception("Empty API response");

            var analysis = JsonSerializer.Deserialize<InstrumentAnalysis>(
                response.Choices[0].Message.Content,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                }
            );

            return new InstrumentAnalysisResult
            {
                NewsId = news.Id,
                InstrumentTicker = instrument.Ticker,
                InstrumentName = instrument.Name,
                VolatilityImpact = analysis.VolatilityImpact ?? "low",
                ExpectedMovementPercent = analysis.ExpectedMovementPercent,
                ExpectedDirection = analysis.ExpectedDirection ?? "neutral",
                TimeframeHours = analysis.TimeframeHours,
                ConfidenceLevel = Math.Clamp(analysis.ConfidenceLevel, 0, 1),
                FundamentalImpact = analysis.FundamentalImpact ?? "neutral",
                RecommendedAction = analysis.RecommendedAction ?? "hold",
                RiskLevel = analysis.RiskLevel ?? "medium",
                Reasoning = analysis.Reasoning ?? "Анализ не выполнен",
                AnalysisTimestamp = DateTime.UtcNow,
                UsedCandleData = usedCandleData,
                AnalyzedReferences = analysis.AnalyzedReferences,
                NewsResume = analysis.NewsResume
            };
        }
        catch
        {
            return CreateFallbackInstrumentResult(news, instrument, "Ошибка парсинга ответа");
        }
    }

    private bool ValidateAnalysisResponse(InstrumentAnalysisResult result)
    {
        if (string.IsNullOrEmpty(result.InstrumentTicker)) return false;
        if (result.ExpectedMovementPercent < 0) return false;
        if (result.ConfidenceLevel < 0 || result.ConfidenceLevel > 1) return false;
        if (string.IsNullOrEmpty(result.Reasoning) || result.Reasoning.Length < 30) return false;
        if (result.TimeframeHours <= 0) return false;
        return true;
    }

    private InstrumentAnalysisResult CreateFallbackInstrumentResult(
        NewsItem news, FinancialInstrument instrument, string reason)
    {
        return new InstrumentAnalysisResult
        {
            NewsId = news.Id,
            InstrumentTicker = instrument.Ticker,
            InstrumentName = instrument.Name,
            VolatilityImpact = "low",
            ExpectedMovementPercent = 0,
            ExpectedDirection = "neutral",
            TimeframeHours = 0,
            ConfidenceLevel = 0.1,
            FundamentalImpact = "neutral",
            RecommendedAction = "hold",
            RiskLevel = "medium",
            Reasoning = reason,
            AnalysisTimestamp = DateTime.UtcNow,
            UsedCandleData = false,
            AnalyzedReferences = new List<string>(),
        };
    }
    private static object ResponseFormatComplex()
    {
        // JSON Schema уменьшает риск свободного текста вместо машинно-читаемого результата.
        return new
        {
            type = "json_schema",
            json_schema = new
            {
                name = "instrument_analysis_schema",
                schema = new
                {
                    type = "object",
                    properties = new
                    {
                        instrumentTicker = new
                        {
                            type = "string",
                        },
                        volatilityImpact = new
                        {
                            type = "string",
                        },
                        expectedMovementPercent = new
                        {
                            type = "number",
                        },
                        expectedDirection = new
                        {
                            type = "string",
                        },
                        timeframeHours = new
                        {
                            type = "number",
                        },
                        confidenceLevel = new
                        {
                            type = "number",
                        },
                        fundamentalImpact = new
                        {
                            type = "string",
                        },
                        recommendedAction = new
                        {
                            type = "string",
                        },
                        riskLevel = new
                        {
                            type = "string",
                        },
                        reasoning = new
                        {
                            type = "string",
                        },
                        analyzedReferences = new
                        {
                            type = "array",
                            items = new { type = "string" }
                        },
                        newsResume = new
                        {
                            type = "string",
                        }
                    },
                    required = new[]
                    {
                        "instrumentTicker",
                        "volatilityImpact",
                        "expectedMovementPercent",
                        "expectedDirection",
                        "timeframeHours",
                        "confidenceLevel",
                        "fundamentalImpact",
                        "recommendedAction",
                        "riskLevel",
                        "reasoning",
                        "analyzedReferences",
                        "newsResume"
                    },
                    additionalProperties = false
                }
            }
        };

    }
    private static object ResponseFormatRelevantInstruments()
    {
        return new
        {
            type = "json_schema",
            json_schema = new
            {
                name = "relevant_instruments_schema",
                schema = new
                {
                    type = "object",
                    properties = new
                    {
                        relevantInstruments = new
                        {
                            type = "array",
                            items = new { type = "string" }
                        }
                    },
                    required = new[] { "relevant_instruments" },
                    additionalProperties = false
                }
            }
        };
    }
    private async Task<OpenAiResponse> CallOpenAiApi(
        object[] messages, object responseFormat = null, bool usewebsearch = true)
    {
        // Используется search-preview модель, потому что новость может содержать только ссылку или требовать уточнений.
        var requestData = new
        {
            model = "gpt-4o-search-preview",
            messages = messages,
            max_completion_tokens = 3000,
            stream = false,
            web_search_options = new { search_context_size = "medium" },
            response_format = responseFormat
        };


        var json = JsonSerializer.Serialize(requestData, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });

        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = _httpClient.PostAsync("chat/completions", content).GetAwaiter().GetResult();

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            _logger?.LogError("API error: {StatusCode} - {Content}", response.StatusCode, errorContent);
            throw new Exception($"API request failed: {response.StatusCode}");
        }

        var responseString = await response.Content.ReadAsStringAsync();
        var res = JsonSerializer.Deserialize<OpenAiResponse>(responseString, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        return res;
    }


    public void Dispose()
    {
        _restClient?.Dispose();
    }
}

// ---------------------------
// OpenAI модели
// ---------------------------
public class OpenAiResponse
{
    public string Id { get; set; }
    public string Object { get; set; }
    public long Created { get; set; }
    public string Model { get; set; }
    public List<OpenAiChoice> Choices { get; set; }
    public Usage Usage { get; set; }
}

public class OpenAiChoice
{
    public int Index { get; set; }
    public OpenAiMessage Message { get; set; }
    public string FinishReason { get; set; }
}

public class OpenAiMessage
{
    public string Role { get; set; }
    public string Content { get; set; }
}

public class Usage
{
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int TotalTokens { get; set; }
}

// ---------------------------
// Модели данных
// ---------------------------
public class InstrumentIdentificationResponse
{
    public List<string> RelevantInstruments { get; set; } = new List<string>();
}

public class InstrumentAnalysis
{
    public string InstrumentTicker { get; set; }
    public string VolatilityImpact { get; set; }
    public double ExpectedMovementPercent { get; set; }
    public string ExpectedDirection { get; set; }
    public double TimeframeHours { get; set; }
    public double ConfidenceLevel { get; set; }
    public string FundamentalImpact { get; set; }
    public string RecommendedAction { get; set; }
    public string RiskLevel { get; set; }
    public string Reasoning { get; set; }
    public List<string> AnalyzedReferences { get; set; }
    public string NewsResume { get; set; }
}

public class InstrumentAnalysisResult
{
    public string NewsId { get; set; }
    public string InstrumentTicker { get; set; }
    public string InstrumentName { get; set; }
    public string VolatilityImpact { get; set; }
    public double ExpectedMovementPercent { get; set; }
    public string ExpectedDirection { get; set; }
    public double TimeframeHours { get; set; }
    public double ConfidenceLevel { get; set; }
    public string FundamentalImpact { get; set; }
    public string RecommendedAction { get; set; }
    public string RiskLevel { get; set; }
    public string Reasoning { get; set; }
    public DateTime AnalysisTimestamp { get; set; }
    public bool UsedCandleData { get; set; }
    public List<string> AnalyzedReferences { get; set; }
    public string NewsResume { get; set; }
}

public class NewsItem
{
    public string Id { get; set; }
    public string Title { get; set; }
    public string Content { get; set; }
    public DateTime PublishDate { get; set; }
    public string Source { get; set; }
}

public class FinancialInstrument
{
    public string Ticker { get; set; }
    public string Name { get; set; }
    public string Sector { get; set; }
}

public class MultiTimeframeCandleData
{
    public List<CandleStick> H1Candles { get; set; }
    public List<CandleStick> D1Candles { get; set; }
    public List<CandleStick> W1Candles { get; set; }
    public List<CandleStick> M1Candles { get; set; }
}

public class CandleStick
{
    public DateTime Date { get; set; }
    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal Close { get; set; }
    public decimal Volume { get; set; }
}
