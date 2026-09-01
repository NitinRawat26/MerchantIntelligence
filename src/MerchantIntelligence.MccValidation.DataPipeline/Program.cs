using System.Text.Json;
using MerchantIntelligence.MccValidation.Classification;
using MerchantIntelligence.MccValidation.Edgar;
using MerchantIntelligence.MccValidation.Taxonomy;
using MerchantIntelligence.MccValidation.Validation;
using MerchantIntelligence.MccValidation.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// Usage:
//   download [--limit N] [--user-agent "Name email"] [--out data/edgar] [--no-websites]
//   train    [--data data/edgar/training.jsonl] [--out models/mcc-classifier.zip]
//   all      (download then train)

var command = args.Length > 0 ? args[0] : "all";
var opts = ParseOptions(args.Skip(1));

var outDir = opts.GetValueOrDefault("--out-dir", Path.Combine("data", "edgar"));
var trainingPath = opts.GetValueOrDefault("--data", Path.Combine(outDir, "training.jsonl"));
var indexPath = opts.GetValueOrDefault("--index-out", Path.Combine("models", "filer-domains.json"));
var modelPath = opts.GetValueOrDefault("--out", Path.Combine("models", "mcc-classifier.zip"));
var limit = int.Parse(opts.GetValueOrDefault("--limit", "2000"));
var userAgent = opts.GetValueOrDefault("--user-agent",
    Environment.GetEnvironmentVariable("EDGAR_USER_AGENT") ?? "MerchantIntelligence research nitinrawat260887@gmail.com");
var fetchWebsites = !opts.ContainsKey("--no-websites");

using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Information));
var log = loggerFactory.CreateLogger("Pipeline");

if (command is "download" or "all")
    await DownloadAsync();
if (command is "train" or "all")
    Train();

async Task DownloadAsync()
{
    Directory.CreateDirectory(outDir);
    var catalog = MccCatalog.Default;

    var services = new ServiceCollection();
    services.AddLogging(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning));
    services.AddHttpClient(WebsiteContentFetcher.HttpClientName, c =>
    {
        c.Timeout = TimeSpan.FromSeconds(15);
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; MerchantIntelligenceBot/1.0)");
    });
    services.AddSingleton<WebsiteContentFetcher>();
    await using var sp = services.BuildServiceProvider();
    var fetcher = sp.GetRequiredService<WebsiteContentFetcher>();

    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    var edgar = new EdgarClient(http,
        new EdgarClientOptions { UserAgent = userAgent, CacheDirectory = Path.Combine(outDir, "cache") },
        loggerFactory.CreateLogger<EdgarClient>());

    var companies = await edgar.GetCompaniesAsync();
    log.LogInformation("EDGAR lists {Count} companies; processing up to {Limit}", companies.Count, limit);

    // Deterministic shuffle so a limited run still samples across industries rather than by market cap.
    var rng = new Random(42);
    var sample = companies.OrderBy(_ => rng.Next()).Take(limit).ToList();

    var records = new List<MccTrainingRecord>();
    var filers = new List<EdgarFilerSummary>();
    var perMcc = new Dictionary<int, int>();
    var processed = 0;

    foreach (var company in sample)
    {
        processed++;
        var submission = await edgar.GetSubmissionAsync(company.Cik);
        if (submission?.Sic is null) continue;

        var mcc = catalog.MapSicToMcc(submission.Sic.Value);
        if (mcc is null) continue;

        Uri? url = submission.Website is not null && Uri.TryCreate(submission.Website, UriKind.Absolute, out var declared) ? declared : null;

        var filing = submission.Latest10K;
        if (filing is not null)
        {
            var business = await edgar.GetBusinessDescriptionAsync(company.Cik, filing);
            if (business is not null)
            {
                records.Add(new MccTrainingRecord { Text = business, Mcc = mcc.Value.ToString(), Source = $"edgar:{company.Cik}:{filing.AccessionNumber}" });
                perMcc[mcc.Value] = perMcc.GetValueOrDefault(mcc.Value) + 1;
            }
            url ??= await edgar.GetWebsiteAsync(company.Cik, filing);
        }

        if (url is not null)
            filers.Add(new EdgarFilerSummary(submission.Cik, submission.Name, submission.Sic, submission.SicDescription, url.Host));

        if (fetchWebsites && url is not null)
        {
            try
            {
                var content = await fetcher.FetchAsync(url, maxPages: 2);
                if (!content.IsEmpty && content.CombinedText.Length > 300)
                    records.Add(new MccTrainingRecord { Text = content.CombinedText, Mcc = mcc.Value.ToString(), Source = $"web:{url.Host}" });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogDebug(ex, "Website fetch failed for {Url}", url);
            }
        }

        if (processed % 100 == 0)
            log.LogInformation("{Processed}/{Total} companies, {Records} records, {Classes} MCC classes", processed, sample.Count, records.Count, perMcc.Count);
    }

    // Catalog descriptions + keywords as a weak prior so long-tail merchant MCCs (restaurants,
    // salons...) exist as classes even when EDGAR has no filers for them.
    foreach (var entry in catalog.Entries)
    {
        var text = $"{entry.Description}. {entry.Category}. {string.Join(". ", entry.Keywords)}. {string.Join(" ", entry.Keywords)}";
        for (var i = 0; i < 3; i++)
            records.Add(new MccTrainingRecord { Text = text, Mcc = entry.Mcc.ToString(), Source = "catalog" });
    }

    await using (var writer = new StreamWriter(trainingPath))
    {
        foreach (var r in records)
            await writer.WriteLineAsync(JsonSerializer.Serialize(r));
    }
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(indexPath))!);
    await File.WriteAllTextAsync(indexPath, JsonSerializer.Serialize(filers, new JsonSerializerOptions { WriteIndented = false }));

    log.LogInformation("Wrote {Records} training records ({Edgar} EDGAR, {Web} website, {Catalog} catalog) to {Path}",
        records.Count, records.Count(r => r.Source.StartsWith("edgar")), records.Count(r => r.Source.StartsWith("web")),
        records.Count(r => r.Source == "catalog"), trainingPath);
    log.LogInformation("Wrote {Count} filer domains to {Path}", filers.Count, indexPath);
    foreach (var kv in perMcc.OrderByDescending(kv => kv.Value).Take(15))
        log.LogInformation("  MCC {Mcc} {Desc}: {Count} filings", kv.Key, catalog.Describe(kv.Key), kv.Value);
}

void Train()
{
    var records = File.ReadLines(trainingPath)
        .Where(l => !string.IsNullOrWhiteSpace(l))
        .Select(l => JsonSerializer.Deserialize<MccTrainingRecord>(l)!)
        .ToList();

    // Drop classes with too few examples to hold out.
    var counts = records.GroupBy(r => r.Mcc).ToDictionary(g => g.Key, g => g.Count());
    records = records.Where(r => counts[r.Mcc] >= 3).ToList();

    log.LogInformation("Training on {Count} records across {Classes} MCC classes", records.Count, records.Select(r => r.Mcc).Distinct().Count());
    var (classifier, metrics) = MccTextClassifier.Train(records);
    log.LogInformation("Held-out: micro-accuracy {Micro:P2}, macro-accuracy {Macro:P2}, top-3 {Top3:P2}, log-loss {LogLoss:F3} ({Rows} rows, {Classes} classes)",
        metrics.MicroAccuracy, metrics.MacroAccuracy, metrics.Top3Accuracy, metrics.LogLoss, metrics.TestRows, metrics.ClassCount);

    classifier.Save(modelPath);
    log.LogInformation("Saved model to {Path}", modelPath);

    var reloaded = MccTextClassifier.Load(modelPath);
    var demo = reloaded.Predict("Family owned Italian restaurant serving pizza, pasta and wine. View our menu and make a reservation.");
    log.LogInformation("Smoke test → {Mcc} ({P:P0})", demo.Top.Mcc, demo.Top.Probability);
}

static Dictionary<string, string> ParseOptions(IEnumerable<string> args)
{
    var dict = new Dictionary<string, string>();
    var list = args.ToList();
    for (var i = 0; i < list.Count; i++)
    {
        if (!list[i].StartsWith("--")) continue;
        var hasValue = i + 1 < list.Count && !list[i + 1].StartsWith("--");
        dict[list[i]] = hasValue ? list[++i] : "true";
    }
    return dict;
}
