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

    /// <summary>Run adverse-media search (news, encyclopaedia and court records) for each subject.</summary>
    public bool EnableAdverseMedia { get; set; } = true;

    /// <summary>Free sources queried in parallel per subject: gdelt, googlenews, bingnews, wikipedia, courtlistener.</summary>
    public string[] AdverseMediaSources { get; set; } = ["gdelt", "googlenews", "bingnews", "wikipedia", "courtlistener"];

    public const int DefaultAdverseMediaSourceTimeoutSeconds = 12;

    /// <summary>Per-source deadline for one subject; a source that has not answered by then is reported as unavailable rather than holding the screening step.</summary>
    public int AdverseMediaSourceTimeoutSeconds { get; set; } = DefaultAdverseMediaSourceTimeoutSeconds;
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
        // Media lookups for all subjects start together; rate-limited sources serialise themselves internally.
        var mediaTasks = subjects.Select(s => SearchMediaAsync(s, ct)).ToList();
        for (var i = 0; i < subjects.Count; i++)
        {
            var subject = subjects[i];
            var hits = index.Search(subject, _options.MatchThreshold);
            var media = await mediaTasks[i];

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
            if (media is { Succeeded: true })
                flags.AddRange(AdverseMediaFlags(subject, media));

            results.Add(new SubjectScreeningResult(subject, hits.Count > 0, hits, media, flags));
        }

        var allFlags = results.SelectMany(r => r.Flags).ToList();
        var overall = allFlags.Count == 0 ? RiskTier.Low : allFlags.Max(f => f.Severity);
        return new ScreeningReport(results, _statuses, overall, allFlags);
    }

    private async Task<AdverseMediaResult?> SearchMediaAsync(ScreeningSubject subject, CancellationToken ct)
    {
        if (!_options.EnableAdverseMedia || _adverseMedia is null) return null;
        try { return await _adverseMedia.SearchAsync(subject, ct); }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Adverse media lookup failed for {Subject}", subject.Name);
            return new AdverseMediaResult(_adverseMedia.Name, false, 0, 0, Array.Empty<AdverseMediaArticle>(), ex.Message);
        }
    }

    /// <summary>
    /// Negative articles (subject and risk term in the same sentence/headline) raise ADVERSE_MEDIA with the matched terms
    /// and a quoted excerpt; risk terms elsewhere in an article naming the subject raise the softer ADVERSE_MEDIA_MENTION.
    /// </summary>
    internal static IEnumerable<KybScreeningFlag> AdverseMediaFlags(ScreeningSubject subject, AdverseMediaResult media)
    {
        var negatives = media.Articles.Where(a => a.Tone == "negative").ToList();
        if (negatives.Count > 0)
        {
            var terms = negatives.SelectMany(a => a.MatchedTerms ?? Array.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();
            var categories = negatives.Select(a => a.Category).Where(c => !string.IsNullOrEmpty(c)).Distinct().Take(4).ToList();
            var sources = negatives.Select(a => a.Provider ?? a.Source).Distinct().ToList();
            var lead = negatives[0];
            var excerpt = string.IsNullOrEmpty(lead.Context) ? lead.Title : lead.Context;
            yield return new KybScreeningFlag(
                "ADVERSE_MEDIA",
                $"{subject.Name}: {negatives.Count} article(s) tie the name to {string.Join(", ", terms)}" +
                (categories.Count > 0 ? $" [{string.Join("; ", categories)}]" : string.Empty) +
                $" across {string.Join(", ", sources)}. E.g. \"{excerpt}\" ({lead.Source}{(lead.Published is { } d ? $", {d:yyyy-MM-dd}" : string.Empty)}).",
                RiskTier.Medium);
        }

        var mentions = media.Articles.Where(a => a.Tone == "mention").ToList();
        if (mentions.Count > 0)
        {
            var terms = mentions.SelectMany(a => a.MatchedTerms ?? Array.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase).Take(6).ToList();
            yield return new KybScreeningFlag(
                "ADVERSE_MEDIA_MENTION",
                $"{subject.Name}: named in {mentions.Count} article(s)/record(s) that also discuss {string.Join(", ", terms)}, but not in the same sentence – analyst should confirm relevance. E.g. \"{mentions[0].Title}\" ({mentions[0].Source}).",
                RiskTier.Low);
        }
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
                catch (Exception ex) when (!ct.IsCancellationRequested)
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
