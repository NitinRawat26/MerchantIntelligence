using System.Net.Http.Json;
using System.Text.Json;
using MerchantIntelligence.Kyb.Matching;
using MerchantIntelligence.MccValidation.Taxonomy;
using Microsoft.Extensions.Logging;

namespace MerchantIntelligence.Kyb.Registry;

/// <summary>A place-of-business record from a points-of-interest source (map / places API).</summary>
public sealed record PlaceRecord(
    string Source,
    string SourceId,
    string Name,
    string? Address,
    double? Latitude,
    double? Longitude,
    string? Category,
    string? Website,
    string? Phone,
    string? Status,
    Uri? SourceUrl,
    PlaceReputation? Reputation = null);

/// <summary>Crowd signals a places source publishes about a venue: how it is rated, how busy it is and how long it has been listed. Tenure and activity evidence for a small merchant, never proof of legitimacy.</summary>
public sealed record PlaceReputation(
    double? Rating,
    double? RatingScale,
    int? RatingCount,
    double? Popularity,
    DateOnly? ListedSince,
    bool? OpenNow);

public sealed record PlaceMatch(
    PlaceRecord Record,
    double NameScore,
    double AddressScore,
    double? DistanceMeters,
    double OverallScore);

public sealed record PlaceSourceResult(
    string Source,
    bool Succeeded,
    IReadOnlyList<PlaceMatch> Matches,
    string? Error = null);

public enum LocalPresenceStatus
{
    Confirmed,
    PartialMatch,
    NotFound,
    Inconclusive,
    NotChecked
}

/// <summary>
/// Evidence that a business operates at its declared location, from places / map data.
/// Complements registry verification for small merchants that hold no LEI and file nothing publicly.
/// </summary>
public sealed record LocalPresenceResult(
    LocalPresenceStatus Status,
    double ConfidencePercent,
    PlaceMatch? BestMatch,
    IReadOnlyList<PlaceSourceResult> Sources,
    string? Note = null,
    DigitalFootprint? Footprint = null);

/// <summary>
/// Registration facts about the merchant's own domains (website and contact e-mail), from RDAP. Gives a tenure signal
/// for merchants that have no website and therefore never reach the website-compliance scan.
/// </summary>
public sealed record DigitalFootprint(
    string? EmailDomain,
    bool EmailIsFreeMail,
    Compliance.DomainInfo? EmailDomainInfo,
    int? EmailDomainAgeMonths,
    string? WebsiteDomain,
    bool? EmailMatchesWebsite,
    IReadOnlyList<KybFlag> Flags);

/// <summary>Location the search is centred on; comes from the geocoded declared address.</summary>
public sealed record GeoPoint(double Latitude, double Longitude);

public interface ILocalPresenceProvider
{
    string Name { get; }
    bool IsEnabled { get; }
    Task<IReadOnlyList<PlaceRecord>> SearchAsync(BusinessIdentity identity, GeoPoint centre, int radiusMeters, CancellationToken ct);
}

public static class Geo
{
    public static double DistanceMeters(double lat1, double lon1, double lat2, double lon2)
    {
        const double r = 6_371_000;
        double ToRad(double d) => d * Math.PI / 180;
        var dLat = ToRad(lat2 - lat1);
        var dLon = ToRad(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(ToRad(lat1)) * Math.Cos(ToRad(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * r * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }
}

/// <summary>Fans out to every enabled places source around the geocoded address and scores the candidates.</summary>
public sealed class LocalPresenceService
{
    private readonly IReadOnlyList<ILocalPresenceProvider> _providers;
    private readonly NominatimGeocoder _geocoder;
    private readonly Compliance.RdapDomainLookup _rdap;
    private readonly KybOptions _options;
    private readonly ILogger<LocalPresenceService> _logger;

    public LocalPresenceService(IEnumerable<ILocalPresenceProvider> providers, NominatimGeocoder geocoder, Compliance.RdapDomainLookup rdap, KybOptions options, ILogger<LocalPresenceService> logger)
    {
        _providers = providers.ToList();
        _geocoder = geocoder;
        _rdap = rdap;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// RDAP facts for the contact e-mail domain, independent of the website scan. Free-mail addresses are recorded, not
    /// penalised heavily: most micro merchants use them. A lookup failure is reported as unknown, never as "new".
    /// </summary>
    public async Task<DigitalFootprint?> FootprintAsync(BusinessIdentity identity, CancellationToken ct)
    {
        var emailDomain = Compliance.RdapDomainLookup.DomainOf(identity.ContactEmail);
        var websiteDomain = Compliance.RdapDomainLookup.DomainOf(identity.WebsiteUrl);
        if (emailDomain is null) return null;

        var flags = new List<KybFlag>();
        var free = Compliance.RdapDomainLookup.IsFreeMail(emailDomain);
        Compliance.DomainInfo? info = null;
        int? ageMonths = null;
        bool? matches = websiteDomain is null ? null : websiteDomain.Equals(emailDomain, StringComparison.OrdinalIgnoreCase);

        if (free)
            flags.Add(new KybFlag("EMAIL_FREEMAIL", $"Contact e-mail uses a free-mail domain ({emailDomain}); no business domain tenure can be established from it.", RiskTier.Low));
        else
        {
            try { info = await _rdap.LookupAsync(emailDomain, ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested) { info = new Compliance.DomainInfo(emailDomain, null, null, null, Array.Empty<string>(), ex.Message); }

            if (info.Registered is { } reg)
            {
                var now = DateTimeOffset.UtcNow;
                ageMonths = Math.Max(0, (now.Year - reg.Year) * 12 + now.Month - reg.Month);
                if (ageMonths < 6)
                    flags.Add(new KybFlag("EMAIL_DOMAIN_NEW", $"Contact e-mail domain {emailDomain} was registered {ageMonths} month(s) ago ({reg:yyyy-MM-dd}); a very young domain is a common bust-out / impersonation marker.", RiskTier.Medium));
                else
                    flags.Add(new KybFlag("EMAIL_DOMAIN_TENURE", $"Contact e-mail domain {emailDomain} registered {reg:yyyy-MM-dd} ({ageMonths / 12} yr {ageMonths % 12} mo){(info.Registrar is null ? "" : $" via {info.Registrar}")}.", RiskTier.Low));
            }
            else
                flags.Add(new KybFlag("EMAIL_DOMAIN_UNRESOLVED", $"RDAP returned no registration date for {emailDomain}{(info.Error is null ? "" : $": {info.Error}")}. Domain tenure is unknown, not clear.", RiskTier.Low));

            if (matches == false)
                flags.Add(new KybFlag("EMAIL_DOMAIN_MISMATCH", $"Contact e-mail domain ({emailDomain}) differs from the website domain ({websiteDomain}).", RiskTier.Low));
        }
        return new DigitalFootprint(emailDomain, free, info, ageMonths, websiteDomain, matches, flags);
    }

    public bool IsEnabled => _options.LocalPresenceEnabled;

    /// <summary>
    /// Runs the check for an already-verified identity, reusing the verified address coordinates as the search centre.
    /// Never throws: source outages surface as an <see cref="LocalPresenceStatus.Inconclusive"/> result.
    /// </summary>
    public async Task<LocalPresenceResult> CheckAsync(BusinessVerificationResult verification, CancellationToken ct = default)
    {
        var a = verification.Address;
        var known = a is { Verified: true, Latitude: { } la, Longitude: { } lo } ? new GeoPoint(la, lo) : null;
        var footprintTask = FootprintAsync(verification.Input, ct);
        LocalPresenceResult result;
        try
        {
            result = await CheckAsync(verification.Input, known, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Local presence check failed");
            result = new LocalPresenceResult(LocalPresenceStatus.Inconclusive, 0, null, Array.Empty<PlaceSourceResult>(), ex.Message);
        }
        return result with { Footprint = await footprintTask };
    }

    public async Task<LocalPresenceResult> CheckAsync(BusinessIdentity identity, GeoPoint? knownLocation, CancellationToken ct = default)
    {
        var hasStreet = !string.IsNullOrWhiteSpace(identity.AddressLine);
        var hasLocality = !string.IsNullOrWhiteSpace(identity.City) || !string.IsNullOrWhiteSpace(identity.PostalCode);
        if (!hasStreet && !hasLocality)
            return new LocalPresenceResult(LocalPresenceStatus.NotChecked, 0, null, Array.Empty<PlaceSourceResult>(), "No address or locality supplied; places sources need a location to search around.");

        var centre = knownLocation;
        if (centre is null)
        {
            try { centre = await _geocoder.GeocodeAsync(identity, ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Nominatim geocoding failed");
                return new LocalPresenceResult(LocalPresenceStatus.Inconclusive, 0, null, Array.Empty<PlaceSourceResult>(), $"Address could not be geocoded: {ex.Message}");
            }
        }
        if (centre is null)
            return new LocalPresenceResult(LocalPresenceStatus.Inconclusive, 0, null, Array.Empty<PlaceSourceResult>(), "Declared address could not be geocoded (OpenStreetMap Nominatim found no match).");

        var radius = hasStreet ? _options.LocalPresenceRadiusMeters : _options.LocalPresenceLocalityRadiusMeters;
        var tasks = _providers.Where(p => p.IsEnabled).Select(async p =>
        {
            try
            {
                var records = await p.SearchAsync(identity, centre, radius, ct);
                var matches = records.Select(r => Score(identity, r, centre, radius))
                    .Where(m => m.NameScore >= 0.5)
                    .OrderByDescending(m => m.OverallScore)
                    .Take(_options.MaxResultsPerSource)
                    .ToList();
                return new PlaceSourceResult(p.Name, true, matches);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Places provider {Provider} failed", p.Name);
                return new PlaceSourceResult(p.Name, false, Array.Empty<PlaceMatch>(), ex.Message);
            }
        }).ToList();
        var disabled = _providers.Where(p => !p.IsEnabled)
            .Select(p => new PlaceSourceResult(p.Name, false, Array.Empty<PlaceMatch>(), "Not configured (API key missing)."));
        var sources = (await Task.WhenAll(tasks)).Concat(disabled).ToList();

        var best = sources.SelectMany(s => s.Matches).OrderByDescending(m => m.OverallScore).FirstOrDefault();
        var anySucceeded = sources.Any(s => s.Succeeded);
        var status = !anySucceeded ? LocalPresenceStatus.Inconclusive
            : best is null ? LocalPresenceStatus.NotFound
            : best.OverallScore >= 0.8 ? LocalPresenceStatus.Confirmed
            : best.OverallScore >= 0.6 ? LocalPresenceStatus.PartialMatch
            : LocalPresenceStatus.NotFound;
        var confidence = best is null ? 0 : Math.Round(best.OverallScore * 100, 1);
        var note = status == LocalPresenceStatus.NotFound && sources.Count(s => s.Succeeded) == 1 && sources.Any(s => s.Source.StartsWith("OpenStreetMap", StringComparison.Ordinal) && s.Succeeded)
            ? "Only OpenStreetMap was searched; its coverage of small businesses is partial, so absence is weak evidence. Configure a Foursquare or Google Places key for stronger coverage."
            : null;
        return new LocalPresenceResult(status, confidence, best, sources, note);
    }

    internal static PlaceMatch Score(BusinessIdentity identity, PlaceRecord record, GeoPoint centre, int radiusMeters)
    {
        // NameMatcher accepts initials ("E" ≈ "Eagle"), which suits person names but not map labels: ignore very short POI names.
        var usable = NameMatcher.Normalize(record.Name).Replace(" ", "").Length >= 3;
        var nameScore = usable ? NameMatcher.Similarity(identity.LegalName, record.Name) : 0;
        if (usable && !string.IsNullOrWhiteSpace(identity.TradingName))
            nameScore = Math.Max(nameScore, NameMatcher.Similarity(identity.TradingName, record.Name));

        var addressScore = AddressMatcher.Similarity(identity.FullAddress, record.Address);
        double? distance = record is { Latitude: { } la, Longitude: { } lo } ? Geo.DistanceMeters(centre.Latitude, centre.Longitude, la, lo) : null;

        // Proximity is the primary location signal (POI addresses are often abbreviated or missing); address text is a fallback.
        var locationScore = distance is { } d
            ? Math.Clamp(1 - d / radiusMeters, 0, 1)
            : addressScore;
        locationScore = Math.Max(locationScore, addressScore * 0.9);

        var overall = 0.65 * nameScore + 0.35 * locationScore;
        if (record.Status is { } st && st.Contains("closed", StringComparison.OrdinalIgnoreCase)) overall *= 0.5;
        return new PlaceMatch(record, Math.Round(nameScore, 4), Math.Round(addressScore, 4), distance is null ? null : Math.Round(distance.Value), Math.Round(overall, 4));
    }
}

/// <summary>OpenStreetMap Nominatim. Free, no key, worldwide; usage policy: identify yourself, ≤1 request/s.</summary>
public sealed class NominatimGeocoder
{
    private readonly IHttpClientFactory _factory;

    public NominatimGeocoder(IHttpClientFactory factory) => _factory = factory;

    public async Task<GeoPoint?> GeocodeAsync(BusinessIdentity identity, CancellationToken ct)
    {
        var client = _factory.CreateClient(KybOptions.HttpClientName);
        var query = string.Join(", ", new[] { AddressMatcher.NumberWordsToDigits(identity.AddressLine), identity.City, identity.Region, identity.PostalCode, identity.Country }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        var url = $"https://nominatim.openstreetmap.org/search?q={Uri.EscapeDataString(query)}&format=jsonv2&limit=1";
        using var doc = await client.GetFromJsonAsync<JsonDocument>(url, ct);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0) return null;
        var hit = doc.RootElement[0];
        if (double.TryParse(hit.GetProperty("lat").GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lat)
            && double.TryParse(hit.GetProperty("lon").GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lon))
            return new GeoPoint(lat, lon);
        return null;
    }
}

/// <summary>OpenStreetMap Overpass API: named points of interest around the address. Free, no key.</summary>
public sealed class OsmLocalPresenceProvider : ILocalPresenceProvider
{
    private static readonly string[] CategoryTags = { "amenity", "shop", "cuisine", "office", "craft", "healthcare", "tourism", "leisure" };
    private readonly IHttpClientFactory _factory;

    public OsmLocalPresenceProvider(IHttpClientFactory factory) => _factory = factory;

    private static readonly string[] Endpoints = { "https://overpass-api.de/api/interpreter", "https://overpass.kumi.systems/api/interpreter" };

    public string Name => "OpenStreetMap (Overpass)";
    public bool IsEnabled => true;

    /// <summary>Public Overpass instances rate-limit per IP (429) and time out under load (504); fall back to the next mirror.</summary>
    private static async Task<JsonDocument> QueryAsync(HttpClient client, string ql, CancellationToken ct)
    {
        Exception? last = null;
        foreach (var endpoint in Endpoints)
        {
            try
            {
                using var response = await client.GetAsync($"{endpoint}?data={Uri.EscapeDataString(ql)}", ct);
                response.EnsureSuccessStatusCode();
                return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                last = ex;
            }
        }
        throw last!;
    }

    public async Task<IReadOnlyList<PlaceRecord>> SearchAsync(BusinessIdentity identity, GeoPoint centre, int radiusMeters, CancellationToken ct)
    {
        var client = _factory.CreateClient(KybOptions.HttpClientName);
        var lat = centre.Latitude.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var lon = centre.Longitude.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var ql = $"[out:json][timeout:15];nwr(around:{radiusMeters},{lat},{lon})[name];out center tags 80;";
        using var doc = await QueryAsync(client, ql, ct);
        var list = new List<PlaceRecord>();
        if (!doc.RootElement.TryGetProperty("elements", out var elements)) return list;
        foreach (var e in elements.EnumerateArray())
        {
            if (!e.TryGetProperty("tags", out var tags) || !tags.TryGetProperty("name", out var n)) continue;
            var type = e.GetProperty("type").GetString();
            var id = e.GetProperty("id").GetRawText();
            double? la = null, lo = null;
            if (e.TryGetProperty("lat", out var latEl)) { la = latEl.GetDouble(); lo = e.GetProperty("lon").GetDouble(); }
            else if (e.TryGetProperty("center", out var c)) { la = c.GetProperty("lat").GetDouble(); lo = c.GetProperty("lon").GetDouble(); }
            var address = string.Join(", ", new[] { "addr:housenumber", "addr:street", "addr:city", "addr:state", "addr:postcode" }
                .Select(k => Tag(tags, k)).Where(v => v is not null));
            var category = string.Join(" / ", CategoryTags.Select(k => Tag(tags, k)).Where(v => v is not null));
            var disused = tags.EnumerateObject().Any(p => p.Name.StartsWith("disused:", StringComparison.Ordinal) || p.Name.StartsWith("was:", StringComparison.Ordinal));
            list.Add(new PlaceRecord(Name, $"{type}/{id}", n.GetString() ?? string.Empty,
                address.Length == 0 ? null : address, la, lo,
                category.Length == 0 ? null : category,
                Tag(tags, "website") ?? Tag(tags, "contact:website"),
                Tag(tags, "phone") ?? Tag(tags, "contact:phone"),
                disused ? "closed" : Tag(tags, "opening_hours") is null ? null : "open",
                new Uri($"https://www.openstreetmap.org/{type}/{id}")));
        }
        return list;
    }

    private static string? Tag(JsonElement tags, string key) =>
        tags.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString() : null;
}

/// <summary>Foursquare Places API. Optional; needs a service key (free developer tier).</summary>
public sealed class FoursquareLocalPresenceProvider : ILocalPresenceProvider
{
    private readonly IHttpClientFactory _factory;
    private readonly KybOptions _options;

    public FoursquareLocalPresenceProvider(IHttpClientFactory factory, KybOptions options)
    {
        _factory = factory;
        _options = options;
    }

    public string Name => "Foursquare Places";
    public bool IsEnabled => !string.IsNullOrWhiteSpace(_options.FoursquareApiKey);

    public async Task<IReadOnlyList<PlaceRecord>> SearchAsync(BusinessIdentity identity, GeoPoint centre, int radiusMeters, CancellationToken ct)
    {
        var client = _factory.CreateClient(KybOptions.HttpClientName);
        var ll = $"{centre.Latitude.ToString(System.Globalization.CultureInfo.InvariantCulture)},{centre.Longitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        var name = identity.TradingName ?? identity.LegalName;
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://places-api.foursquare.com/places/search?query={Uri.EscapeDataString(name)}&ll={ll}&radius={Math.Max(radiusMeters, 500)}&limit=10" +
            "&fields=fsq_place_id,name,latitude,longitude,location,categories,website,tel,closed_bucket,rating,popularity,stats,hours,date_created");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _options.FoursquareApiKey);
        request.Headers.TryAddWithoutValidation("X-Places-Api-Version", "2025-06-17");
        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var list = new List<PlaceRecord>();
        if (!doc.RootElement.TryGetProperty("results", out var results)) return list;
        foreach (var r in results.EnumerateArray())
        {
            var id = Str(r, "fsq_place_id") ?? Str(r, "fsq_id") ?? string.Empty;
            double? la = null, lo = null;
            if (r.TryGetProperty("latitude", out var latEl) && latEl.ValueKind == JsonValueKind.Number) { la = latEl.GetDouble(); lo = r.GetProperty("longitude").GetDouble(); }
            else if (r.TryGetProperty("geocodes", out var g) && g.TryGetProperty("main", out var m)) { la = m.GetProperty("latitude").GetDouble(); lo = m.GetProperty("longitude").GetDouble(); }
            string? address = null;
            if (r.TryGetProperty("location", out var loc) && loc.ValueKind == JsonValueKind.Object)
                address = Str(loc, "formatted_address") ?? string.Join(", ", new[] { "address", "locality", "region", "postcode" }.Select(k => Str(loc, k)).Where(v => v is not null));
            string? category = null;
            if (r.TryGetProperty("categories", out var cats) && cats.ValueKind == JsonValueKind.Array)
                category = string.Join(" / ", cats.EnumerateArray().Select(c => Str(c, "name")).Where(v => v is not null));
            list.Add(new PlaceRecord(Name, id, Str(r, "name") ?? string.Empty, string.IsNullOrWhiteSpace(address) ? null : address, la, lo,
                string.IsNullOrWhiteSpace(category) ? null : category, Str(r, "website"), Str(r, "tel"), Str(r, "closed_bucket"),
                id.Length == 0 ? null : new Uri($"https://foursquare.com/v/{id}"), Reputation(r)));
        }
        return list;
    }

    /// <summary>Foursquare rates 0–10; popularity is a 0–1 foot-traffic percentile; stats carry rating/tip counts; date_created is when the venue was first listed.</summary>
    internal static PlaceReputation? Reputation(JsonElement r)
    {
        double? rating = Num(r, "rating"), popularity = Num(r, "popularity");
        int? count = null;
        if (r.TryGetProperty("stats", out var stats) && stats.ValueKind == JsonValueKind.Object)
            count = Num(stats, "total_ratings") is { } tr ? (int)tr : Num(stats, "total_tips") is { } tt ? (int)tt : null;
        DateOnly? since = Str(r, "date_created") is { } dc && DateOnly.TryParse(dc[..Math.Min(10, dc.Length)], System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
        bool? openNow = r.TryGetProperty("hours", out var hours) && hours.TryGetProperty("open_now", out var on) && on.ValueKind is JsonValueKind.True or JsonValueKind.False ? on.GetBoolean() : null;
        return rating is null && popularity is null && count is null && since is null && openNow is null ? null
            : new PlaceReputation(rating, 10, count, popularity, since, openNow);
    }

    private static string? Str(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString() : null;

    private static double? Num(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
}

/// <summary>Google Places API (New) text search. Optional; needs an API key (monthly free usage allowance).</summary>
public sealed class GooglePlacesLocalPresenceProvider : ILocalPresenceProvider
{
    private readonly IHttpClientFactory _factory;
    private readonly KybOptions _options;

    public GooglePlacesLocalPresenceProvider(IHttpClientFactory factory, KybOptions options)
    {
        _factory = factory;
        _options = options;
    }

    public string Name => "Google Places";
    public bool IsEnabled => !string.IsNullOrWhiteSpace(_options.GooglePlacesApiKey);

    public async Task<IReadOnlyList<PlaceRecord>> SearchAsync(BusinessIdentity identity, GeoPoint centre, int radiusMeters, CancellationToken ct)
    {
        var client = _factory.CreateClient(KybOptions.HttpClientName);
        var name = identity.TradingName ?? identity.LegalName;
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://places.googleapis.com/v1/places:searchText")
        {
            Content = JsonContent.Create(new
            {
                textQuery = $"{name} {identity.City}".Trim(),
                maxResultCount = 10,
                locationBias = new { circle = new { center = new { latitude = centre.Latitude, longitude = centre.Longitude }, radius = (double)Math.Max(radiusMeters, 500) } }
            })
        };
        request.Headers.TryAddWithoutValidation("X-Goog-Api-Key", _options.GooglePlacesApiKey);
        request.Headers.TryAddWithoutValidation("X-Goog-FieldMask",
            "places.id,places.displayName,places.formattedAddress,places.location,places.primaryTypeDisplayName,places.websiteUri,places.nationalPhoneNumber,places.businessStatus,places.googleMapsUri,places.rating,places.userRatingCount,places.currentOpeningHours.openNow");
        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var list = new List<PlaceRecord>();
        if (!doc.RootElement.TryGetProperty("places", out var places)) return list;
        foreach (var p in places.EnumerateArray())
        {
            double? la = null, lo = null;
            if (p.TryGetProperty("location", out var loc)) { la = loc.GetProperty("latitude").GetDouble(); lo = loc.GetProperty("longitude").GetDouble(); }
            var display = p.TryGetProperty("displayName", out var dn) ? Str(dn, "text") : null;
            var type = p.TryGetProperty("primaryTypeDisplayName", out var pt) ? Str(pt, "text") : null;
            list.Add(new PlaceRecord(Name, Str(p, "id") ?? string.Empty, display ?? string.Empty, Str(p, "formattedAddress"), la, lo, type,
                Str(p, "websiteUri"), Str(p, "nationalPhoneNumber"),
                Str(p, "businessStatus") is { } bs ? bs.Replace("_", " ").ToLowerInvariant() : null,
                Str(p, "googleMapsUri") is { } u && Uri.TryCreate(u, UriKind.Absolute, out var uri) ? uri : null,
                new PlaceReputation(Num(p, "rating"), 5, Num(p, "userRatingCount") is { } rc ? (int)rc : null, null, null,
                    p.TryGetProperty("currentOpeningHours", out var oh) && oh.TryGetProperty("openNow", out var on) && on.ValueKind is JsonValueKind.True or JsonValueKind.False ? on.GetBoolean() : null)));
        }
        return list;
    }

    private static string? Str(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString() : null;

    private static double? Num(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
}
