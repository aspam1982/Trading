using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using RestSharp;

namespace DeepSeekAnalyser;
/// <summary>
/// Альтернативный AI-анализатор новостей через DeepSeek.
/// Архитектура повторяет OpenAI-вариант: идентификация релевантных инструментов,
/// подгрузка свечей и детальный JSON-анализ влияния новости на каждый тикер.
/// </summary>
public class EnhancedNewsAnalyzer : IDisposable
{
    private readonly string _deepSeekApiKey;
    private readonly RestClient _restClient;
    private readonly IMemoryCache _cache;
    private readonly ILogger<EnhancedNewsAnalyzer> _logger;
    private readonly Func<string, Task<MultiTimeframeCandleData>> _getCandlesFunction;
    private readonly List<FinancialInstrument> _relevantInstruments;

    private readonly TimeSpan _cacheDuration = TimeSpan.FromHours(4);
    private const string DeepSeekApiUrl = "https://api.deepseek.com";
    private const string ModelName = "deepseek-chat";

    public EnhancedNewsAnalyzer(
        string apiKey,
        List<FinancialInstrument> relevantInstruments,
        Func<string, Task<MultiTimeframeCandleData>> getCandlesFunction,
        IMemoryCache cache,
        ILogger<EnhancedNewsAnalyzer> logger = null)
    {
        _deepSeekApiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _relevantInstruments = relevantInstruments ?? throw new ArgumentNullException(nameof(relevantInstruments));
        _getCandlesFunction = getCandlesFunction ?? throw new ArgumentNullException(nameof(getCandlesFunction));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _logger = logger;

        _restClient = new RestClient(new RestClientOptions(DeepSeekApiUrl)
        {
            ThrowOnAnyError = false,
            Timeout = new TimeSpan(0,0,60)
        });
    }

    public async Task<List<InstrumentAnalysisResult>> AnalyzeNewsAsync(NewsItem news)
    {
        var cacheKey = $"news_analysis_v4_{news.Id}_{news.GetHashCode()}";

        if (_cache.TryGetValue(cacheKey, out List<InstrumentAnalysisResult> cachedResult))
        {
            _logger?.LogInformation("Returning cached analysis for news: {NewsId}", news.Id);
            return cachedResult;
        }

        try
        {
            // Этап 1: идентификация релевантных инструментов.
            var relevantInstruments = await IdentifyRelevantInstruments(news);

            if (!relevantInstruments.Any())
            {
                _logger?.LogInformation("No relevant instruments found for news: {NewsId}", news.Id);
                return new List<InstrumentAnalysisResult>();
            }

            // Этап 2: анализ для каждого релевантного инструмента.
            var analysisResults = await AnalyzeInstrumentsParallel(news, relevantInstruments);

            // Фильтруем ответы, которые не прошли базовый контракт качества.
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
        2. Верни ответ ТОЛЬКО в формате JSON: {"relevant_instruments": ["string"]}
        3. Указывай только тикеры инструментов в верхнем регистре
        4. Ищи дополнительную информацию в интернете в случае необходимости используя веб посик.
        5. Если в содержимом есть только ссылка на содержимое новости - внимательно проанализируй содержимое страницы по ссылке.
        6. Включай только инструменты, которые действительно могут испытать значительное движение цены более 3% под влиянием новости, если значительного движения цены по инструменту не ожидается не надо включать его в список релевантных инструментов
        """;

        var messages = new[]
        {
            new { role = "system", content = "Ты финансовый аналитик с большим опытом работы в хедж фондах. Имеешь большой опыт в торговле опционами. Рассматривай все новости с точки зрения использования вызванной новостью волатильности для заработка на опционах. Отвечай строго в указанном JSON формате." },
            new { role = "user", content = prompt }
        };

        try
        {
            var response = await CallDeepSeekApi(messages, null, new { type = "json_object" });

            if (response?.Choices?[0]?.Message?.Content == null)
                return new List<FinancialInstrument>();

            var result = JsonSerializer.Deserialize<InstrumentIdentificationResponse>(
                response.Choices[0].Message.Content,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }
            );

            // Фильтруем только инструменты из локального списка, чтобы AI не добавил неожиданные тикеры.
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
        var analysisTasks = instruments.Select(instrument =>
            AnalyzeInstrumentWithNews(news, instrument));

        var results = await Task.WhenAll(analysisTasks);
        return results.ToList();
    }

    private async Task<InstrumentAnalysisResult> AnalyzeInstrumentWithNews(
        NewsItem news, FinancialInstrument instrument)
    {
        try
        {
            // Получаем свечные данные перед AI-вызовом: они нужны для технического блока анализа.
            var candleData = await _getCandlesFunction(instrument.Ticker);
            var candleJson = JsonSerializer.Serialize(candleData, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            });
            var prompt = $$"""
            НОВОСТЬ ДЛЯ АНАЛИЗА:
            - Заголовок: {{news.Title}}
            - Дата публикации: {{news.PublishDate:yyyy-MM-dd HH:mm}}
            - Источник: {{news.Source}}
            - Текст: {{news.Content}}

            ИНСТРУМЕНТ: {{instrument.Ticker}} - {{instrument.Name}}
            СЕКТОР: {{instrument.Sector}}

            СВЕЧНЫЕ ДАННЫЕ ДОСТУПНЫ ДЛЯ АНАЛИЗА В ФОРМАТЕ JSON:
            {{candleJson}}

            ИНСТРУКЦИИ:
            1. Проанализируй влияние новости на конкретный инструмент {{instrument.Ticker}}
            2. Если в тестке новости есть ссылка - внимательно проанализируй содержимое страницы по ссылке.
            3. Учти техническую картину на основе свечных данных
            4. Оцени фундаментальное воздействие новости
            5. Дай конкретные рекомендации для трейдинга
            6. Верни ответ в строго заданном JSON формате
            7. Используй веб поиск для более глубокого анализа информации
            """;

            var messages = new[]
            {
                new { role = "system", content = GetInstrumentAnalysisSystemPrompt() },
                new { role = "user", content = prompt }
            };

            var response = await CallDeepSeekApi(messages, null, new { type = "json_object" });

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

    private string GetInstrumentAnalysisSystemPrompt()
    {
        return """
        Ты финансовый аналитик с большим опытом работы в хедж фондах. 
        Имеешь большой опыт в торговле опционами. 
        Рассматривай все новости с точки зрения использования, вызванной новостью волатильности, для заработка на опционах.
        Всегда отвечай ТОЛЬКО в формате JSON следующего вида:
        {
            "instrument_ticker": "string",
            "volatility_impact": "extreme|high|medium|low",
            "expected_movement_percent": number,
            "expected_direction": "up|down|neutral",
            "timeframe_hours": number,
            "confidence_level": number(0-1),
            "technical_analysis": {
                "support_levels": [number],
                "resistance_levels": [number],
                "trend_direction": "bullish|bearish|sideways",
                "volume_analysis": "increasing|decreasing|stable"
            },
            "fundamental_impact": "positive|negative|neutral",
            "recommended_action": "buy|sell|hold|strong_buy|strong_sell",
            "risk_level": "high|medium|low",
            "reasoning": "string", //На русском языке
            "analyzed_references" : [string], //ссылки на материалы использованные при веб-посике информации
            "news_resume" : "string" //краткое резюме новости
        }

        [АНАЛИТИЧЕСКИЕ ПРИНЦИПЫ]
        - Сочетай фундаментальный анализ новости с техническим анализом
        - Учитывай текущие уровни поддержки/сопротивления
        - Оценивай объемы торгов и их динамику
        - Определяй направление тренда
        - Рассчитывай потенциальные цели движения цены
        - Обосновывай рекомендации конкретными фактами из новости и тех анализа
        """;
    }

    private InstrumentAnalysisResult ParseInstrumentAnalysisResponse(
        DeepSeekResponse response, NewsItem news, FinancialInstrument instrument, bool usedCandleData)
    {
        try
        {
            if (response?.Choices?[0]?.Message?.Content == null)
                throw new Exception("Empty API response");

            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                PropertyNameCaseInsensitive = true
            };

            var analysis = JsonSerializer.Deserialize<InstrumentAnalysis>(response.Choices[0].Message.Content, options);

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
                TechnicalAnalysis = analysis.TechnicalAnalysis ?? new TechnicalAnalysisData(),
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
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error parsing analysis response for {Ticker}", instrument.Ticker);
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
            TechnicalAnalysis = new TechnicalAnalysisData(),
            FundamentalImpact = "neutral",
            RecommendedAction = "hold",
            RiskLevel = "medium",
            Reasoning = reason,
            AnalysisTimestamp = DateTime.UtcNow,
            UsedCandleData = false,
            AnalyzedReferences = new List<string>(),
        };
    }

    private async Task<DeepSeekResponse> CallDeepSeekApi(
        object[] messages, object[] tools = null, object responseFormat = null)
    {
        // Центральная точка HTTP-вызова DeepSeek API: здесь задается модель, web_search и JSON-формат.
        var requestData = new
        {
            model = ModelName,
            messages = messages,
            tools = tools,
            tool_choice = tools != null && tools.Length > 0 ? "auto" : "none",
            temperature = 0.1,
            max_tokens = 3000,
            stream = false,
            web_search = true,
            response_format = responseFormat
        };

        var request = new RestRequest("/chat/completions", Method.Post);
        request.AddHeader("Authorization", $"Bearer {_deepSeekApiKey}");
        request.AddHeader("Content-Type", "application/json");

        var json = JsonSerializer.Serialize(requestData, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        request.AddStringBody(json, ContentType.Json);

        var response = await _restClient.ExecutePostAsync(request);

        if (!response.IsSuccessful)
        {
            _logger?.LogError("API error: {StatusCode} - {Content}", response.StatusCode, response.Content);
            throw new Exception($"API request failed: {response.StatusCode}");
        }
        var res = JsonSerializer.Deserialize<DeepSeekResponse>(response.Content, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true
        });
        return res;
    }

    public void Dispose()
    {
        _restClient?.Dispose();
    }
}


// Новые классы для двухэтапного анализа
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
    public TechnicalAnalysisData TechnicalAnalysis { get; set; }
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
    public TechnicalAnalysisData TechnicalAnalysis { get; set; }
    public string FundamentalImpact { get; set; }
    public string RecommendedAction { get; set; }
    public string RiskLevel { get; set; }
    public string Reasoning { get; set; }
    public DateTime AnalysisTimestamp { get; set; }
    public bool UsedCandleData { get; set; }
    public List<string> AnalyzedReferences { get; set; }
    public string NewsResume { get; set; }
}

public class TechnicalAnalysisData
{
    public List<decimal> SupportLevels { get; set; } = new List<decimal>();
    public List<decimal> ResistanceLevels { get; set; } = new List<decimal>();
    public string TrendDirection { get; set; } = "sideways";
    public string VolumeAnalysis { get; set; } = "stable";
}
// Классы для работы с DeepSeek API
public class DeepSeekResponse
{
    public string Id { get; set; }
    public string Object { get; set; }
    public long Created { get; set; }
    public string Model { get; set; }
    public List<Choice> Choices { get; set; }
    public Usage Usage { get; set; }
    public string SystemFingerprint { get; set; }
}

public class Choice
{
    public int Index { get; set; }
    public Message Message { get; set; }
    public object Logprobs { get; set; }
    public string FinishReason { get; set; }
}

public class Message
{
    public string Role { get; set; }
    public string Content { get; set; }
    public List<ToolCall> ToolCalls { get; set; }
}

public class ToolCall
{
    public string Id { get; set; }
    public string Type { get; set; }
    public FunctionCall Function { get; set; }
}

public class FunctionCall
{
    public string Name { get; set; }
    public string Arguments { get; set; }
}

public class Usage
{
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int TotalTokens { get; set; }
    public PromptTokensDetails PromptTokensDetails { get; set; }
    public int PromptCacheHitTokens { get; set; }
    public int PromptCacheMissTokens { get; set; }
}

public class PromptTokensDetails
{
    public int CachedTokens { get; set; }
}

// Классы для запросов данных
public class CandleDataRequest
{
    public string Ticker { get; set; }
}

// Классы для анализа волатильности
public class VolatilityAnalysis
{
    public List<string> RelevantTickers { get; set; }
    public string VolatilityImpact { get; set; }
    public double ExpectedMovementPercent { get; set; }
    public double ExpectedDurationHours { get; set; }
    public double ConfidenceLevel { get; set; }
    public bool UsedCandleData { get; set; }
    public bool UsedWebSearch { get; set; }
    public string Reasoning { get; set; }
    public bool ImmediateActionRequired { get; set; }
    public string RecommendedAction { get; set; }
}

public class VolatilityAnalysisResult
{
    public string NewsId { get; set; }
    public List<string> RelevantTickers { get; set; }
    public string VolatilityImpact { get; set; }
    public double ExpectedMovementPercent { get; set; }
    public double ExpectedDurationHours { get; set; }
    public double ConfidenceLevel { get; set; }
    public bool UsedCandleData { get; set; }
    public bool UsedWebSearch { get; set; }
    public string Reasoning { get; set; }
    public bool ImmediateActionRequired { get; set; }
    public string RecommendedAction { get; set; }
    public DateTime AnalysisTimestamp { get; set; }
}

// Классы данных
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

// Классы для статистики использования
public class AnalysisStatistics
{
    public int TotalAnalyses { get; set; }
    public int SuccessfulAnalyses { get; set; }
    public int FailedAnalyses { get; set; }
    public double AverageConfidence { get; set; }
    public Dictionary<string, int> VolatilityImpactCounts { get; set; }
    public Dictionary<string, int> ActionRecommendations { get; set; }
}

// Классы для ошибок
public class AnalysisError
{
    public string NewsId { get; set; }
    public DateTime ErrorTime { get; set; }
    public string ErrorMessage { get; set; }
    public string StackTrace { get; set; }
    public string Source { get; set; }
}

// Классы для конфигурации
public class AnalyzerConfiguration
{
    public int MaxIterations { get; set; } = 3;
    public int RequestTimeoutSeconds { get; set; } = 45;
    public double Temperature { get; set; } = 0.1;
    public int MaxTokens { get; set; } = 4000;
    public bool EnableWebSearch { get; set; } = true;
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromHours(4);
}

// Классы для мониторинга
public class PerformanceMetrics
{
    public TimeSpan AverageResponseTime { get; set; }
    public TimeSpan MaxResponseTime { get; set; }
    public TimeSpan MinResponseTime { get; set; }
    public int ApiCallsPerMinute { get; set; }
    public int FunctionCallsPerAnalysis { get; set; }
    public int WebSearchUsageCount { get; set; }
}

// Классы для валидации
public class ValidationResult
{
    public bool IsValid { get; set; }
    public List<string> Errors { get; set; }
    public List<string> Warnings { get; set; }
}

// Классы для логов
public class AnalysisLogEntry
{
    public DateTime Timestamp { get; set; }
    public string NewsId { get; set; }
    public string Level { get; set; }
    public string Message { get; set; }
    public string Exception { get; set; }
    public Dictionary<string, object> Properties { get; set; }
}
