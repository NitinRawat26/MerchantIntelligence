using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace MerchantIntelligence.Platform.Integrations;

public sealed record MatchInquiry(
    string LegalName,
    string? DoingBusinessAs,
    string? TaxId,
    string? Country,
    string? AddressLine,
    string? City,
    string? Region,
    string? PostalCode,
    IReadOnlyList<MatchPrincipal> Principals);

public sealed record MatchPrincipal(string FirstName, string LastName, DateOnly? DateOfBirth, string? NationalId);

public enum MatchAvailability
{
    /// <summary>Provider not configured (no credentials); treat as unknown, not clear.</summary>
    NotConfigured,
    Available,
    Error
}

public sealed record MatchHit(string MatchedOn, string ReasonCode, string ReasonDescription, DateOnly? TerminationDate, string? Acquirer);

public sealed record MatchResult(
    MatchAvailability Availability,
    bool? Found,
    IReadOnlyList<MatchHit> Hits,
    string Provider,
    string? Message);

/// <summary>
/// Boundary for the Mastercard MATCH (Member Alert To Control High-risk merchants) lookup. MATCH is only
/// accessible to Mastercard-licensed acquirers, so the suite ships without a working client; plug a real
/// implementation in when credentials exist. Callers must treat <see cref="MatchAvailability.NotConfigured"/>
/// as "unknown" and fall back to the self-declared <c>MatchFound</c> flag.
/// </summary>
public interface IMatchProvider
{
    Task<MatchResult> InquireAsync(MatchInquiry inquiry, CancellationToken ct = default);
}

public sealed class MatchOptions
{
    /// <summary>Optional endpoint of a MATCH-compatible service (e.g. Mastercard Developers MATCH API or an internal proxy).</summary>
    public string? Endpoint { get; set; }
    public string? ApiKey { get; set; }
    /// <summary>Optional path to a local CSV of terminated merchants (name,taxId,reasonCode,terminationDate,acquirer) for testing / internal lists.</summary>
    public string? LocalListPath { get; set; }
}

/// <summary>Used when no MATCH access is configured: never claims a merchant is clear.</summary>
public sealed class UnavailableMatchProvider : IMatchProvider
{
    public Task<MatchResult> InquireAsync(MatchInquiry inquiry, CancellationToken ct = default) =>
        Task.FromResult(new MatchResult(MatchAvailability.NotConfigured, null, [], "none",
            "MATCH access requires Mastercard acquirer credentials; configure Match:Endpoint/ApiKey or Match:LocalListPath. Result is UNKNOWN, not clear."));
}

/// <summary>
/// Looks up a local terminated-merchant list (an acquirer's own TMF export). Exact tax-id match or normalised
/// legal-name match counts as a hit.
/// </summary>
public sealed class LocalListMatchProvider : IMatchProvider
{
    private readonly List<(string Name, string? TaxId, string Reason, DateOnly? Date, string? Acquirer)> _rows = new();

    public LocalListMatchProvider(string path)
    {
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            var parts = line.Split(',');
            if (parts.Length < 3 || string.IsNullOrWhiteSpace(parts[0])) continue;
            _rows.Add((Normalise(parts[0]), NormaliseId(parts.ElementAtOrDefault(1)), parts[2].Trim(),
                DateOnly.TryParse(parts.ElementAtOrDefault(3), out var d) ? d : null, parts.ElementAtOrDefault(4)?.Trim()));
        }
    }

    public int Count => _rows.Count;

    public Task<MatchResult> InquireAsync(MatchInquiry inquiry, CancellationToken ct = default)
    {
        var name = Normalise(inquiry.LegalName);
        var dba = inquiry.DoingBusinessAs is null ? null : Normalise(inquiry.DoingBusinessAs);
        var taxId = NormaliseId(inquiry.TaxId);
        var hits = new List<MatchHit>();
        foreach (var row in _rows)
        {
            if (taxId is not null && row.TaxId is not null && taxId == row.TaxId)
                hits.Add(new MatchHit("TaxId", row.Reason, ReasonText(row.Reason), row.Date, row.Acquirer));
            else if (row.Name == name || (dba is not null && row.Name == dba))
                hits.Add(new MatchHit("LegalName", row.Reason, ReasonText(row.Reason), row.Date, row.Acquirer));
        }
        return Task.FromResult(new MatchResult(MatchAvailability.Available, hits.Count > 0, hits, "local-list", null));
    }

    private static string Normalise(string s) =>
        new string(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static string? NormaliseId(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : new string(s.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    /// <summary>Standard MATCH reason codes.</summary>
    public static string ReasonText(string code) => code switch
    {
        "01" => "Account data compromise",
        "02" => "Common point of purchase",
        "03" => "Laundering",
        "04" => "Excessive chargebacks",
        "05" => "Excessive fraud",
        "07" => "Fraud conviction",
        "08" => "Mastercard questionable merchant audit program",
        "09" => "Bankruptcy / liquidation / insolvency",
        "10" => "Violation of Mastercard standards",
        "11" => "Merchant collusion",
        "12" => "PCI DSS non-compliance",
        "13" => "Illegal transactions",
        "14" => "Identity theft",
        _ => "Unspecified"
    };
}

/// <summary>Thin HTTP client for a MATCH-compatible endpoint; request/response shapes follow the Mastercard MATCH v2 inquiry API loosely.</summary>
public sealed class HttpMatchProvider : IMatchProvider
{
    public const string HttpClientName = "MerchantIntelligence.Match";
    private readonly IHttpClientFactory _http;
    private readonly MatchOptions _options;
    private readonly ILogger<HttpMatchProvider> _logger;

    public HttpMatchProvider(IHttpClientFactory http, MatchOptions options, ILogger<HttpMatchProvider> logger)
    {
        _http = http;
        _options = options;
        _logger = logger;
    }

    public async Task<MatchResult> InquireAsync(MatchInquiry inquiry, CancellationToken ct = default)
    {
        try
        {
            var client = _http.CreateClient(HttpClientName);
            using var req = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint) { Content = JsonContent.Create(inquiry) };
            if (!string.IsNullOrEmpty(_options.ApiKey)) req.Headers.Add("Authorization", "Bearer " + _options.ApiKey);
            using var resp = await client.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
                return new MatchResult(MatchAvailability.Error, null, [], "http", $"MATCH endpoint returned HTTP {(int)resp.StatusCode}.");
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var hits = new List<MatchHit>();
            if (doc.RootElement.TryGetProperty("hits", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var h in arr.EnumerateArray())
                {
                    var code = h.TryGetProperty("reasonCode", out var rc) ? rc.GetString() ?? "" : "";
                    hits.Add(new MatchHit(h.TryGetProperty("matchedOn", out var mo) ? mo.GetString() ?? "" : "",
                        code, LocalListMatchProvider.ReasonText(code),
                        h.TryGetProperty("terminationDate", out var td) && DateOnly.TryParse(td.GetString(), out var d) ? d : null,
                        h.TryGetProperty("acquirer", out var aq) ? aq.GetString() : null));
                }
            return new MatchResult(MatchAvailability.Available, hits.Count > 0, hits, "http", null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "MATCH inquiry failed");
            return new MatchResult(MatchAvailability.Error, null, [], "http", ex.Message);
        }
    }
}
