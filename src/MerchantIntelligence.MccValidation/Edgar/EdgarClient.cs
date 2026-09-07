using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;

namespace MerchantIntelligence.MccValidation.Edgar;

public sealed record EdgarCompany(int Cik, string Ticker, string Name);

public sealed record EdgarFiling(string AccessionNumber, string PrimaryDocument, string Form, DateOnly FilingDate);

public sealed record EdgarSubmission(
    int Cik,
    string Name,
    int? Sic,
    string SicDescription,
    string? Website,
    string? StateOfIncorporation,
    IReadOnlyList<EdgarFiling> Filings)
{
    public EdgarFiling? Latest10K =>
        Filings.Where(f => f.Form is "10-K" or "10-K405" or "20-F")
            .OrderByDescending(f => f.FilingDate)
            .FirstOrDefault();
}

public sealed class EdgarClientOptions
{
    /// <summary>SEC requires a descriptive User-Agent with contact details.</summary>
    public string UserAgent { get; set; } = "MerchantIntelligence research contact@example.com";

    /// <summary>SEC fair-access limit is 10 requests/second; stay comfortably under it.</summary>
    public int MaxRequestsPerSecond { get; set; } = 8;

    public string CacheDirectory { get; set; } = Path.Combine("data", "edgar-cache");
}

/// <summary>
/// Thin, throttled, disk-cached client for the public SEC EDGAR endpoints.
/// </summary>
public sealed class EdgarClient
{
    public const string HttpClientName = "SecEdgar";

    private static readonly Regex ItemOneBusiness = new(
        @"item\s*1\s*[\.\-:–—]?\s*business",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ItemOneAEnd = new(
        @"item\s*1a\s*[\.\-:–—]?\s*risk\s*factors|item\s*2\s*[\.\-:–—]?\s*properties",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    // "...our website, www.example.com, ..." / "Internet address is http://example.com"
    private static readonly Regex WebsiteMention = new(
        @"(?:web\s?site|internet address|home page)[^.]{0,80}?((?:https?://)?(?:[a-z0-9-]+\.)+(?:com|net|org|io|co|us|ai|tv|biz|info))\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> IgnoredWebsiteHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "sec.gov", "www.sec.gov", "nyse.com", "www.nyse.com", "nasdaq.com", "www.nasdaq.com"
    };

    private readonly HttpClient _http;
    private readonly EdgarClientOptions _options;
    private readonly ILogger<EdgarClient> _logger;
    private readonly SemaphoreSlim _throttle = new(1, 1);
    private DateTime _nextAllowedRequestUtc = DateTime.MinValue;

    public EdgarClient(HttpClient http, EdgarClientOptions options, ILogger<EdgarClient> logger)
    {
        _http = http;
        _options = options;
        _logger = logger;
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", _options.UserAgent);
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/html");
        Directory.CreateDirectory(_options.CacheDirectory);
    }

    public async Task<IReadOnlyList<EdgarCompany>> GetCompaniesAsync(CancellationToken ct = default)
    {
        var json = await GetCachedAsync("https://www.sec.gov/files/company_tickers.json", "company_tickers.json", ct);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateObject()
            .Select(p => p.Value)
            .Select(v => new EdgarCompany(
                v.GetProperty("cik_str").GetInt32(),
                v.GetProperty("ticker").GetString() ?? string.Empty,
                v.GetProperty("title").GetString() ?? string.Empty))
            .ToList();
    }

    public async Task<EdgarSubmission?> GetSubmissionAsync(int cik, CancellationToken ct = default)
    {
        var padded = cik.ToString("D10");
        string json;
        try
        {
            json = await GetCachedAsync($"https://data.sec.gov/submissions/CIK{padded}.json", $"submissions/CIK{padded}.json", ct);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("Submission fetch for CIK {Cik} failed: {Message}", cik, ex.Message);
            return null;
        }

        var raw = JsonSerializer.Deserialize<SubmissionDto>(json);
        if (raw is null) return null;

        var recent = raw.Filings?.Recent;
        var filings = new List<EdgarFiling>();
        if (recent is not null)
        {
            var count = Math.Min(
                Math.Min(recent.AccessionNumber.Count, recent.PrimaryDocument.Count),
                Math.Min(recent.Form.Count, recent.FilingDate.Count));
            for (var i = 0; i < count; i++)
            {
                if (DateOnly.TryParse(recent.FilingDate[i], out var date))
                    filings.Add(new EdgarFiling(recent.AccessionNumber[i], recent.PrimaryDocument[i], recent.Form[i], date));
            }
        }

        int? sic = int.TryParse(raw.Sic, out var s) ? s : null;
        return new EdgarSubmission(cik, raw.Name ?? string.Empty, sic, raw.SicDescription ?? string.Empty,
            string.IsNullOrWhiteSpace(raw.Website) ? null : raw.Website, raw.StateOfIncorporation, filings);
    }

    public async Task<string?> GetBusinessDescriptionAsync(int cik, EdgarFiling filing, CancellationToken ct = default)
    {
        var html = await GetFilingHtmlAsync(cik, filing, ct);
        return html is null ? null : ExtractBusinessSection(html);
    }

    /// <summary>
    /// EDGAR's submissions feed leaves <c>website</c> empty for most filers, so fall back to the
    /// "our website is ..." sentence that 10-Ks include under "Available Information".
    /// </summary>
    public async Task<Uri?> GetWebsiteAsync(int cik, EdgarFiling filing, CancellationToken ct = default)
    {
        var html = await GetFilingHtmlAsync(cik, filing, ct);
        return html is null ? null : ExtractWebsite(html);
    }

    public static Uri? ExtractWebsite(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var text = Whitespace.Replace(HtmlEntity.DeEntitize(doc.DocumentNode.InnerText), " ");

        foreach (Match m in WebsiteMention.Matches(text))
        {
            var raw = m.Groups[1].Value.TrimEnd('.', ',', ';');
            if (!raw.Contains("://", StringComparison.Ordinal)) raw = "https://" + raw;
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) continue;
            if (IgnoredWebsiteHosts.Contains(uri.Host)) continue;
            return new Uri($"https://{uri.Host}/");
        }

        return null;
    }

    private async Task<string?> GetFilingHtmlAsync(int cik, EdgarFiling filing, CancellationToken ct)
    {
        var accession = filing.AccessionNumber.Replace("-", string.Empty);
        var url = $"https://www.sec.gov/Archives/edgar/data/{cik}/{accession}/{filing.PrimaryDocument}";
        try
        {
            return await GetCachedAsync(url, $"filings/{cik}/{accession}/{filing.PrimaryDocument}", ct);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("Filing fetch {Url} failed: {Message}", url, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Extracts "Item 1. Business" text from a 10-K HTML document. Skips the table-of-contents
    /// occurrence by taking the longest section between an "Item 1. Business" heading and the next
    /// "Item 1A"/"Item 2" heading.
    /// </summary>
    public static string? ExtractBusinessSection(string html, int maxChars = 30_000)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        foreach (var n in doc.DocumentNode.SelectNodes("//script|//style") ?? Enumerable.Empty<HtmlNode>())
            n.Remove();

        var text = Whitespace.Replace(HtmlEntity.DeEntitize(doc.DocumentNode.InnerText), " ");

        string? best = null;
        foreach (Match start in ItemOneBusiness.Matches(text))
        {
            var from = start.Index + start.Length;
            var end = ItemOneAEnd.Match(text, from);
            var to = end.Success ? end.Index : Math.Min(text.Length, from + maxChars);
            var length = to - from;
            if (length > (best?.Length ?? 0))
                best = text.Substring(from, length).Trim();
        }

        if (best is null || best.Length < 500) return null;
        return best.Length > maxChars ? best[..maxChars] : best;
    }

    private async Task<string> GetCachedAsync(string url, string cacheKey, CancellationToken ct)
    {
        var path = Path.Combine(_options.CacheDirectory, cacheKey);
        if (File.Exists(path)) return await File.ReadAllTextAsync(path, ct);

        await ThrottleAsync(ct);
        using var response = await _http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(ct);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, body, ct);
        return body;
    }

    private async Task ThrottleAsync(CancellationToken ct)
    {
        await _throttle.WaitAsync(ct);
        try
        {
            var now = DateTime.UtcNow;
            if (now < _nextAllowedRequestUtc)
                await Task.Delay(_nextAllowedRequestUtc - now, ct);
            _nextAllowedRequestUtc = DateTime.UtcNow.AddMilliseconds(1000.0 / _options.MaxRequestsPerSecond);
        }
        finally
        {
            _throttle.Release();
        }
    }

    private sealed class SubmissionDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("sic")] public string? Sic { get; set; }
        [JsonPropertyName("sicDescription")] public string? SicDescription { get; set; }
        [JsonPropertyName("website")] public string? Website { get; set; }
        [JsonPropertyName("stateOfIncorporation")] public string? StateOfIncorporation { get; set; }
        [JsonPropertyName("filings")] public FilingsDto? Filings { get; set; }
    }

    private sealed class FilingsDto
    {
        [JsonPropertyName("recent")] public RecentDto? Recent { get; set; }
    }

    private sealed class RecentDto
    {
        [JsonPropertyName("accessionNumber")] public List<string> AccessionNumber { get; set; } = new();
        [JsonPropertyName("primaryDocument")] public List<string> PrimaryDocument { get; set; } = new();
        [JsonPropertyName("form")] public List<string> Form { get; set; } = new();
        [JsonPropertyName("filingDate")] public List<string> FilingDate { get; set; } = new();
    }
}
