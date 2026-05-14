// FILE: Domain.cs
// WPF .NET 8
// NuGet: Tinkoff.InvestApi
//
// Обновлено:
// - Ликвидность: только по дневным свечам. Берем свечи за последние 30 дней,
//   отбираем последние 5 ДНЕЙ С ТОРГАМИ (Volume>0). Если <5 — бумагу исключаем.
//   Ликвидность = средний дневной денежный оборот за эти 5 дней.
// - Spread: только по тем же 5 свечам (без стакана).
//   SpreadProxyPct = median( (High-Low)/Close * 100 ).
// - Цена для YTM: берем Close последней торговой дневной свечи (из этих 5).
// - Score: считается по 1Y-forward total return (а не по annualized-YTM),
//   чтобы short-бумаги не “взрывались”.
//
// Логика:
// 1) Rating-first gate для корпоратов (ОФЗ bypass)
// 2) Bucket assignment
// 3) Candles(30d) -> last5 traded days -> turnover, spread proxy, last close price
// 4) Coupons -> YTM(IRR) + Forward1Y total return
// 5) ScoreBond (forward-based, corrected)
// 6) PortfolioAllocator: только Score>0, top-N per bucket, веса ∝ Score,
//    недобор по корзинам -> Money Market
// 7) Expected portfolio yield: Σ(w * Forward1YReturn) + wMM * MM

using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Text.Json;
using Tinkoff.InvestApi;
using Tinkoff.InvestApi.V1;

namespace BondSelectorWpf;

/// <summary>
/// Корзины портфеля. Для стратегии "ОФЗ-ИН" ключевой является B_OFZ_Infl:
/// это защитный инфляционный слой из ОФЗ с индексируемым номиналом.
/// Остальные корзины оставлены как часть общего облигационного каркаса.
/// </summary>
public enum BondBucket
{
    Unknown = 0,
    A_OFZ_Fixed = 1,
    B_OFZ_Infl = 2,
    C_Corp_AA = 3,
    D_Short = 4
}

public record TaggedBond(
    string Figi,
    string Ticker,
    string Isin,
    string Name,
    string Currency,
    DateTime EffectiveMaturityDateUtc,
    BondBucket Bucket,
    double YtmAnnual,            // decimal (0.1498)
    double Forward1YReturn,      // decimal (0.17 = +17% total return over 1 year)
    double SpreadPct,            // percent (proxy from candles, e.g. 0.40)
    double AvgDailyTurnoverRub,  // ₽ (avg of last 5 traded days)
    double YearsToMaturity,
    string IssuerKey,
    string TagReason,
    double Score,
    int BestBidQty,              // no orderbook in this model -> 0
    int BestAskQty               // no orderbook in this model -> 0
);

public record AllocatedBond(
    TaggedBond Bond,
    double PortfolioWeight,   // 0..1
    double BucketWeight       // 0..1 (allocated to this bucket in portfolio)
);

/// <summary>
/// Конфигурация стратегии отбора. Для ОФЗ-ИН здесь особенно важны диапазон
/// срока, минимальная ликвидность, proxy-спред и целевой вес инфляционной корзины.
/// Эти параметры позволяют держать стратегию консервативной и не масштабировать
/// ее сверх ликвидности узкого рынка ОФЗ-ИН.
/// </summary>
public sealed class StrategyConfig
{
    // ---- Bands ----
    public double OfzFixedMinYears { get; init; } = 2.5;
    public double OfzFixedMaxYears { get; init; } = 5.5;

    public double OfzInflMinYears { get; init; } = 2.0;
    public double OfzInflMaxYears { get; init; } = 8.0;

    public double CorpMinYears { get; init; } = 0.7;
    public double CorpMaxYears { get; init; } = 4.0;

    public double ShortMaxYears { get; init; } = 1.0;

    // Spread proxy based on daily candles: median((High-Low)/Close)*100 over last 5 traded days
    public double MaxSpreadPct { get; init; } = 0.7;

    // Liquidity filter (avg daily turnover in RUB, last 5 traded days)
    public double MinAvgDailyTurnoverRub { get; init; } = 5_000_000;

    // Retail feasibility: exclude huge nominal papers
    public decimal MaxNominalAllowed { get; init; } = 1_000_000m;

    public string Currency { get; init; } = "rub";

    // ---- Rating gate ----
    public string MinCorpRating { get; init; } = "AA-";
    public RatingsDb? RatingsDb { get; init; }

    // ---- Scoring weights ----
    public double WExcess { get; init; } = 1.00;
    public double WSpread { get; init; } = 0.60;
    public double WTerm { get; init; } = 0.10;

    // Short forward-excess cap (to avoid domination by ultra-short papers)
    public double ShortExcessCap { get; init; } = 0.06; // +6% above key, max (forward-based)

    // Liquidity mapping parameters
    public double LiquidityLogScaleMillions { get; init; } = 1_000_000; // scale for turnover in log10(1+turnover/scale)
    public double LiquidityClampMin { get; init; } = 0.90;
    public double LiquidityClampMax { get; init; } = 1.25;

    // ---- Selection "best only" ----
    public int TopNPerBucket { get; init; } = 10;
    public int MaxPerIssuerInBucket { get; init; } = 1;

    // ---- Strategy bucket targets (sum <= 1.0; rest may go to MM) ----
    // По умолчанию: 35% ОФЗ фикс, 15% ОФЗ инфл, 35% корп AA, 15% short.
    public Dictionary<BondBucket, double> BucketTargets { get; init; } = new()
    {
        [BondBucket.A_OFZ_Fixed] = 0.35,
        [BondBucket.B_OFZ_Infl] = 0.15,
        [BondBucket.C_Corp_AA] = 0.35,
        [BondBucket.D_Short] = 0.15
    };

    // Минимум бумаг в корзине, чтобы выполнить 100% целевой доли корзины.
    public int MinCountForFullBucket { get; init; } = 5;

    // Для bondAllocation (сколько вообще держим в облигациях)
    public double ScoreTargetForFullBondAllocation { get; init; } = 0.02; // ~ +2% годовых
    public int NTargetForFullBondAllocation { get; init; } = 12;

    // Доходность денежного рынка (если null — берём keyRatePct)
    public double? MoneyMarketYieldPctOverride { get; init; } = null;
}

public sealed record IssuerRating(string Rating, string Agency);

public sealed class RatingsDb
{
    public DateTime AsOf { get; init; }
    public string MinRating { get; init; } = "AA-";
    public Dictionary<string, IssuerRating> Issuers { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public static RatingsDb Load(string path)
    {
        // JSON намеренно простой: ключи issuers нормализуются так же, как имена эмитентов
        // из T-Invest, после чего рейтинг можно искать без точного совпадения регистра и кавычек.
        var json = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(json);

        var root = doc.RootElement;

        var db = new RatingsDb
        {
            AsOf = root.TryGetProperty("as_of", out var asOfEl) && DateTime.TryParse(asOfEl.GetString(), out var dt)
                ? dt
                : DateTime.MinValue,
            MinRating = root.TryGetProperty("min_rating", out var minEl) ? (minEl.GetString() ?? "AA-") : "AA-",
        };

        if (root.TryGetProperty("issuers", out var issuersEl) && issuersEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in issuersEl.EnumerateObject())
            {
                var issuerName = prop.Name;
                var obj = prop.Value;

                var rating = obj.TryGetProperty("rating", out var rEl) ? (rEl.GetString() ?? "") : "";
                var agency = obj.TryGetProperty("agency", out var aEl) ? (aEl.GetString() ?? "") : "";

                if (!string.IsNullOrWhiteSpace(issuerName) && !string.IsNullOrWhiteSpace(rating))
                {
                    db.Issuers[NormalizeIssuer(issuerName)] =
                        new IssuerRating(rating.Trim().ToUpperInvariant(), agency.Trim());
                }
            }
        }

        return db;
    }

    public bool TryGetIssuerRating(string issuerKeyRaw, out IssuerRating rating)
        => Issuers.TryGetValue(NormalizeIssuer(issuerKeyRaw), out rating);

    public static string NormalizeIssuer(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var t = s.Trim().ToUpperInvariant()
            .Replace("«", "").Replace("»", "")
            .Replace("\"", "")
            .Replace(".", " ")
            .Replace("  ", " ");
        while (t.Contains("  ")) t = t.Replace("  ", " ");
        return t;
    }
}

public static class RatingScaleRu
{
    private static readonly Dictionary<string, int> Score = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AAA"] = 100,
        ["AA+"] = 95,
        ["AA"] = 90,
        ["AA-"] = 85,
        ["A+"] = 80,
        ["A"] = 75,
        ["A-"] = 70,
        ["BBB+"] = 65,
        ["BBB"] = 60,
        ["BBB-"] = 55
    };

    public static bool IsAtLeast(string rating, string minRating)
    {
        if (!Score.TryGetValue(rating, out var r)) return false;
        if (!Score.TryGetValue(minRating, out var m)) return false;
        return r >= m;
    }
}

public static class QuotationExt
{
    public static decimal ToDecimal(this Quotation q)
        => q is null ? 0m : q.Units + q.Nano / 1_000_000_000m;
}

public static class MoneyValueExt
{
    public static decimal ToDecimal(this MoneyValue v)
        => v is null ? 0m : v.Units + v.Nano / 1_000_000_000m;
}

public sealed class BondAnalytics
{
    public sealed record Last5CandleStats(
        IReadOnlyList<HistoricCandle> Last5Traded,
        double AvgDailyTurnoverRub,
        double SpreadProxyPct,
        decimal LastClosePricePct
    );

    /// <summary>
    /// Берем дневные свечи за последние 30 дней, отбираем последние 5 торговых (Volume>0).
    /// Если меньше 5 — возвращаем null.
    /// Возвращает:
    /// - AvgDailyTurnoverRub: средний дневной оборот (₽)
    /// - SpreadProxyPct: медиана (High-Low)/Close * 100
    /// - LastClosePricePct: Close последней торговой дневной свечи
    /// </summary>
    public static async Task<Last5CandleStats?> GetLast5CandleStatsAsync(
        InvestApiClient client,
        string figi,
        DateTime nowUtc,
        decimal nominal,
        CancellationToken ct)
    {
        var fromUtc = nowUtc.AddDays(-30);

        var resp = await client.MarketData.GetCandlesAsync(
            new GetCandlesRequest
            {
                Figi = figi,
                From = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(
                    DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc)),
                To = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(
                    DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc)),
                Interval = CandleInterval.Day
            },
            cancellationToken: ct);

        var last5 = resp.Candles
            .Where(c => c.Volume > 0)
            .OrderByDescending(c => c.Time)
            .Take(5)
            .ToList();

        if (last5.Count < 5)
            return null;

        // Last close (the most recent traded day)
        var lastClosePct = last5[0].Close.ToDecimal();
        if (lastClosePct <= 0m)
            return null;

        // Avg daily turnover in money (₽): Volume(lots) * priceMoneyPerBond
        // Для облигаций цена обычно в % от номинала.
        double sumTurnover = 0.0;
        var spreads = new List<double>(capacity: 5);

        foreach (var c in last5)
        {
            var closePct = c.Close.ToDecimal();
            if (closePct <= 0m) continue;

            double priceMoney = (double)(nominal * closePct / 100m);
            sumTurnover += priceMoney * c.Volume;

            var hi = (double)c.High.ToDecimal();
            var lo = (double)c.Low.ToDecimal();
            var cl = (double)closePct;
            if (hi > 0 && lo > 0 && cl > 0)
                spreads.Add((hi - lo) / cl * 100.0);
        }

        if (spreads.Count == 0)
            return null;

        spreads.Sort();
        double spreadProxy = spreads[spreads.Count / 2]; // median

        return new Last5CandleStats(
            last5,
            sumTurnover / 5.0,
            spreadProxy,
            lastClosePct);
    }

    // IRR by future cashflows: coupons + nominal at effective maturity.
    // Accrued interest is NOT included (approximation).
    public static double ComputeYtmIrr(
        decimal cleanPricePct,
        decimal nominal,
        IReadOnlyList<(DateTime dt, decimal couponAmount)> couponsUtc,
        DateTime maturityUtc)
    {
        if (nominal <= 0) return double.NaN;

        var priceMoney = nominal * cleanPricePct / 100m;
        var now = DateTime.UtcNow.Date;

        var flows = new List<(double tYears, double cf)>();

        foreach (var (dt, cpn) in couponsUtc)
        {
            if (dt.Date <= now) continue;
            flows.Add(((dt.Date - now).TotalDays / 365.25, (double)cpn));
        }

        if (maturityUtc.Date > now)
            flows.Add(((maturityUtc.Date - now).TotalDays / 365.25, (double)nominal));

        if (flows.Count == 0) return double.NaN;

        double price = (double)priceMoney;
        double r = 0.15;

        for (int i = 0; i < 60; i++)
        {
            double npv = -price;
            double dnpv = 0.0;

            foreach (var (t, cf) in flows)
            {
                double disc = Math.Pow(1.0 + r, t);
                npv += cf / disc;
                dnpv += -t * cf / (disc * (1.0 + r));
            }

            if (Math.Abs(dnpv) < 1e-12) break;

            double step = npv / dnpv;
            r -= step;

            if (double.IsNaN(r) || double.IsInfinity(r)) return double.NaN;
            if (r < -0.95) r = -0.95;
            if (Math.Abs(step) < 1e-10) break;
        }

        return r;
    }

    /// <summary>
    /// 1Y-forward total return:
    /// - cashflows within 1Y are reinvested at mmYield
    /// - if not matured within 1Y, add terminal price at horizon by discounting remaining cashflows at YTM
    /// </summary>
    public static double ComputeForward1YTotalReturn(
        decimal cleanPricePct,
        decimal nominal,
        IReadOnlyList<(DateTime dt, decimal couponAmount)> couponsUtc,
        DateTime maturityUtc,
        double ytmAnnual,
        double mmYieldAnnual)
    {
        if (nominal <= 0) return double.NaN;

        var t0 = DateTime.UtcNow.Date;
        var tH = t0.AddDays(365);

        double P0 = (double)(nominal * cleanPricePct / 100m);
        if (P0 <= 0) return double.NaN;

        // 1) CF <= horizon, reinvest at MM
        double fv = 0.0;

        foreach (var (dt, cpn) in couponsUtc)
        {
            var d = dt.Date;
            if (d <= t0) continue;
            if (d > tH) continue;

            double tau = (tH - d).TotalDays / 365.25;
            fv += (double)cpn * Math.Pow(1.0 + mmYieldAnnual, tau);
        }

        // maturity within horizon -> add nominal and finish
        if (maturityUtc.Date > t0 && maturityUtc.Date <= tH)
        {
            double tau = (tH - maturityUtc.Date).TotalDays / 365.25;
            fv += (double)nominal * Math.Pow(1.0 + mmYieldAnnual, tau);
            return fv / P0 - 1.0;
        }

        // 2) maturity after horizon -> terminal price at horizon (discount remaining CF at YTM)
        double PH = 0.0;

        foreach (var (dt, cpn) in couponsUtc)
        {
            var d = dt.Date;
            if (d <= tH) continue;

            double delta = (d - tH).TotalDays / 365.25;
            PH += (double)cpn / Math.Pow(1.0 + ytmAnnual, delta);
        }

        if (maturityUtc.Date > tH)
        {
            double delta = (maturityUtc.Date - tH).TotalDays / 365.25;
            PH += (double)nominal / Math.Pow(1.0 + ytmAnnual, delta);
        }

        fv += PH;
        return fv / P0 - 1.0;
    }
}

public sealed class BondUniverseBuilder
{
    private readonly InvestApiClient _client;
    private readonly StrategyConfig _cfg;

    public BondUniverseBuilder(InvestApiClient client, StrategyConfig cfg)
    {
        _client = client;
        _cfg = cfg;
    }

    public async Task<List<TaggedBond>> BuildCandidatesAsync(double keyRatePct, CancellationToken ct)
    {
        // Основной конвейер отбора: T-Invest дает полный список облигаций, а дальше каждая
        // бумага последовательно проходит фильтры торговой доступности, валюты, срока,
        // номинала, типа выпуска, ликвидности, спреда и расчетной доходности.
        // Для стратегии "ОФЗ-ИН" основной интерес представляют выпуски серии 52
        // с индексируемым номиналом и достаточным оборотом.
        if (_cfg.RatingsDb == null)
            throw new InvalidOperationException("RatingsDb is null (required for corp rating filter).");

        var bondsResp = await _client.Instruments.BondsAsync(
            new InstrumentsRequest { InstrumentStatus = InstrumentStatus.Base },
            cancellationToken: ct);

        var candidates = new List<TaggedBond>();

        var nowUtc = DateTime.UtcNow.Date;

        // Money market yield for forward-1Y
        double mmYieldPct = _cfg.MoneyMarketYieldPctOverride ?? keyRatePct;
        double mmYield = mmYieldPct / 100.0;

        foreach (var b in bondsResp.Instruments)
        {
            if (!b.ApiTradeAvailableFlag || !b.BuyAvailableFlag) continue;
            if (b.OtcFlag) continue;
            if (!string.Equals(b.Currency, _cfg.Currency, StringComparison.OrdinalIgnoreCase)) continue;

            var effMat = GetEffectiveMaturityUtc(b);
            if (effMat == null) continue;

            var maturityUtc = effMat.Value.Date;
            if (maturityUtc <= nowUtc) continue;

            var years = (maturityUtc - nowUtc).TotalDays / 365.25;

            bool isOfz = IsOfz(b);
            var issuerKey = isOfz ? "MINFIN" : IssuerKeyFromName(b.Name);

            // Nominal / retail feasibility: крупные номиналы исключаются, потому что
            // защитный инфляционный слой должен быть собираемым на розничном счете.
            var nominal = b.Nominal?.ToDecimal() ?? 0m;
            if (nominal <= 0) continue;
            if (nominal > _cfg.MaxNominalAllowed) continue;

            // Rating gate применяется только к корпоративным бумагам. ОФЗ-ИН относятся
            // к суверенному риску, поэтому их отбор держится на сроке, цене и ликвидности.
            IssuerRating? corpRating = null;
            if (!isOfz)
            {
                if (string.IsNullOrWhiteSpace(issuerKey)) continue;
                if (!_cfg.RatingsDb.TryGetIssuerRating(issuerKey, out var ir)) continue;
                if (!RatingScaleRu.IsAtLeast(ir.Rating, _cfg.MinCorpRating)) continue;
                corpRating = ir;
            }

            var (bucket, reason) = ClassifyAfterRatingGate(b, years, isOfz);
            if (bucket == BondBucket.Unknown) continue;

            // Candles-based liquidity + spread + last close price
            var stats = await BondAnalytics.GetLast5CandleStatsAsync(_client, b.Figi, nowUtc, nominal, ct);
            if (stats == null) continue;

            double spreadPct = stats.SpreadProxyPct;
            if (!double.IsFinite(spreadPct) || spreadPct <= 0) continue;
            if (spreadPct > _cfg.MaxSpreadPct) continue;

            double avgTurnover = stats.AvgDailyTurnoverRub;
            if (avgTurnover < _cfg.MinAvgDailyTurnoverRub) continue;

            var lastPricePct = stats.LastClosePricePct;
            if (lastPricePct <= 0m) continue;

            // Coupons
            var from = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(
                DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc));
            var to = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(
                DateTime.SpecifyKind(maturityUtc, DateTimeKind.Utc));

            var couponsResp = await _client.Instruments.GetBondCouponsAsync(new GetBondCouponsRequest
            {
                Figi = b.Figi,
                From = from,
                To = to
            }, cancellationToken: ct);

            var coupons = couponsResp.Events
                .Where(e => e.PayOneBond != null)
                .Select(e => (dt: e.CouponDate.ToDateTime(), couponAmount: e.PayOneBond.ToDecimal()))
                .ToList();

            // YTM (IRR)
            var ytm = BondAnalytics.ComputeYtmIrr(
                cleanPricePct: lastPricePct,
                nominal: nominal,
                couponsUtc: coupons,
                maturityUtc: maturityUtc);

            if (!double.IsFinite(ytm)) continue;

            // Forward-1Y return
            var fwd1y = BondAnalytics.ComputeForward1YTotalReturn(
                cleanPricePct: lastPricePct,
                nominal: nominal,
                couponsUtc: coupons,
                maturityUtc: maturityUtc,
                ytmAnnual: ytm,
                mmYieldAnnual: mmYield);

            if (!double.IsFinite(fwd1y)) continue;

            // Score (forward-based)
            var score = ScoreBond(
                bucket: bucket,
                forward1YReturn: fwd1y,
                keyRatePct: keyRatePct,
                spreadPct: spreadPct,
                yearsToMaturity: years,
                avgDailyTurnoverRub: avgTurnover,
                cfg: _cfg);

            var tagReason = reason;
            if (!isOfz && corpRating != null)
                tagReason = $"{reason}; rating {corpRating.Rating} ({corpRating.Agency})";

            candidates.Add(new TaggedBond(
                b.Figi,
                b.Ticker,
                b.Isin,
                b.Name,
                b.Currency,
                maturityUtc,
                bucket,
                ytm,
                fwd1y,
                spreadPct,
                avgTurnover,
                years,
                issuerKey,
                tagReason,
                score,
                0,
                0
            ));
        }

        return candidates
            .OrderBy(x => x.Bucket)
            .ThenByDescending(x => x.Score)
            .ThenByDescending(x => x.Forward1YReturn)
            .ThenByDescending(x => x.YtmAnnual)
            .ToList();
    }

    private (BondBucket bucket, string reason) ClassifyAfterRatingGate(Bond b, double years, bool isOfz)
    {
        // Классификация идет после базовых фильтров. Для ОФЗ-ИН бумага должна быть
        // распознана как inflation-linked выпуск и попадать в заданный диапазон срока.
        if (years <= _cfg.ShortMaxYears)
            return (BondBucket.D_Short, $"T={years:F2}y <= {_cfg.ShortMaxYears}");

        if (isOfz && IsOfzInflationLinked(b))
        {
            if (years >= _cfg.OfzInflMinYears && years <= _cfg.OfzInflMaxYears)
                return (BondBucket.B_OFZ_Infl, "OFZ-Infl (by name)");
            return (BondBucket.Unknown, "OFZ-Infl out of band");
        }

        if (isOfz)
        {
            if (years >= _cfg.OfzFixedMinYears && years <= _cfg.OfzFixedMaxYears)
                return (BondBucket.A_OFZ_Fixed, "OFZ (by name/ticker)");
            return (BondBucket.Unknown, "OFZ out of band");
        }

        if (years < _cfg.CorpMinYears || years > _cfg.CorpMaxYears)
            return (BondBucket.Unknown, "Corp out of maturity band");

        return (BondBucket.C_Corp_AA, "Corp (passed rating gate)");
    }

    private static bool IsOfz(Bond b)
    {
        var name = b.Name ?? "";
        var ticker = b.Ticker ?? "";
        if (ticker.StartsWith("SU", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Contains("ОФЗ", StringComparison.OrdinalIgnoreCase) || name.Contains("OFZ", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool IsOfzInflationLinked(Bond b)
    {
        var name = b.Name ?? "";
        return name.Contains("ОФЗ-ИН", StringComparison.OrdinalIgnoreCase)
            || name.Contains("ОФЗИН", StringComparison.OrdinalIgnoreCase)
            || name.Contains("ИНФЛ", StringComparison.OrdinalIgnoreCase)
            || name.Contains("OFZ-IN", StringComparison.OrdinalIgnoreCase);
    }

    private static string IssuerKeyFromName(string? name)
    {
        // У T-Invest нет единого поля "эмитент" для всех нужных кейсов, поэтому ключ
        // эмитента извлекается из названия облигации и нормализуется под issuer_ratings.json.
        if (string.IsNullOrWhiteSpace(name)) return "";
        var s = name.Trim();

        int idx = s.IndexOfAny("0123456789".ToCharArray());
        if (idx > 1) s = s.Substring(0, idx).Trim();

        s = s.Replace("БО", " ")
             .Replace("-", " ")
             .Replace("  ", " ");
        while (s.Contains("  ")) s = s.Replace("  ", " ");

        return RatingsDb.NormalizeIssuer(s);
    }

    private static DateTime? GetEffectiveMaturityUtc(Bond b)
    {
        // Если у облигации есть call date раньше maturity, используем его как эффективный
        // горизонт возврата номинала: для инвестора это более консервативная дата.
        DateTime? maturity = b.MaturityDate != null ? b.MaturityDate.ToDateTime() : (DateTime?)null;
        DateTime? call = b.CallDate != null ? b.CallDate.ToDateTime() : (DateTime?)null;

        if (maturity == null && call == null) return null;
        if (maturity == null) return call;
        if (call == null) return maturity;

        return call.Value.Date < maturity.Value.Date ? call : maturity;
    }

        // Score измеряет не абсолютную доходность, а защитную привлекательность выпуска:
        // excess к ключевой ставке минус proxy-спред и лишняя дюрация, затем поправка на ликвидность.
        // Для ОФЗ-ИН это помогает не гнаться за доходностью в ущерб масштабируемости и цене входа.
    private static double ScoreBond(
        BondBucket bucket,
        double forward1YReturn,
        double keyRatePct,
        double spreadPct,
        double yearsToMaturity,
        double avgDailyTurnoverRub,
        StrategyConfig cfg)
    {
        if (!double.IsFinite(forward1YReturn) || yearsToMaturity <= 0) return double.NaN;

        double keyRate = keyRatePct / 100.0;
        double spread = spreadPct / 100.0;

        double excess = forward1YReturn - keyRate;

        // Optional cap for short, to prevent domination
        if (bucket == BondBucket.D_Short)
            excess = Math.Min(excess, cfg.ShortExcessCap);

        double tTarget = bucket switch
        {
            BondBucket.A_OFZ_Fixed => 3.5,
            BondBucket.B_OFZ_Infl => 4.5,
            BondBucket.C_Corp_AA => 2.0,
            BondBucket.D_Short => 0.5,
            _ => 3.0
        };

        double termPenaltyYears = Math.Max(0.0, yearsToMaturity - tTarget);
        double termPenalty = termPenaltyYears / 10.0;

        double core =
            cfg.WExcess * excess
            - cfg.WSpread * spread
            - cfg.WTerm * termPenalty;

        // Liquidity multiplier from turnover (log-scale)
        double liq = Math.Log10(1.0 + Math.Max(0.0, avgDailyTurnoverRub) / Math.Max(1.0, cfg.LiquidityLogScaleMillions));
        // bring typical range closer to ~[0.9..1.2]
        // log10(1+5)=0.78 -> +? We map linearly around 1.0 baseline.
        liq = 0.90 + 0.35 * liq; // tunable mapping
        liq = Math.Clamp(liq, cfg.LiquidityClampMin, cfg.LiquidityClampMax);

        return core * liq;
    }
}

// =======================
// Portfolio allocation + Expected yield
// =======================

public sealed class PortfolioAllocator
{
    public sealed record AllocationResult(
        List<AllocatedBond> Bonds,
        double MoneyMarketWeight,            // 0..1
        double ExpectedYieldAnnualDecimal,   // 0.17 = 17% годовых
        double MoneyMarketYieldAnnualDecimal // 0.16 = 16%
    );

    public static AllocationResult AllocateBestPositive(
        List<TaggedBond> candidates,
        StrategyConfig cfg,
        double keyRatePct)
    {
        // Аллокация специально консервативная: если ликвидных и привлекательных ОФЗ-ИН мало
        // или средний Score слабый, общий вес облигаций снижается, а остаток остается в Money Market.
        if (candidates == null) throw new ArgumentNullException(nameof(candidates));

        // Money market yield
        double mmYieldPct = cfg.MoneyMarketYieldPctOverride ?? keyRatePct;
        double mmYield = mmYieldPct / 100.0;

        // 1) Keep only positive score
        var pos = candidates.Where(b => double.IsFinite(b.Score) && b.Score > 0).ToList();
        if (pos.Count == 0)
        {
            // 100% MM
            return new AllocationResult(new List<AllocatedBond>(), 1.0, mmYield, mmYield);
        }

        // 2) Compute overall bond allocation factor (how much of portfolio is in bonds vs MM)
        int nPos = pos.Count;
        double avgScore = pos.Average(x => x.Score);

        double nFactor = Math.Clamp(nPos / (double)Math.Max(1, cfg.NTargetForFullBondAllocation), 0.0, 1.0);
        double qFactor = Math.Clamp(avgScore / Math.Max(1e-9, cfg.ScoreTargetForFullBondAllocation), 0.0, 1.0);

        double bondAllocation = Math.Clamp(nFactor * qFactor, 0.0, 1.0);

        // 3) Select best top-N per bucket, enforce max-per-issuer
        var selectedByBucket = new Dictionary<BondBucket, List<TaggedBond>>();
        foreach (var bucket in cfg.BucketTargets.Keys)
        {
            var list = pos.Where(x => x.Bucket == bucket)
                          .OrderByDescending(x => x.Score)
                          .ThenByDescending(x => x.Forward1YReturn)
                          .ThenBy(x => x.SpreadPct)
                          .ToList();

            var trimmed = TrimByIssuerAndTopN(list, cfg.TopNPerBucket, cfg.MaxPerIssuerInBucket);
            selectedByBucket[bucket] = trimmed;
        }

        // 4) Bucket weights = bondAllocation * target * fillFactor
        var bucketWeights = new Dictionary<BondBucket, double>();
        double sumBucketWeights = 0.0;

        foreach (var kv in cfg.BucketTargets)
        {
            var bucket = kv.Key;
            var target = kv.Value;

            var count = selectedByBucket.TryGetValue(bucket, out var lst) ? lst.Count : 0;
            double fill = Math.Clamp(count / (double)Math.Max(1, cfg.MinCountForFullBucket), 0.0, 1.0);

            double w = bondAllocation * target * fill;
            bucketWeights[bucket] = w;
            sumBucketWeights += w;
        }

        // 5) Allocate within each bucket weights ∝ score
        var allocated = new List<AllocatedBond>();

        foreach (var bucket in cfg.BucketTargets.Keys)
        {
            if (!selectedByBucket.TryGetValue(bucket, out var lst) || lst.Count == 0)
                continue;

            double bw = bucketWeights.TryGetValue(bucket, out var w) ? w : 0.0;
            if (bw <= 0) continue;

            double sumScore = lst.Sum(x => Math.Max(0.0, x.Score));
            if (sumScore <= 0) continue;

            foreach (var b in lst)
            {
                double wi = bw * Math.Max(0.0, b.Score) / sumScore;
                allocated.Add(new AllocatedBond(b, wi, bw));
            }
        }

        // 6) Remaining weight -> MM
        double bondsWeight = allocated.Sum(x => x.PortfolioWeight);
        double mmWeight = Math.Clamp(1.0 - bondsWeight, 0.0, 1.0);

        // ExpectedYield = Σ(w_bond * Forward1YReturn) + mmWeight * mmYield
        double expectedYield = allocated.Sum(x => x.PortfolioWeight * x.Bond.Forward1YReturn) + mmWeight * mmYield;

        return new AllocationResult(allocated, mmWeight, expectedYield, mmYield);
    }

    private static List<TaggedBond> TrimByIssuerAndTopN(List<TaggedBond> list, int topN, int maxPerIssuer)
    {
        var result = new List<TaggedBond>(capacity: Math.Min(topN, list.Count));
        var issuerCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var b in list)
        {
            if (!issuerCounts.TryGetValue(b.IssuerKey, out var cnt)) cnt = 0;
            if (cnt >= maxPerIssuer) continue;

            result.Add(b);
            issuerCounts[b.IssuerKey] = cnt + 1;

            if (result.Count >= topN) break;
        }

        return result;
    }
}
public static class WindowsCredentialManager
{
    private const int CredTypeGeneric = 1;
    private const int PersistLocalMachine = 2;

    public static string? ReadSecret(string targetName)
    {
        // Секрет хранится в Windows Credential Manager как Generic Credential.
        // Приложение получает только значение по имени targetName и не пишет токен на диск.
        if (string.IsNullOrWhiteSpace(targetName))
            return null;

        if (!CredRead(targetName, CredTypeGeneric, 0, out var credentialPtr))
            return null;

        try
        {
            var credential = Marshal.PtrToStructure<CREDENTIAL>(credentialPtr);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize <= 0)
                return null;

            var blob = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, blob, 0, blob.Length);
            return Encoding.Unicode.GetString(blob).TrimEnd('\0');
        }
        finally
        {
            CredFree(credentialPtr);
        }
    }

    public static bool WriteSecret(string targetName, string secret)
    {
        if (string.IsNullOrWhiteSpace(targetName))
            return false;

        var secretBytes = Encoding.Unicode.GetBytes(secret ?? string.Empty);
        var credential = new CREDENTIAL
        {
            Type = CredTypeGeneric,
            TargetName = targetName,
            CredentialBlobSize = (uint)secretBytes.Length,
            Persist = PersistLocalMachine,
            AttributeCount = 0,
            Attributes = IntPtr.Zero,
            Comment = null,
            TargetAlias = null,
            UserName = targetName
        };

        var blobHandle = GCHandle.Alloc(secretBytes, GCHandleType.Pinned);
        try
        {
            credential.CredentialBlob = blobHandle.AddrOfPinnedObject();
            return CredWrite(ref credential, 0);
        }
        finally
        {
            blobHandle.Free();
        }
    }

    [DllImport("Advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPtr);

    [DllImport("Advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite([In] ref CREDENTIAL userCredential, [In] uint flags);

    [DllImport("Advapi32.dll", SetLastError = true)]
    private static extern void CredFree([In] IntPtr cred);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }
}
