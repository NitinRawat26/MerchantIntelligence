using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace MerchantIntelligence.Kyb.Sanctions;

/// <summary>A single free news / records source. Sources return raw candidate articles; the analyser decides tone.</summary>
public interface IAdverseMediaSource
{
    string Name { get; }
    Task<IReadOnlyList<AdverseMediaArticle>> SearchAsync(ScreeningSubject subject, CancellationToken ct);
}

/// <summary>Risk vocabulary used to grade an article. Grouped so the explanation can say *what kind* of adverse coverage was found.</summary>
public static class AdverseMediaLexicon
{
    public static readonly IReadOnlyDictionary<string, string[]> Categories = new Dictionary<string, string[]>
    {
        ["Financial crime"] = ["fraud", "fraudulent", "money laundering", "laundering", "embezzlement", "embezzled", "ponzi", "scam", "bribery", "bribe", "kickback", "tax evasion", "wire fraud", "racketeering", "extortion", "forgery"],
        ["Criminal proceedings"] = ["indicted", "indictment", "arrested", "arrest", "convicted", "conviction", "charged with", "pleaded guilty", "guilty", "sentenced", "felony", "prison", "criminal charges"],
        ["Civil / litigation"] = ["lawsuit", "sued", "class action", "settlement", "judgment against", "bankruptcy", "insolvency", "receivership", "liquidation", "default judgment"],
        ["Regulatory"] = ["sanctions", "sanctioned", "fined", "penalty", "enforcement action", "investigation", "probe", "subpoena", "cease and desist", "consent order", "license revoked", "banned", "deregistered"],
        ["Payments / card risk"] = ["chargeback", "chargebacks", "counterfeit", "data breach", "skimming", "bust-out", "shell company", "transaction laundering", "terminated merchant"],
        ["Organised crime / terrorism"] = ["terrorism", "terrorist", "cartel", "trafficking", "organized crime", "organised crime", "smuggling"]
    };

    /// <summary>Compact list used to build search queries (the most discriminating single words).</summary>
    public static readonly string[] QueryTerms =
    [
        "fraud", "laundering", "indicted", "lawsuit", "scam", "embezzlement", "bribery", "sanctions", "arrested",
        "convicted", "ponzi", "chargeback", "counterfeit", "investigation", "fined", "bankruptcy"
    ];

    private static readonly (string Term, string Category)[] All = Categories
        .SelectMany(kv => kv.Value.Select(t => (t, kv.Key)))
        .OrderByDescending(x => x.t.Length)
        .ToArray();

    public static IReadOnlyList<(string Term, string Category)> FindTerms(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<(string, string)>();
        var found = new List<(string, string)>();
        foreach (var (term, category) in All)
        {
            if (Regex.IsMatch(text, $@"\b{Regex.Escape(term)}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                found.Add((term.ToLowerInvariant(), category));
        }
        return found;
    }
}

/// <summary>
/// Grades an article against a subject. An article is <c>negative</c> only when the subject and a risk term occur in the
/// same sentence (or the headline); a risk term elsewhere in an article that names the subject is a <c>mention</c>;
/// everything else is <c>neutral</c>. The co-located sentence is kept as <see cref="AdverseMediaArticle.Context"/>.
/// </summary>
public static class AdverseMediaAnalyzer
{
    private static readonly string[] LegalSuffixes =
    [
        "inc", "inc.", "llc", "l.l.c.", "ltd", "ltd.", "limited", "corp", "corp.", "corporation", "co", "co.", "company",
        "plc", "gmbh", "sa", "s.a.", "llp", "lp", "holdings", "group", "the", "and", "&", "of"
    ];

    private static readonly Regex Tags = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex SentenceSplit = new(@"(?<=[.!?])\s+(?=[A-Z""'(\[])|\r?\n+", RegexOptions.Compiled);

    public static string Clean(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;
        var text = WebUtility.HtmlDecode(Tags.Replace(html, " "));
        return Spaces.Replace(text, " ").Trim();
    }

    public static IReadOnlyList<string> NameTokens(string name)
    {
        var tokens = Clean(name).Split(new[] { ' ', ',', ';', '/' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim('"', '\'', '(', ')').ToLowerInvariant())
            .Where(t => t.Length > 1 && !LegalSuffixes.Contains(t))
            .ToList();
        return tokens.Count > 0 ? tokens : [Clean(name).ToLowerInvariant()];
    }

    public static bool MentionsSubject(string text, ScreeningSubject subject, IReadOnlyList<string> tokens)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var lower = text.ToLowerInvariant();
        var full = Clean(subject.Name).ToLowerInvariant();
        if (full.Length > 0 && lower.Contains(full)) return true;
        if (subject.IsIndividual)
        {
            // People: require first and last token together (middle names / initials are often dropped by the press).
            if (tokens.Count >= 2) return lower.Contains(tokens[0]) && lower.Contains(tokens[^1]);
            return lower.Contains(tokens[0]);
        }
        // Organisations: every significant token must appear.
        return tokens.All(lower.Contains);
    }

    public static AdverseMediaArticle Grade(ScreeningSubject subject, AdverseMediaArticle raw)
    {
        var tokens = NameTokens(subject.Name);
        var title = Clean(raw.Title);
        var snippet = Clean(raw.Snippet);
        var sentences = new List<string>();
        if (title.Length > 0) sentences.Add(title);
        if (snippet.Length > 0) sentences.AddRange(SentenceSplit.Split(snippet).Select(s => s.Trim()).Where(s => s.Length > 0));

        var subjectAnywhere = sentences.Any(s => MentionsSubject(s, subject, tokens));
        var terms = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var categories = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        string? context = null;
        var coLocated = false;

        foreach (var sentence in sentences)
        {
            var found = AdverseMediaLexicon.FindTerms(sentence);
            if (found.Count == 0) continue;
            foreach (var (term, category) in found) { terms.Add(term); categories.Add(category); }
            if (!coLocated && MentionsSubject(sentence, subject, tokens))
            {
                coLocated = true;
                context = Truncate(sentence, 260);
            }
        }

        var tone = coLocated ? "negative" : terms.Count > 0 && subjectAnywhere ? "mention" : "neutral";
        if (tone == "mention" && context is null)
            context = Truncate(sentences.FirstOrDefault(s => AdverseMediaLexicon.FindTerms(s).Count > 0) ?? title, 260);

        return raw with
        {
            Title = title.Length > 0 ? title : raw.Title,
            Tone = tone,
            Snippet = snippet.Length > 0 ? Truncate(snippet, 400) : null,
            MatchedTerms = terms.ToList(),
            Category = categories.Count > 0 ? string.Join(", ", categories) : null,
            Context = context
        };
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)].TrimEnd() + "…";
}

/// <summary>
/// Fans a subject out to every registered <see cref="IAdverseMediaSource"/> in parallel, grades and de-duplicates the
/// articles, and reports per-source status so a failed source shows as a gap without hiding the others.
/// </summary>
public sealed class CompositeAdverseMediaProvider : IAdverseMediaProvider
{
    private readonly IReadOnlyList<IAdverseMediaSource> _sources;
    private readonly ILogger<CompositeAdverseMediaProvider> _logger;

    public CompositeAdverseMediaProvider(IEnumerable<IAdverseMediaSource> sources, ILogger<CompositeAdverseMediaProvider> logger)
    {
        _sources = sources.ToList();
        _logger = logger;
    }

    public string Name => _sources.Count == 0 ? "none" : string.Join(" + ", _sources.Select(s => s.Name));

    public async Task<AdverseMediaResult> SearchAsync(ScreeningSubject subject, CancellationToken ct)
    {
        var tasks = _sources.Select(async source =>
        {
            try
            {
                var articles = await source.SearchAsync(subject, ct);
                return (source.Name, Articles: (IReadOnlyList<AdverseMediaArticle>)articles.Select(a => AdverseMediaAnalyzer.Grade(subject, a with { Provider = source.Name })).ToList(), Error: (string?)null);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Adverse media source {Source} failed for {Subject}", source.Name, subject.Name);
                return (source.Name, Articles: (IReadOnlyList<AdverseMediaArticle>)Array.Empty<AdverseMediaArticle>(), Error: ex.Message);
            }
        }).ToList();

        var outcomes = await Task.WhenAll(tasks);
        var statuses = outcomes.Select(o => new AdverseMediaProviderStatus(o.Name, o.Error is null, o.Articles.Count, o.Error)).ToList();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var graded = outcomes.SelectMany(o => o.Articles)
            .OrderBy(a => ToneRank(a.Tone)).ThenByDescending(a => a.Published ?? DateTimeOffset.MinValue)
            .Where(a => seen.Add(DedupKey(a)))
            .ToList();
        var negatives = graded.Count(a => a.Tone == "negative");
        var mentions = graded.Count(a => a.Tone == "mention");
        var articles = graded.Take(MaxArticles).ToList();

        var succeeded = statuses.Any(s => s.Succeeded);
        var failed = statuses.Where(s => !s.Succeeded).ToList();
        var error = failed.Count == 0 ? null
            : succeeded ? $"{failed.Count} of {statuses.Count} source(s) unavailable: {string.Join("; ", failed.Select(f => $"{f.Provider}: {f.Error}"))}"
            : $"All adverse-media sources failed: {string.Join("; ", failed.Select(f => $"{f.Provider}: {f.Error}"))}";

        return new AdverseMediaResult(Name, succeeded, graded.Count, negatives, articles, error, statuses, mentions);
    }

    /// <summary>Articles kept on the result (negatives first); counts still reflect everything that was graded.</summary>
    private const int MaxArticles = 40;

    private static int ToneRank(string tone) => tone switch { "negative" => 0, "mention" => 1, _ => 2 };

    private static string DedupKey(AdverseMediaArticle a)
    {
        var title = a.Title;
        var cut = title.LastIndexOf(" - ", StringComparison.Ordinal);
        if (cut > 20) title = title[..cut];
        title = Regex.Replace(title.ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim();
        return title.Length >= 24 ? title : a.Url.ToString().ToLowerInvariant();
    }
}

/// <summary>GDELT 2.0 DOC API: free global news index, no key. Enforces GDELT's one-request-per-5-seconds rule process-wide and retries once on 429.</summary>
public sealed class GdeltAdverseMediaSource : IAdverseMediaSource
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(5.2);
    private static DateTimeOffset _lastRequest = DateTimeOffset.MinValue;

    private readonly IHttpClientFactory _factory;

    public GdeltAdverseMediaSource(IHttpClientFactory factory) => _factory = factory;

    public string Name => "GDELT DOC 2.0";

    public async Task<IReadOnlyList<AdverseMediaArticle>> SearchAsync(ScreeningSubject subject, CancellationToken ct)
    {
        var client = _factory.CreateClient(SanctionsOptions.HttpClientName);
        var query = $"\"{subject.Name}\" ({string.Join(" OR ", AdverseMediaLexicon.QueryTerms)})";
        var url = $"https://api.gdeltproject.org/api/v2/doc/doc?query={Uri.EscapeDataString(query)}&mode=artlist&format=json&maxrecords=25&timespan=3months&sort=hybridrel";

        for (var attempt = 0; ; attempt++)
        {
            string body;
            HttpStatusCode status;
            await Gate.WaitAsync(ct);
            try
            {
                var wait = _lastRequest + MinInterval - DateTimeOffset.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
                using var response = await client.GetAsync(url, ct);
                _lastRequest = DateTimeOffset.UtcNow;
                status = response.StatusCode;
                body = status == HttpStatusCode.OK ? await response.Content.ReadAsStringAsync(ct) : string.Empty;
                if (status != HttpStatusCode.TooManyRequests) response.EnsureSuccessStatusCode();
            }
            finally
            {
                Gate.Release();
            }

            if (status == HttpStatusCode.TooManyRequests)
            {
                if (attempt == 0) continue;
                throw new HttpRequestException("GDELT rate limit reached after retry (one request per 5 seconds).");
            }

            if (string.IsNullOrWhiteSpace(body) || !body.TrimStart().StartsWith('{')) return Array.Empty<AdverseMediaArticle>();
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("articles", out var articles)) return Array.Empty<AdverseMediaArticle>();

            var list = new List<AdverseMediaArticle>();
            foreach (var a in articles.EnumerateArray())
            {
                if (!Uri.TryCreate(a.GetProperty("url").GetString(), UriKind.Absolute, out var u)) continue;
                DateTimeOffset? published = a.TryGetProperty("seendate", out var sd)
                    && DateTimeOffset.TryParseExact(sd.GetString(), "yyyyMMdd'T'HHmmss'Z'", null, System.Globalization.DateTimeStyles.AssumeUniversal, out var p) ? p : null;
                var title = a.TryGetProperty("title", out var t) ? t.GetString() ?? string.Empty : string.Empty;
                list.Add(new AdverseMediaArticle(title, u, a.TryGetProperty("domain", out var d) ? d.GetString() ?? u.Host : u.Host, published, "neutral"));
            }
            return list;
        }
    }
}

/// <summary>Google News RSS search: no key, no documented quota, English/US edition.</summary>
public sealed class GoogleNewsAdverseMediaSource : IAdverseMediaSource
{
    private readonly IHttpClientFactory _factory;
    public GoogleNewsAdverseMediaSource(IHttpClientFactory factory) => _factory = factory;
    public string Name => "Google News";

    public async Task<IReadOnlyList<AdverseMediaArticle>> SearchAsync(ScreeningSubject subject, CancellationToken ct)
    {
        var client = _factory.CreateClient(SanctionsOptions.HttpClientName);
        var q = $"\"{subject.Name}\" ({string.Join(" OR ", AdverseMediaLexicon.QueryTerms)})";
        var url = $"https://news.google.com/rss/search?q={Uri.EscapeDataString(q)}&hl=en-US&gl=US&ceid=US:en";
        var xml = await client.GetStringAsync(url, ct);
        return RssReader.Read(xml, Name, item =>
        {
            // Google wraps the headline as "Title - Publisher" and puts the publisher in <source>.
            var source = item.Element("source")?.Value;
            var title = item.Element("title")?.Value ?? string.Empty;
            if (source is { Length: > 0 } && title.EndsWith(" - " + source, StringComparison.Ordinal)) title = title[..^(source.Length + 3)];
            return (title, source);
        });
    }
}

/// <summary>Bing News RSS search: no key.</summary>
public sealed class BingNewsAdverseMediaSource : IAdverseMediaSource
{
    private static readonly XNamespace News = "https://www.bing.com/news/search?q=&format=rss";
    private readonly IHttpClientFactory _factory;
    public BingNewsAdverseMediaSource(IHttpClientFactory factory) => _factory = factory;
    public string Name => "Bing News";

    public async Task<IReadOnlyList<AdverseMediaArticle>> SearchAsync(ScreeningSubject subject, CancellationToken ct)
    {
        var client = _factory.CreateClient(SanctionsOptions.HttpClientName);
        var q = $"\"{subject.Name}\" ({string.Join(" OR ", AdverseMediaLexicon.QueryTerms.Take(8))})";
        var url = $"https://www.bing.com/news/search?q={Uri.EscapeDataString(q)}&format=rss";
        var xml = await client.GetStringAsync(url, ct);
        return RssReader.Read(xml, Name, item =>
        {
            var source = item.Elements().FirstOrDefault(e => e.Name.LocalName == "Source")?.Value;
            return (item.Element("title")?.Value ?? string.Empty, source);
        });
    }
}

/// <summary>Wikipedia full-text search: catches notable subjects whose article discusses fraud, convictions, sanctions, etc.</summary>
public sealed class WikipediaAdverseMediaSource : IAdverseMediaSource
{
    private readonly IHttpClientFactory _factory;
    public WikipediaAdverseMediaSource(IHttpClientFactory factory) => _factory = factory;
    public string Name => "Wikipedia";

    public async Task<IReadOnlyList<AdverseMediaArticle>> SearchAsync(ScreeningSubject subject, CancellationToken ct)
    {
        var client = _factory.CreateClient(SanctionsOptions.HttpClientName);
        var q = $"\"{subject.Name}\" {string.Join(" OR ", AdverseMediaLexicon.QueryTerms.Take(10))}";
        var url = $"https://en.wikipedia.org/w/api.php?action=query&list=search&srsearch={Uri.EscapeDataString(q)}&format=json&srlimit=10&srprop=snippet|timestamp";
        var body = await client.GetStringAsync(url, ct);
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("query", out var query) || !query.TryGetProperty("search", out var results)) return Array.Empty<AdverseMediaArticle>();
        var list = new List<AdverseMediaArticle>();
        foreach (var r in results.EnumerateArray())
        {
            var title = r.GetProperty("title").GetString() ?? string.Empty;
            var snippet = r.TryGetProperty("snippet", out var s) ? s.GetString() : null;
            DateTimeOffset? ts = r.TryGetProperty("timestamp", out var t) && DateTimeOffset.TryParse(t.GetString(), out var p) ? p : null;
            var pageUrl = new Uri("https://en.wikipedia.org/wiki/" + Uri.EscapeDataString(title.Replace(' ', '_')));
            list.Add(new AdverseMediaArticle(title, pageUrl, "en.wikipedia.org", ts, "neutral", Snippet: snippet));
        }
        return list;
    }
}

/// <summary>CourtListener (Free Law Project) opinion search: US court decisions naming the subject. Litigation is reported as a mention unless the case text carries risk terms.</summary>
public sealed class CourtListenerAdverseMediaSource : IAdverseMediaSource
{
    private readonly IHttpClientFactory _factory;
    public CourtListenerAdverseMediaSource(IHttpClientFactory factory) => _factory = factory;
    public string Name => "CourtListener";

    public async Task<IReadOnlyList<AdverseMediaArticle>> SearchAsync(ScreeningSubject subject, CancellationToken ct)
    {
        var client = _factory.CreateClient(SanctionsOptions.HttpClientName);
        var url = $"https://www.courtlistener.com/api/rest/v4/search/?q={Uri.EscapeDataString($"\"{subject.Name}\"")}&type=o&order_by=dateFiled+desc";
        var body = await client.GetStringAsync(url, ct);
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("results", out var results)) return Array.Empty<AdverseMediaArticle>();
        var list = new List<AdverseMediaArticle>();
        foreach (var r in results.EnumerateArray().Take(10))
        {
            var caseName = r.TryGetProperty("caseName", out var cn) ? cn.GetString() ?? string.Empty : string.Empty;
            if (caseName.Length == 0) continue;
            var court = r.TryGetProperty("court", out var c) ? c.GetString() : null;
            DateTimeOffset? filed = r.TryGetProperty("dateFiled", out var df) && DateTimeOffset.TryParse(df.GetString(), out var p) ? p : null;
            var path = r.TryGetProperty("absolute_url", out var au) ? au.GetString() : null;
            var snippet = new StringBuilder();
            if (r.TryGetProperty("suitNature", out var sn) && sn.GetString() is { Length: > 0 } nature) snippet.Append(nature).Append(". ");
            if (r.TryGetProperty("opinions", out var ops))
                foreach (var op in ops.EnumerateArray())
                    if (op.TryGetProperty("snippet", out var s) && s.GetString() is { Length: > 0 } text) snippet.Append(text).Append(' ');
            var uri = new Uri("https://www.courtlistener.com" + (path ?? "/"));
            list.Add(new AdverseMediaArticle($"{caseName}{(court is { Length: > 0 } ? $" ({court})" : "")}", uri, "courtlistener.com", filed, "neutral", Snippet: snippet.ToString()));
        }
        return list;
    }
}

internal static class RssReader
{
    public static IReadOnlyList<AdverseMediaArticle> Read(string xml, string provider, Func<XElement, (string Title, string? Source)> headline)
    {
        var doc = XDocument.Parse(xml);
        var list = new List<AdverseMediaArticle>();
        foreach (var item in doc.Descendants("item"))
        {
            var link = item.Element("link")?.Value;
            if (!Uri.TryCreate(link, UriKind.Absolute, out var url)) continue;
            var (title, source) = headline(item);
            var description = item.Element("description")?.Value;
            DateTimeOffset? published = DateTimeOffset.TryParse(item.Element("pubDate")?.Value, out var p) ? p : null;
            var host = source is { Length: > 0 } ? source : url.Host;
            list.Add(new AdverseMediaArticle(title, url, host, published, "neutral", Snippet: description, Provider: provider));
        }
        return list;
    }
}
