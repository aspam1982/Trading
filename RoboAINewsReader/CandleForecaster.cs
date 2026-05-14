using CommonClasses;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using static CommonClasses.HistoricalData;

namespace TechnicalForecaster
{
    /// <summary>
    /// Клиент технического прогнозирования по свечам.
    /// Получает подготовленные OHLCV-данные разных таймфреймов, отправляет их в OpenAI
    /// и ожидает строго структурированный JSON с прогнозом на несколько горизонтов.
    /// </summary>
    public class CandleForecaster : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly bool _ownsClient;
        private readonly string _apiKey;
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };

        public CandleForecaster(string apiKey, HttpClient? httpClient = null)
        {
            _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));

            if (httpClient is null)
            {
                _httpClient = new HttpClient
                {
                    BaseAddress = new Uri("https://api.openai.com/v1/")
                };
                _ownsClient = true;
            }
            else
            {
                _httpClient = httpClient;
                if (_httpClient.BaseAddress == null)
                    _httpClient.BaseAddress = new Uri("https://api.openai.com/v1/");
            }

            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _apiKey);
            _httpClient.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));
        }

        /// <summary>
        /// Получить прогноз по свечам в TA-подходе без новостей и внешних данных.
        /// Daily отвечает за ближайшую динамику, weekly - за среднесрочную структуру,
        /// monthly - за старший тренд и крупные уровни.
        /// </summary>
        public async Task<ForecastOnly> GetForecastAsync(
            ForecastRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            // Модель получает только последние свечи, поэтому без достаточной дневной истории прогноз не строим.
            if (request.Daily == null || request.Daily.Count < 20)
                throw new ArgumentException("Нужно минимум 20 дневных свечей (рекомендую 50).", nameof(request));

            // ----- 1) System prompt -----
            var system = """
            You are a quantitative technical analyst.

            You receive OHLCV candles for three timeframes:
            - "daily"   : short-term price action (1D candles)
            - "weekly"  : medium-term structure (1W candles)
            - "monthly" : macro trend (1M candles)

            Use:
            - monthly candles to understand long-term trend and major levels,
            - weekly candles to understand medium-term trend and volatility regime,
            - daily candles to understand recent momentum and local noise.

            Combine signals from all three timeframes. 
            If monthly and weekly trends align with daily, increase confidence.
            If timeframes conflict, reduce confidence.

            You must return EXACTLY 7 forecast items with the following periods:
            "1d", "5d", "10d", "15d", "20d", "25d", "30d".

            Only use the provided candles. 
            Do NOT use any external data, fundamentals, macro news, or web search.

            Output MUST follow the JSON schema exactly.

            Method (high-level):
            - infer trend regime from prices (e.g. simple moving averages slopes),
            - estimate volatility using candle ranges,
            - estimate momentum using simple price rate-of-change,
            - produce probabilistic forecast of growth/decline (in +/-percent) and confidence (0..100)
              for horizons 1d, 5d, 10d, 15d, 20d, 25d, 30d.
            The "confidence_pct" reflects statistical strength of the detected regime and
            recent volatility clustering, not certainty. Keep values realistic.
            """;

            // Передаем свечи как JSON: это проще валидировать и стабильнее для response_format.
            var userPayload = new
            {
                instruction = "Generate forecast for EXACTLY these horizons: 1d, 5d, 10d, 15d, 20d, 25d, 30d.",
                ticker = request.Ticker,
                base_timeframe = request.Timeframe, // например "1D"
                daily = request.Daily ?? new List<Candle>(),
                weekly = request.Weekly ?? new List<Candle>(),
                monthly = request.Monthly ?? new List<Candle>()
            };

            var userContentJson = System.Text.Json.JsonSerializer.Serialize(userPayload, JsonOpts);

            // response_format фиксирует контракт: ровно 7 прогнозов для заранее заданных горизонтов.
            var body = new
            {
                model = "gpt-4o-mini", // можно заменить на "gpt-5.1", если решишься
                temperature = 0.0,
                seed = 12345, // фиксированный сид для воспроизводимости
                response_format = new
                {
                    type = "json_schema",
                    json_schema = new
                    {
                        name = "ForecastResponse",
                        strict = true,
                        schema = new
                        {
                            type = "object",
                            additionalProperties = false,
                            required = new[] { "forecast" },
                            properties = new
                            {
                                forecast = new
                                {
                                    type = "array",
                                    minItems = 7,
                                    maxItems = 7,
                                    items = new
                                    {
                                        type = "object",
                                        additionalProperties = false,
                                        required = new[] { "period", "growth_pct", "confidence_pct" },
                                        properties = new
                                        {
                                            period = new
                                            {
                                                type = "string",
                                                // можно ещё сильнее зажать:
                                                // @enum = new[] { "1d", "5d", "10d", "15d", "20d", "25d", "30d" }
                                            },
                                            growth_pct = new { type = "number" },
                                            confidence_pct = new
                                            {
                                                type = "integer",
                                                minimum = 0,
                                                maximum = 100
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                },
                messages = new object[]
                {
                    new { role = "system", content = system },
                    new { role = "user",   content = userContentJson }
                }
            };

            var content = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(body, JsonOpts),
                Encoding.UTF8,
                "application/json");

            // Выполняем запрос и читаем сырой ответ, чтобы при ошибках API видеть тело ответа.
            using var response = _httpClient.PostAsync("chat/completions", content, cancellationToken).GetAwaiter().GetResult();
            var raw = response.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"OpenAI API error {(int)response.StatusCode} ({response.StatusCode}): {raw}");
            }

            // В chat/completions JSON лежит внутри choices[0].message.content.
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            if (!root.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0)
            {
                throw new InvalidOperationException("Ответ OpenAI не содержит choices.");
            }

            var firstChoice = choices[0];

            if (!firstChoice.TryGetProperty("message", out var message))
                throw new InvalidOperationException("Ответ OpenAI не содержит message.");

            if (!message.TryGetProperty("content", out var contentProp))
                throw new InvalidOperationException("Ответ OpenAI не содержит content.");

            var contentString = contentProp.GetString();
            if (string.IsNullOrWhiteSpace(contentString))
                throw new InvalidOperationException("Пустой content в ответе OpenAI.");

            // contentString уже должен быть JSON по нашей схеме: {"forecast":[{...},...]}.
            var result = System.Text.Json.JsonSerializer.Deserialize<ForecastOnly>(contentString, JsonOpts);

            if (result == null || result.Forecast == null || result.Forecast.Count == 0)
            {
                throw new InvalidOperationException("Не удалось десериализовать ForecastOnly из ответа модели.");
            }

            // Защищаем downstream-графики от неполного ответа модели.
            if (result.Forecast.Count != 7)
            {
                throw new InvalidOperationException($"Модель вернула {result.Forecast.Count} элементов вместо 7.");
            }

            return result;
        }

        public void Dispose()
        {
            if (_ownsClient)
                _httpClient.Dispose();
        }
    }

    // DTO для запроса и ответа модели. Имена полей короткие, чтобы уменьшить размер prompt.

    public record ForecastRequest
    {
        // 50 дневных (или меньше)
        [JsonPropertyName("daily")]
        public List<Candle> Daily { get; init; } = new();

        // 50 недельных (или меньше, опционально)
        [JsonPropertyName("weekly")]
        public List<Candle> Weekly { get; init; } = new();

        // 50 месячных (или меньше, опционально)
        [JsonPropertyName("monthly")]
        public List<Candle> Monthly { get; init; } = new();

        [JsonPropertyName("ticker")]
        public string Ticker { get; init; } = "UNKNOWN";

        // Базовый ТФ, например "1D" — для ориентира
        [JsonPropertyName("timeframe")]
        public string Timeframe { get; init; } = "1D";
    }

    public record Candle
    {
        [JsonPropertyName("t")]
        public string T { get; init; } = ""; // дата (ISO-строка или YYYY-MM-DD)

        [JsonPropertyName("o")]
        public decimal O { get; init; }

        [JsonPropertyName("h")]
        public decimal H { get; init; }

        [JsonPropertyName("l")]
        public decimal L { get; init; }

        [JsonPropertyName("c")]
        public decimal C { get; init; }

        [JsonPropertyName("v")]
        public long V { get; init; }
    }

    public record ForecastOnly
    {
        [JsonPropertyName("forecast")]
        public List<ForecastItem> Forecast { get; init; } = new();
    }

    public record ForecastItem
    {
        [JsonPropertyName("period")]
        public string Period { get; init; } = default!; // "1d" | "5d" | "10d" | ...

        [JsonPropertyName("growth_pct")]
        public decimal GrowthPct { get; init; }

        [JsonPropertyName("confidence_pct")]
        public int ConfidencePct { get; init; }
    }

    public class StoredForecastData
    {
        public HistoricalTimeFrame Timeframe { get; set; }
        public string Symbol { get; set; }
        public ConcurrentDictionary<DateTime, ForecastOnly> Forecasts { get; set; } =
            new ConcurrentDictionary<DateTime, ForecastOnly>();

        public void SaveForecasts(string dir)
        {
            // Кэш прогнозов хранится отдельно по тикеру и таймфрейму, чтобы не пересчитывать уже обработанные свечи.
            string filename = Path.Combine(dir, $@"Forecasts_{Symbol}_{Timeframe}.json");
            File.WriteAllText(filename, JsonConvert.SerializeObject(this, Formatting.Indented));
        }

        public static StoredForecastData ReadForecasts(string dir, string symbol, HistoricalTimeFrame tf)
        {
            StoredForecastData res = null;
            string filename = Path.Combine(dir, $@"Forecasts_{symbol}_{tf}.json");
            if (File.Exists(filename))
                res = JsonConvert.DeserializeObject<StoredForecastData>(File.ReadAllText(filename, Encoding.UTF8));
            return res == null
                ? new StoredForecastData
                {
                    Symbol = symbol,
                    Timeframe = tf,
                    Forecasts = new ConcurrentDictionary<DateTime, ForecastOnly>()
                }
                : res;
        }
    }
}
