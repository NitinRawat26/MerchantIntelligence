using System.Net.Http.Json;
using System.Text.Json;
using MerchantIntelligence.MccValidation.Taxonomy;
using Microsoft.Extensions.Logging;

namespace MerchantIntelligence.Kyb.Sanctions;

public sealed class SanctionsOptions
{
    public const string HttpClientName = "SanctionsLists";

    public string CacheDirectory { get; set; } = Path.Combine("data", "sanctions");

    /// <summary>How long a downloaded list is reused before being refreshed.</summary>
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Minimum fuzzy score for a hit to be reported.</summary>
    public double MatchThreshold { get; set; } = 0.85;

    /// <summary>Also load the OpenSanctions PEP dataset (~180 MB download, several hundred MB RAM).</summary>
    public bool IncludePeps { get; set; } = false;

    /// <summary>Also download the raw OFAC SDN and UN XML lists in addition to the OpenSanctions consolidation.</summary>
    public bool IncludeRawGovernmentLists { get; set; } = true;

    /// <summary>Run GDELT adverse-media search for each subject.</summary>
    public bool EnableAdverseMedia { get; set; } = true;
}

/// <summary>Downloads (with disk cache), parses and indexes the configured lists; screens subjects against them.</summary>
public sealed class SanctionsScreeningService
{
    private readonly IHttpClientFactory _factory;
    private readonly SanctionsOptions _options;
    private readonly IReadOnlyList<ISanctionsListSource> _sources;
    private readonly IAdverseMediaProvider? _adverseMedia;
    private readonly ILogger<SanctionsScreeningService> _logger;
    private readonly SemaphoreSlim _loadLock = new(1, 1);

    private SanctionsIndex? _index;
    private List<SanctionsListStatus> _statuses = new();
    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;

    public SanctionsScreeningService(
        IHttpClientFactory factory,
        SanctionsOptions options,
        IEnumerable<ISanctionsListSource> sources,
        ILogger<SanctionsScreeningService> logger,
        IAdverseMediaProvider? adverseMedia = null)
    {
        _factory = factory;
        _options = options;
        _sources = sources.ToList();
        _adverseMedia = adverseMedia;
        _logger = logger;
    }

    public IReadOnlyList<SanctionsListStatus> ListStatuses => _statuses;

    public async Task<ScreeningReport> ScreenAsync(IReadOnlyList<ScreeningSubject> subjects, CancellationToken ct = default)
    {
        var index = await GetIndexAsync(ct);
        var results = new List<SubjectScreeningResult>();
        foreach (var subject in subjects)
        {
            var hits = index.Search(subject, _options.MatchThreshold);
            AdverseMediaResult? media = null;
            if (_options.EnableAdverseMedia && _adverseMedia is not null)
            {
                try { media = await _adverseMedia.SearchAsync(subject, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Adverse media lookup failed for {Subject}", subject.Name);
                    media = new AdverseMediaResult(_adverseMedia.Name, false, 0, 0, Array.Empty<AdverseMediaArticle>(), ex.Message);
                }
            }

            var flags = new List<KybScreeningFlag>();
            if (hits.Count > 0)
            {
                var top = hits[0];
                // A corroborating identifier (birth year / nationality) turns a strong fuzzy name match into a confirmed one.
                var corroborated = top.Reasons.Any(r => r.Contains("matches listing", StringComparison.OrdinalIgnoreCase));
                var confirmed = top.Score >= 0.95 || (top.Score >= 0.9 && corroborated);
                var severity = confirmed ? RiskTier.High : RiskTier.Medium;
                flags.Add(new KybScreeningFlag(confirmed ? "SANCTIONS_MATCH" : "SANCTIONS_POSSIBLE_MATCH",
                    $"{subject.Name}: {hits.Count} potential hit(s); best '{top.MatchedName}' on {top.Entity.ListName} ({string.Join(", ", top.Entity.Programs.Take(3))}) score {top.Score:P0}.",
                    severity));
                if (hits.Any(h => h.Entity.ListName.Contains("peps", StringComparison.OrdinalIgnoreCase) || h.Entity.Programs.Any(p => p.Contains("PEP", StringComparison.OrdinalIgnoreCase))))
                    flags.Add(new KybScreeningFlag("PEP_MATCH", $"{subject.Name} matches a politically exposed person record.", RiskTier.Medium));
            }
            if (media is { Succeeded: true, NegativeCount: > 0 })
                flags.Add(new KybScreeningFlag("ADVERSE_MEDIA", $"{subject.Name}: {media.NegativeCount} negative-tone article(s) in the last 3 months mentioning fraud/laundering/etc.", media.NegativeCount >= 3 ? RiskTier.High : RiskTier.Medium));

            results.Add(new SubjectScreeningResult(subject, hits.Count > 0, hits, media, flags));
        }

        var allFlags = results.SelectMany(r => r.Flags).ToList();
        var overall = allFlags.Count == 0 ? RiskTier.Low : allFlags.Max(f => f.Severity);
        return new ScreeningReport(results, _statuses, overall, allFlags);
    }

    public async Task<SanctionsIndex> GetIndexAsync(CancellationToken ct)
    {
        if (_index is not null && DateTimeOffset.UtcNow - _loadedAt < _options.RefreshInterval) return _index;
        await _loadLock.WaitAsync(ct);
        try
        {
            if (_index is not null && DateTimeOffset.UtcNow - _loadedAt < _options.RefreshInterval) return _index;
            var index = new SanctionsIndex();
            var statuses = new List<SanctionsListStatus>();
            foreach (var source in _sources)
            {
                try
                {
                    var path = await EnsureDownloadedAsync(source, ct);
                    var before = index.Count;
                    await using var stream = File.OpenRead(path);
                    index.AddRange(source.Parse(stream));
                    statuses.Add(new SanctionsListStatus(source.ListName, index.Count - before, new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero), null));
                    _logger.LogInformation("Loaded {Count} entities from {List}", index.Count - before, source.ListName);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Failed to load sanctions list {List}", source.ListName);
                    statuses.Add(new SanctionsListStatus(source.ListName, 0, null, ex.Message));
                }
            }
            _index = index;
            _statuses = statuses;
            _loadedAt = DateTimeOffset.UtcNow;
            return index;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private async Task<string> EnsureDownloadedAsync(ISanctionsListSource source, CancellationToken ct)
    {
        Directory.CreateDirectory(_options.CacheDirectory);
        var path = Path.Combine(_options.CacheDirectory, source.CacheFileName);
        if (File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < _options.RefreshInterval && new FileInfo(path).Length > 0)
            return path;

        var client = _factory.CreateClient(SanctionsOptions.HttpClientName);
        _logger.LogInformation("Downloading {List} from {Url}", source.ListName, source.DownloadUrl);
        using var response = await client.GetAsync(source.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var tmp = path + ".tmp";
        await using (var fs = File.Create(tmp))
        {
            await response.Content.CopyToAsync(fs, ct);
        }
        File.Move(tmp, path, overwrite: true);
        return path;
    }
}

public interface IAdverseMediaProvider
{
    string Name { get; }
    Task<AdverseMediaResult> SearchAsync(ScreeningSubject subject, CancellationToken ct);
}

/// <summary>GDELT 2.0 DOC API: free global news index with tone scoring, no key required.</summary>
public sealed class GdeltAdverseMediaProvider : IAdverseMediaProvider
{
    private static readonly string[] RiskTerms =
    {
        "fraud", "laundering", "indicted", "indictment", "lawsuit", "scam", "embezzlement", "bribery",
        "sanctions", "arrested", "convicted", "ponzi", "chargeback", "counterfeit", "investigation"
    };

    private readonly IHttpClientFactory _factory;

    public GdeltAdverseMediaProvider(IHttpClientFactory factory) => _factory = factory;

    public string Name => "GDELT DOC 2.0";

    public async Task<AdverseMediaResult> SearchAsync(ScreeningSubject subject, CancellationToken ct)
    {
        var client = _factory.CreateClient(SanctionsOptions.HttpClientName);
        var query = $"\"{subject.Name}\" ({string.Join(" OR ", RiskTerms)})";
        var url = $"https://api.gdeltproject.org/api/v2/doc/doc?query={Uri.EscapeDataString(query)}&mode=artlist&format=json&maxrecords=25&timespan=3months&sort=hybridrel";
        using var response = await client.GetAsync(url, ct);
        if ((int)response.StatusCode == 429)
            return new AdverseMediaResult(Name, false, 0, 0, Array.Empty<AdverseMediaArticle>(), "GDELT rate limit reached; retry later.");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(body) || !body.TrimStart().StartsWith('{'))
            return new AdverseMediaResult(Name, true, 0, 0, Array.Empty<AdverseMediaArticle>());

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("articles", out var articles))
            return new AdverseMediaResult(Name, true, 0, 0, Array.Empty<AdverseMediaArticle>());

        var list = new List<AdverseMediaArticle>();
        foreach (var a in articles.EnumerateArray())
        {
            if (!Uri.TryCreate(a.GetProperty("url").GetString(), UriKind.Absolute, out var u)) continue;
            DateTimeOffset? published = a.TryGetProperty("seendate", out var sd)
                && DateTimeOffset.TryParseExact(sd.GetString(), "yyyyMMdd'T'HHmmss'Z'", null, System.Globalization.DateTimeStyles.AssumeUniversal, out var p) ? p : null;
            var title = a.TryGetProperty("title", out var t) ? t.GetString() ?? string.Empty : string.Empty;
            var tone = RiskTerms.Any(term => title.Contains(term, StringComparison.OrdinalIgnoreCase)) ? "negative" : "neutral";
            list.Add(new AdverseMediaArticle(title, u, a.TryGetProperty("domain", out var d) ? d.GetString() ?? u.Host : u.Host, published, tone));
        }
        return new AdverseMediaResult(Name, true, list.Count, list.Count(l => l.Tone == "negative"), list);
    }
}
