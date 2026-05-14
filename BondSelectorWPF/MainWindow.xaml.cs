// FILE: MainWindow.xaml.cs
// WPF .NET 8
// NuGet: Tinkoff.InvestApi, HtmlAgilityPack

using HtmlAgilityPack;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using Tinkoff.InvestApi;

namespace BondSelectorWpf;

/// <summary>
/// Главное окно приложения. Здесь собран сценарий анализа защитного слоя ОФЗ-ИН:
/// прочитать настройки из формы, загрузить рыночные данные, построить список
/// подходящих инфляционных облигаций и показать расчетные веса в таблице.
/// </summary>
public partial class MainWindow : Window
{
    public ObservableCollection<RowVm> Rows { get; } = new();

    // Пример локальной базы рейтингов оставлен для общего облигационного каркаса.
    // Для фокуса на ОФЗ-ИН главный фильтр не рейтинг, а тип выпуска, срок и ликвидность.
    private const string ExampleRatingsJson =
@"{
  ""as_of"": ""2026-01-15"",
  ""scale"": ""RU_NATIONAL"",
  ""min_rating"": ""AA-"",
  ""issuers"": {
    ""ГАЗПРОМ"": { ""rating"": ""AAA"", ""agency"": ""ACRA"" },
    ""РЖД"":     { ""rating"": ""AAA"", ""agency"": ""ACRA"" },
    ""СБЕРБАНК"": { ""rating"": ""AA+"", ""agency"": ""ACRA"" },
    ""ВТБ"":     { ""rating"": ""AA"", ""agency"": ""ExpertRA"" }
  }
}";

    public MainWindow()
    {
        InitializeComponent();
        BondsGrid.ItemsSource = Rows;
        StatusText.Text = "Ready.";
        TokenBox.Text = "InvestTestAccount";
    }

    private void PasteToken_Click(object sender, RoutedEventArgs e)
    {
        if (Clipboard.ContainsText())
            TokenBox.Text = Clipboard.GetText().Trim();
    }

    private void BrowseRatings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            FileName = "issuer_ratings.json"
        };
        if (dlg.ShowDialog() == true)
            RatingsPathBox.Text = dlg.FileName;
    }

    private void ShowExample_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(ExampleRatingsJson, "Example issuer_ratings.json", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        RunButton.IsEnabled = false;
        Rows.Clear();

        try
        {
            // В поле вводится не сам токен, а имя секрета в Windows Credential Manager.
            // Это позволяет не хранить API-токен в коде, настройках или JSON-файлах проекта.
            var token = WindowsCredentialManager.ReadSecret(TokenBox.Text.Trim())??"key not found";
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException("Token is empty.");

            var ratingsPath = RatingsPathBox.Text.Trim();
            if (!File.Exists(ratingsPath))
                throw new FileNotFoundException($"Ratings file not found: {ratingsPath}");

            var minRating = (MinRatingCombo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? "AA+";

            if (!double.TryParse(MaxSpreadBox.Text.Trim().Replace(',', '.'),
                    NumberStyles.Any, CultureInfo.InvariantCulture, out var maxSpreadPct))
                throw new InvalidOperationException("Max spread proxy is not a valid number.");

            if (!double.TryParse(MinTurnoverBox.Text.Trim().Replace(',', '.'),
                    NumberStyles.Any, CultureInfo.InvariantCulture, out var minTurnoverRub))
                throw new InvalidOperationException("Min turnover is not a valid number.");

            if (!decimal.TryParse(MaxNominalBox.Text.Trim().Replace(',', '.'),
                    NumberStyles.Any, CultureInfo.InvariantCulture, out var maxNominalRub))
                throw new InvalidOperationException("Max nominal is not a valid number.");

            // Рейтинговая база нужна для совместимости с более широким отбором облигаций.
            // В стратегии ОФЗ-ИН кредитный риск эмитента минимален, поэтому важнее ликвидность и срок.
            StatusText.Text = "Loading ratings DB...";
            await Dispatcher.Yield(DispatcherPriority.Background);

            var ratingsDb = RatingsDb.Load(ratingsPath);

            // Ключевая ставка нужна как базовая доходность: с ней сравнивается 1Y forward return,
            // а остаток портфеля, не занятый ОФЗ-ИН, считается размещенным в Money Market.
            StatusText.Text = "Loading CBR key rate series...";
            await Dispatcher.Yield(DispatcherPriority.Background);

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
            var to = DateTime.UtcNow.Date;
            var from = to.AddDays(-700);

            var series = await GetCbrKeyRateSeriesAsync(http, from, to, CancellationToken.None);
            if (series.Count == 0)
                throw new InvalidOperationException("CBR key rate series is empty (CBR site parsing failed).");

            var keyRatePct = series.Last().rate;

            StatusText.Text = $"CBR key rate: {keyRatePct:F2}% | Building candidates...";
            await Dispatcher.Yield(DispatcherPriority.Background);

            // StrategyConfig фиксирует расчетную модель: диапазоны сроков, требования к
            // ликвидности/спреду, целевые веса защитного слоя и параметры Score.
            var cfg = new StrategyConfig
            {
                RatingsDb = ratingsDb,
                MinCorpRating = minRating,
                Currency = "rub",

                // candle-based
                MaxSpreadPct = maxSpreadPct,
                MinAvgDailyTurnoverRub = minTurnoverRub,

                // retail feasibility
                MaxNominalAllowed = maxNominalRub,

                TopNPerBucket = 10,
                MaxPerIssuerInBucket = 5,

                BucketTargets = new()
                {
                    [BondBucket.A_OFZ_Fixed] = 0.35,
                    [BondBucket.B_OFZ_Infl] = 0.15,
                    [BondBucket.C_Corp_AA] = 0.35,
                    [BondBucket.D_Short] = 0.15
                },

                MinCountForFullBucket = 5,
                ScoreTargetForFullBondAllocation = 0.01,
                NTargetForFullBondAllocation = 12,

                // MM yield (null => keyRatePct)
                MoneyMarketYieldPctOverride = null
            };

            var client = InvestApiClientFactory.Create(token);

            // BondUniverseBuilder проходит по рынку облигаций, выделяет подходящие ОФЗ-ИН
            // в рамках общего каркаса и считает доходность, ликвидность и Score.
            StatusText.Text = "Loading bonds, computing candle-liquidity/spread, YTM, Forward1Y, Score...";
            await Dispatcher.Yield(DispatcherPriority.Background);

            var builder = new BondUniverseBuilder(client, cfg);
            var candidates = await builder.BuildCandidatesAsync(keyRatePct, CancellationToken.None);

            // Аллокатор берет только бумаги с положительным Score. Если ликвидных выпусков
            // ОФЗ-ИН мало, часть веса остается в Money Market, что отражает слабую масштабируемость.
            StatusText.Text = "Allocating portfolio (best positive Score) + money market...";
            await Dispatcher.Yield(DispatcherPriority.Background);

            var alloc = PortfolioAllocator.AllocateBestPositive(candidates, cfg, keyRatePct);

            foreach (var x in alloc.Bonds)
                Rows.Add(new RowVm(x, keyRatePct));

            double mmPct = alloc.MoneyMarketWeight * 100.0;
            double expYieldPct = alloc.ExpectedYieldAnnualDecimal * 100.0;
            double expExcessPct = expYieldPct - keyRatePct;

            StatusText.Text =
                $"Done. Selected bonds: {alloc.Bonds.Count}. " +
                $"Money Market: {mmPct:F1}%. " +
                $"Expected Yield (1Y fwd): {expYieldPct:F2}% (Excess vs Key: {expExcessPct:F2}%). " +
                $"Key: {keyRatePct:F2}%.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "Error: " + ex.Message;
        }
        finally
        {
            RunButton.IsEnabled = true;
        }
    }

    private static async Task<List<(DateTime date, double rate)>> GetCbrKeyRateSeriesAsync(
        HttpClient http,
        DateTime from,
        DateTime to,
        CancellationToken ct)
    {
        // На стороне ЦБ это HTML-таблица, поэтому данные читаются через HtmlAgilityPack.
        // Возвращаем уже очищенный и отсортированный ряд дат/ставок.
        string url =
            "https://www.cbr.ru/hd_base/KeyRate/?" +
            "UniDbQuery.Posted=True" +
            $"&UniDbQuery.From={from:dd.MM.yyyy}" +
            $"&UniDbQuery.To={to:dd.MM.yyyy}";

        var html = await http.GetStringAsync(url, ct);

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var rows = doc.DocumentNode.SelectNodes("//table//tr");
        if (rows is null) return new();

        var result = new List<(DateTime, double)>();

        foreach (var tr in rows)
        {
            var tds = tr.SelectNodes("./td");
            if (tds is null || tds.Count < 2) continue;

            var dateText = HtmlEntity.DeEntitize(tds[0].InnerText).Trim();
            var rateText = HtmlEntity.DeEntitize(tds[1].InnerText).Trim();

            if (!DateTime.TryParse(dateText, out var dt)) continue;

            rateText = rateText.Replace(',', '.');
            if (!double.TryParse(rateText, NumberStyles.Any, CultureInfo.InvariantCulture, out var rate))
                continue;

            result.Add((dt.Date, rate));
        }

        return result
            .GroupBy(x => x.Item1)
            .Select(g => g.First())
            .OrderBy(x => x.Item1)
            .ToList();
    }
}

/// <summary>
/// Плоская модель строки для DataGrid. Она форматирует доменные числа в строки,
/// чтобы UI не знал о правилах округления и fallback-значениях вроде "n/a".
/// </summary>
public sealed class RowVm
{
    public string Bucket { get; }
    public string WeightPct { get; }
    public string BucketWeightPct { get; }

    public string Score { get; }

    public string Forward1YPct { get; }
    public string ForwardExcessPct { get; }

    public string YtmPct { get; }
    public string KeyRatePct { get; }

    public string SpreadPct { get; }
    public string AvgTurnoverMln { get; }

    public string YearsToMaturity { get; }
    public DateTime EffectiveMaturityDateUtc { get; }

    public string Ticker { get; }
    public string Isin { get; }
    public string IssuerKey { get; }
    public string Name { get; }
    public string Reason { get; }

    public RowVm(AllocatedBond a, double keyRatePct)
    {
        var b = a.Bond;

        Bucket = b.Bucket.ToString();
        WeightPct = (a.PortfolioWeight * 100.0).ToString("F2", CultureInfo.InvariantCulture);
        BucketWeightPct = (a.BucketWeight * 100.0).ToString("F2", CultureInfo.InvariantCulture);

        Score = b.Score.ToString("F6", CultureInfo.InvariantCulture);

        KeyRatePct = keyRatePct.ToString("F2", CultureInfo.InvariantCulture);

        // Forward 1Y
        if (double.IsFinite(b.Forward1YReturn))
        {
            double fwdPct = b.Forward1YReturn * 100.0;
            Forward1YPct = fwdPct.ToString("F2", CultureInfo.InvariantCulture);

            double fwdExcess = fwdPct - keyRatePct;
            ForwardExcessPct = fwdExcess.ToString("F2", CultureInfo.InvariantCulture);
        }
        else
        {
            Forward1YPct = "n/a";
            ForwardExcessPct = "n/a";
        }

        // YTM (still shown for reference)
        YtmPct = double.IsFinite(b.YtmAnnual)
            ? (b.YtmAnnual * 100.0).ToString("F2", CultureInfo.InvariantCulture)
            : "n/a";

        SpreadPct = double.IsFinite(b.SpreadPct)
            ? b.SpreadPct.ToString("F2", CultureInfo.InvariantCulture)
            : "n/a";

        AvgTurnoverMln = double.IsFinite(b.AvgDailyTurnoverRub)
            ? (b.AvgDailyTurnoverRub / 1_000_000.0).ToString("F2", CultureInfo.InvariantCulture)
            : "n/a";

        YearsToMaturity = b.YearsToMaturity.ToString("F2", CultureInfo.InvariantCulture);
        EffectiveMaturityDateUtc = b.EffectiveMaturityDateUtc;

        Ticker = b.Ticker;
        Isin = b.Isin;
        IssuerKey = b.IssuerKey;
        Name = b.Name;
        Reason = b.TagReason;
    }
}
