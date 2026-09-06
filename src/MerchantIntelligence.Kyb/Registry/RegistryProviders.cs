using System.Net.Http.Json;
using System.Text.Json;
using MerchantIntelligence.Kyb.Matching;
using Microsoft.Extensions.Logging;

namespace MerchantIntelligence.Kyb.Registry;

public interface IBusinessRegistryProvider
{
    string Name { get; }

    /// <summary>True when the provider can be used with the current configuration (e.g. API key present).</summary>
    bool IsEnabled { get; }

    Task<IReadOnlyList<RegistryRecord>> SearchAsync(BusinessIdentity identity, CancellationToken ct);
}

public sealed class KybOptions
{
    public const string HttpClientName = "KybRegistry";

    /// <summary>Contact identity sent in the User-Agent; SEC and public registries require one.</summary>
    public string UserAgent { get; set; } = "MerchantIntelligence KYB contact@example.com";

    /// <summary>Optional. https://opencorporates.com/api_accounts/new (free tier available).</summary>
    public string? OpenCorporatesApiToken { get; set; }

    /// <summary>Optional. Free key from https://developer.company-information.service.gov.uk/.</summary>
    public string? CompaniesHouseApiKey { get; set; }

    public int MaxResultsPerSource { get; set; } = 5;

    /// <summary>Entities younger than this many months are flagged as newly incorporated.</summary>
    public int NewEntityThresholdMonths { get; set; } = 12;
}

/// <summary>Global LEI Foundation open API. Free, no key, worldwide coverage of entities holding an LEI.</summary>
public sealed class GleifRegistryProvider : IBusinessRegistryProvider
{
    private readonly IHttpClientFactory _factory;
    private readonly KybOptions _options;
    private readonly ILogger<GleifRegistryProvider> _logger;

    public GleifRegistryProvider(IHttpClientFactory factory, KybOptions options, ILogger<GleifRegistryProvider> logger)
    {
        _factory = factory;
        _options = options;
        _logger = logger;
    }

    public string Name => "GLEIF LEI";
    public bool IsEnabled => true;

    public async Task<IReadOnlyList<RegistryRecord>> SearchAsync(BusinessIdentity identity, CancellationToken ct)
    {
        var client = _factory.CreateClient(KybOptions.HttpClientName);
        var url = $"https://api.gleif.org/api/v1/lei-records?filter[fulltext]={Uri.EscapeDataString(identity.LegalName)}&page[size]={_options.MaxResultsPerSource}";
        using var doc = await client.GetFromJsonAsync<JsonDocument>(url, ct);
        if (doc is null || !doc.RootElement.TryGetProperty("data", out var data)) return Array.Empty<RegistryRecord>();

        var results = new List<RegistryRecord>();
        foreach (var item in data.EnumerateArray())
        {
            var attr = item.GetProperty("attributes");
            var entity = attr.GetProperty("entity");
            var lei = attr.GetProperty("lei").GetString() ?? string.Empty;
            var name = entity.GetProperty("legalName").GetProperty("name").GetString() ?? string.Empty;
            var address = FormatAddress(entity.TryGetProperty("legalAddress", out var la) ? la : default);
            var status = entity.TryGetProperty("status", out var s) ? s.GetString() : null;
            var jurisdiction = entity.TryGetProperty("jurisdiction", out var j) ? j.GetString() : null;
            var regAs = entity.TryGetProperty("registeredAs", out var ra) ? ra.GetString() : null;
            var legalForm = entity.TryGetProperty("legalForm", out var lf) && lf.ValueKind == JsonValueKind.Object && lf.TryGetProperty("id", out var lfid) ? lfid.GetString() : null;
            DateOnly? created = null;
            if (entity.TryGetProperty("creationDate", out var cd) && cd.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(cd.GetString(), out var dto)) created = DateOnly.FromDateTime(dto.UtcDateTime);

            results.Add(new RegistryRecord(Name, lei, name, status, created, jurisdiction, regAs, address, legalForm,
                new Uri($"https://search.gleif.org/#/record/{lei}")));
        }
        _logger.LogDebug("GLEIF returned {Count} records for {Name}", results.Count, identity.LegalName);
        return results;
    }

    private static string? FormatAddress(JsonElement addr)
    {
        if (addr.ValueKind != JsonValueKind.Object) return null;
        var parts = new List<string>();
        if (addr.TryGetProperty("addressLines", out var lines) && lines.ValueKind == JsonValueKind.Array)
            parts.AddRange(lines.EnumerateArray().Select(l => l.GetString()).Where(l => !string.IsNullOrWhiteSpace(l))!);
        foreach (var key in new[] { "city", "region", "postalCode", "country" })
        {
            if (addr.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()))
                parts.Add(v.GetString()!);
        }
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }
}

/// <summary>SEC EDGAR company search + submissions API. Free, no key; covers US public filers.</summary>
public sealed class EdgarRegistryProvider : IBusinessRegistryProvider
{
    private readonly IHttpClientFactory _factory;
    private readonly KybOptions _options;

    public EdgarRegistryProvider(IHttpClientFactory factory, KybOptions options)
    {
        _factory = factory;
        _options = options;
    }

    public string Name => "SEC EDGAR";
    public bool IsEnabled => true;

    public async Task<IReadOnlyList<RegistryRecord>> SearchAsync(BusinessIdentity identity, CancellationToken ct)
    {
        var client = _factory.CreateClient(KybOptions.HttpClientName);
        var url = $"https://efts.sec.gov/LATEST/search-index?keysTyped={Uri.EscapeDataString(identity.LegalName)}";
        using var doc = await client.GetFromJsonAsync<JsonDocument>(url, ct);
        if (doc is null || !doc.RootElement.TryGetProperty("hits", out var hits) || !hits.TryGetProperty("hits", out var arr))
            return Array.Empty<RegistryRecord>();

        var results = new List<RegistryRecord>();
        foreach (var hit in arr.EnumerateArray().Take(_options.MaxResultsPerSource))
        {
            var cik = hit.GetProperty("_id").GetString();
            var entityName = hit.GetProperty("_source").GetProperty("entity").GetString() ?? string.Empty;
            if (cik is null) continue;
            var record = await FetchSubmissionAsync(client, cik, entityName, ct);
            if (record is not null) results.Add(record);
        }
        return results;
    }

    private async Task<RegistryRecord?> FetchSubmissionAsync(HttpClient client, string cik, string fallbackName, CancellationToken ct)
    {
        var padded = cik.PadLeft(10, '0');
        try
        {
            using var doc = await client.GetFromJsonAsync<JsonDocument>($"https://data.sec.gov/submissions/CIK{padded}.json", ct);
            if (doc is null) return null;
            var root = doc.RootElement;
            var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? fallbackName : fallbackName;
            var extra = new Dictionary<string, string>();
            foreach (var key in new[] { "sic", "sicDescription", "stateOfIncorporation", "entityType", "ein", "website" })
            {
                if (root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()))
                    extra[key] = v.GetString()!;
            }
            string? address = null;
            if (root.TryGetProperty("addresses", out var addrs) && addrs.TryGetProperty("business", out var biz) && biz.ValueKind == JsonValueKind.Object)
            {
                var parts = new[] { "street1", "street2", "city", "stateOrCountry", "zipCode" }
                    .Select(k => biz.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null)
                    .Where(s => !string.IsNullOrWhiteSpace(s));
                address = string.Join(", ", parts!);
            }
            // Filers with recent filings are treated as active.
            string? status = null;
            if (root.TryGetProperty("filings", out var filings) && filings.TryGetProperty("recent", out var recent)
                && recent.TryGetProperty("filingDate", out var dates) && dates.ValueKind == JsonValueKind.Array && dates.GetArrayLength() > 0
                && DateOnly.TryParse(dates[0].GetString(), out var last))
            {
                status = last > DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-2)) ? "ACTIVE" : "DORMANT";
                extra["lastFilingDate"] = last.ToString("yyyy-MM-dd");
            }
            var state = extra.GetValueOrDefault("stateOfIncorporation");
            var jurisdiction = string.IsNullOrWhiteSpace(state) ? "US" : state.Length == 2 ? $"US-{state}" : state;
            return new RegistryRecord(Name, cik, name, status, null,
                jurisdiction, extra.GetValueOrDefault("ein"), address,
                extra.GetValueOrDefault("entityType"),
                new Uri($"https://www.sec.gov/cgi-bin/browse-edgar?action=getcompany&CIK={padded}"), extra);
        }
        catch (HttpRequestException)
        {
            return new RegistryRecord(Name, cik, fallbackName, null, null, null, null, null, null,
                new Uri($"https://www.sec.gov/cgi-bin/browse-edgar?action=getcompany&CIK={padded}"));
        }
    }
}

/// <summary>OpenCorporates (140+ jurisdictions). Requires a free API token.</summary>
public sealed class OpenCorporatesRegistryProvider : IBusinessRegistryProvider
{
    private readonly IHttpClientFactory _factory;
    private readonly KybOptions _options;

    public OpenCorporatesRegistryProvider(IHttpClientFactory factory, KybOptions options)
    {
        _factory = factory;
        _options = options;
    }

    public string Name => "OpenCorporates";
    public bool IsEnabled => !string.IsNullOrWhiteSpace(_options.OpenCorporatesApiToken);

    public async Task<IReadOnlyList<RegistryRecord>> SearchAsync(BusinessIdentity identity, CancellationToken ct)
    {
        var client = _factory.CreateClient(KybOptions.HttpClientName);
        var url = $"https://api.opencorporates.com/v0.4/companies/search?q={Uri.EscapeDataString(identity.LegalName)}&per_page={_options.MaxResultsPerSource}&api_token={_options.OpenCorporatesApiToken}";
        if (!string.IsNullOrWhiteSpace(identity.Country) && identity.Country.Length == 2)
            url += $"&jurisdiction_code={identity.Country.ToLowerInvariant()}";
        using var doc = await client.GetFromJsonAsync<JsonDocument>(url, ct);
        if (doc is null) return Array.Empty<RegistryRecord>();
        var companies = doc.RootElement.GetProperty("results").GetProperty("companies");
        var list = new List<RegistryRecord>();
        foreach (var wrapper in companies.EnumerateArray())
        {
            var c = wrapper.GetProperty("company");
            DateOnly? inc = c.TryGetProperty("incorporation_date", out var d) && d.ValueKind == JsonValueKind.String && DateOnly.TryParse(d.GetString(), out var dd) ? dd : null;
            list.Add(new RegistryRecord(Name,
                c.GetProperty("company_number").GetString() ?? string.Empty,
                c.GetProperty("name").GetString() ?? string.Empty,
                c.TryGetProperty("current_status", out var st) ? st.GetString() : null,
                inc,
                c.TryGetProperty("jurisdiction_code", out var j) ? j.GetString()?.ToUpperInvariant() : null,
                c.GetProperty("company_number").GetString(),
                c.TryGetProperty("registered_address_in_full", out var a) ? a.GetString() : null,
                c.TryGetProperty("company_type", out var t) ? t.GetString() : null,
                c.TryGetProperty("opencorporates_url", out var u) && Uri.TryCreate(u.GetString(), UriKind.Absolute, out var uri) ? uri : null));
        }
        return list;
    }
}

/// <summary>UK Companies House. Requires a free API key.</summary>
public sealed class CompaniesHouseRegistryProvider : IBusinessRegistryProvider
{
    private readonly IHttpClientFactory _factory;
    private readonly KybOptions _options;

    public CompaniesHouseRegistryProvider(IHttpClientFactory factory, KybOptions options)
    {
        _factory = factory;
        _options = options;
    }

    public string Name => "UK Companies House";
    public bool IsEnabled => !string.IsNullOrWhiteSpace(_options.CompaniesHouseApiKey);

    public async Task<IReadOnlyList<RegistryRecord>> SearchAsync(BusinessIdentity identity, CancellationToken ct)
    {
        var client = _factory.CreateClient(KybOptions.HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.company-information.service.gov.uk/search/companies?q={Uri.EscapeDataString(identity.LegalName)}&items_per_page={_options.MaxResultsPerSource}");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes(_options.CompaniesHouseApiKey + ":")));
        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var list = new List<RegistryRecord>();
        if (!doc.RootElement.TryGetProperty("items", out var items)) return list;
        foreach (var c in items.EnumerateArray())
        {
            var number = c.TryGetProperty("company_number", out var cn) ? cn.GetString() ?? string.Empty : string.Empty;
            DateOnly? inc = c.TryGetProperty("date_of_creation", out var d) && d.ValueKind == JsonValueKind.String && DateOnly.TryParse(d.GetString(), out var dd) ? dd : null;
            list.Add(new RegistryRecord(Name, number,
                c.GetProperty("title").GetString() ?? string.Empty,
                c.TryGetProperty("company_status", out var st) ? st.GetString() : null,
                inc, "GB", number,
                c.TryGetProperty("address_snippet", out var a) ? a.GetString() : null,
                c.TryGetProperty("company_type", out var t) ? t.GetString() : null,
                new Uri($"https://find-and-update.company-information.service.gov.uk/company/{number}")));
        }
        return list;
    }
}

public interface IAddressGeocoder
{
    string Name { get; }
    Task<AddressVerification> VerifyAsync(BusinessIdentity identity, CancellationToken ct);
}

/// <summary>US Census Bureau geocoder. Free, no key; US addresses only.</summary>
public sealed class CensusAddressGeocoder : IAddressGeocoder
{
    private readonly IHttpClientFactory _factory;

    public CensusAddressGeocoder(IHttpClientFactory factory) => _factory = factory;

    public string Name => "US Census Geocoder";

    public async Task<AddressVerification> VerifyAsync(BusinessIdentity identity, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(identity.AddressLine))
            return new AddressVerification(Name, false, null, null, null, "No street address supplied.");
        if (!string.IsNullOrWhiteSpace(identity.Country) && identity.Country is not ("US" or "USA" or "United States"))
            return new AddressVerification(Name, false, null, null, null, $"Geocoder only covers US addresses (country={identity.Country}).");

        var oneLine = string.Join(", ", new[] { AddressMatcher.NumberWordsToDigits(identity.AddressLine), identity.City, identity.Region, identity.PostalCode }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var client = _factory.CreateClient(KybOptions.HttpClientName);
        var url = $"https://geocoding.geo.census.gov/geocoder/locations/onelineaddress?address={Uri.EscapeDataString(oneLine)}&benchmark=Public_AR_Current&format=json";
        using var doc = await client.GetFromJsonAsync<JsonDocument>(url, ct);
        var matches = doc?.RootElement.GetProperty("result").GetProperty("addressMatches");
        if (matches is null || matches.Value.GetArrayLength() == 0)
            return new AddressVerification(Name, false, null, null, null, "Address not found in the Census address ranges.");
        var m = matches.Value[0];
        var coords = m.GetProperty("coordinates");
        return new AddressVerification(Name, true, m.GetProperty("matchedAddress").GetString(),
            coords.GetProperty("y").GetDouble(), coords.GetProperty("x").GetDouble());
    }
}

/// <summary>Similarity between two free-text addresses, tolerant of abbreviations (St/Street, Ave/Avenue…).</summary>
public static class AddressMatcher
{
    private static readonly Dictionary<string, string> Abbreviations = new(StringComparer.Ordinal)
    {
        ["street"] = "st", ["avenue"] = "ave", ["road"] = "rd", ["boulevard"] = "blvd", ["drive"] = "dr",
        ["lane"] = "ln", ["suite"] = "ste", ["floor"] = "fl", ["building"] = "bldg", ["north"] = "n",
        ["south"] = "s", ["east"] = "e", ["west"] = "w", ["place"] = "pl", ["court"] = "ct", ["highway"] = "hwy",
        ["united states"] = "us", ["usa"] = "us", ["united kingdom"] = "gb", ["uk"] = "gb"
    };

    private static readonly Dictionary<string, string> NumberWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["one"] = "1", ["two"] = "2", ["three"] = "3", ["four"] = "4", ["five"] = "5",
        ["six"] = "6", ["seven"] = "7", ["eight"] = "8", ["nine"] = "9", ["ten"] = "10"
    };

    /// <summary>"One Apple Park Way" → "1 Apple Park Way" (geocoders need a numeric house number).</summary>
    public static string? NumberWordsToDigits(string? addressLine)
    {
        if (string.IsNullOrWhiteSpace(addressLine)) return addressLine;
        var parts = addressLine.Trim().Split(' ', 2);
        return parts.Length == 2 && NumberWords.TryGetValue(parts[0], out var digits) ? $"{digits} {parts[1]}" : addressLine;
    }

    public static double Similarity(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return 0;
        var ta = Canonical(NumberWordsToDigits(a)!);
        var tb = Canonical(NumberWordsToDigits(b)!);
        return Math.Round(NameMatcher.TokenSetRatio(ta, tb), 4);
    }

    private static string[] Canonical(string address) =>
        NameMatcher.Normalize(address).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => Abbreviations.GetValueOrDefault(t, t))
            .Where(t => t.Length > 1 || char.IsDigit(t[0]))
            .ToArray();
}
