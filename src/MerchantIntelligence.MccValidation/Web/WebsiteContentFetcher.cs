using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace MerchantIntelligence.MccValidation.Web;

public sealed record WebsiteContent(
    Uri RequestedUrl,
    IReadOnlyList<Uri> FetchedPages,
    IReadOnlyList<ExtractedPage> Pages)
{
    public bool IsEmpty => Pages.Count == 0 || Pages.All(p => string.IsNullOrWhiteSpace(p.BodyText));

    public string CombinedText => string.Join(" ", Pages.Select(p => p.ToClassifierText()));

    public IReadOnlyList<string> SchemaOrgTypes =>
        Pages.SelectMany(p => p.SchemaOrgTypes).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}

/// <summary>
/// Downloads a merchant's homepage plus a few "about / products / services" pages.
/// </summary>
public sealed class WebsiteContentFetcher
{
    public const string HttpClientName = "MerchantWebsite";

    private static readonly Regex InterestingPath = new(
        @"(about|company|who-we-are|our-story|products?|services?|shop|menu|what-we-do|solutions?)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<WebsiteContentFetcher> _logger;

    public WebsiteContentFetcher(IHttpClientFactory httpClientFactory, ILogger<WebsiteContentFetcher> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<WebsiteContent> FetchAsync(Uri url, int maxPages = 4, CancellationToken ct = default)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        var fetched = new List<Uri>();
        var pages = new List<ExtractedPage>();

        var home = await FetchPageAsync(client, url, ct);
        if (home is null) return new WebsiteContent(url, fetched, pages);

        fetched.Add(url);
        pages.Add(home);

        var candidates = home.InternalLinks
            .Where(l => InterestingPath.IsMatch(l.AbsolutePath))
            .OrderBy(l => l.AbsolutePath.Length)
            .Where(l => l != url)
            .Take(maxPages - 1);

        foreach (var link in candidates)
        {
            var page = await FetchPageAsync(client, link, ct);
            if (page is null) continue;
            fetched.Add(link);
            pages.Add(page);
        }

        return new WebsiteContent(url, fetched, pages);
    }

    private async Task<ExtractedPage?> FetchPageAsync(HttpClient client, Uri url, CancellationToken ct)
    {
        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Website fetch {Url} returned {Status}", url, (int)response.StatusCode);
                return null;
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (!mediaType.Contains("html", StringComparison.OrdinalIgnoreCase) && mediaType.Length > 0)
            {
                return null;
            }

            var html = await response.Content.ReadAsStringAsync(ct);
            return HtmlTextExtractor.Extract(html, url);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            _logger.LogInformation(ex, "Website fetch {Url} failed", url);
            return null;
        }
    }
}
