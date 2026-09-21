using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace MerchantIntelligence.Kyb.Compliance;

/// <summary>
/// Domain registration facts from RDAP (rdap.org bootstrap, free and keyless). Shared by the website scan and the
/// digital-footprint check so a merchant with no website still gets a tenure signal from its e-mail domain.
/// </summary>
public sealed class RdapDomainLookup
{
    private static readonly HashSet<string> FreeMailDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "gmail.com", "googlemail.com", "yahoo.com", "yahoo.co.uk", "ymail.com", "hotmail.com", "hotmail.co.uk", "outlook.com", "live.com", "msn.com",
        "aol.com", "icloud.com", "me.com", "mac.com", "protonmail.com", "proton.me", "mail.com", "gmx.com", "gmx.de", "zoho.com", "yandex.com", "yandex.ru"
    };

    private readonly IHttpClientFactory _factory;
    private readonly ILogger<RdapDomainLookup> _logger;

    public RdapDomainLookup(IHttpClientFactory factory, ILogger<RdapDomainLookup> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public static bool IsFreeMail(string domain) => FreeMailDomains.Contains(domain);

    /// <summary>Registrable domain of an e-mail address or host name ("www." stripped); null when the input has none.</summary>
    public static string? DomainOf(string? emailOrHost)
    {
        if (string.IsNullOrWhiteSpace(emailOrHost)) return null;
        var s = emailOrHost.Trim();
        var at = s.LastIndexOf('@');
        if (at >= 0) s = s[(at + 1)..];
        if (Uri.TryCreate(s, UriKind.Absolute, out var uri)) s = uri.Host;
        if (s.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) s = s[4..];
        s = s.TrimEnd('.').ToLowerInvariant();
        return s.Length == 0 || !s.Contains('.') || Uri.CheckHostName(s) != UriHostNameType.Dns ? null : s;
    }

    public async Task<DomainInfo> LookupAsync(string host, CancellationToken ct)
    {
        var domain = host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
        if (Uri.CheckHostName(domain) != UriHostNameType.Dns) return new DomainInfo(domain, null, null, null, Array.Empty<string>(), "Not a DNS host name.");
        try
        {
            var client = _factory.CreateClient(Registry.KybOptions.HttpClientName);
            using var doc = await client.GetFromJsonAsync<JsonDocument>($"https://rdap.org/domain/{domain}", ct);
            if (doc is null) return new DomainInfo(domain, null, null, null, Array.Empty<string>(), "Empty RDAP response.");
            DateTimeOffset? registered = null, expires = null;
            if (doc.RootElement.TryGetProperty("events", out var events))
            {
                foreach (var e in events.EnumerateArray())
                {
                    var action = e.GetProperty("eventAction").GetString();
                    if (!DateTimeOffset.TryParse(e.GetProperty("eventDate").GetString(), out var date)) continue;
                    if (action == "registration") registered = date;
                    else if (action == "expiration") expires = date;
                }
            }
            string? registrar = null;
            if (doc.RootElement.TryGetProperty("entities", out var entities))
            {
                foreach (var ent in entities.EnumerateArray())
                {
                    if (!ent.TryGetProperty("roles", out var roles) || !roles.EnumerateArray().Any(r => r.GetString() == "registrar")) continue;
                    if (ent.TryGetProperty("vcardArray", out var vcard) && vcard.ValueKind == JsonValueKind.Array && vcard.GetArrayLength() > 1)
                    {
                        foreach (var prop in vcard[1].EnumerateArray())
                        {
                            if (prop.GetArrayLength() > 3 && prop[0].GetString() == "fn") { registrar = prop[3].GetString(); break; }
                        }
                    }
                    break;
                }
            }
            var statuses = doc.RootElement.TryGetProperty("status", out var st)
                ? st.EnumerateArray().Select(s => s.GetString() ?? string.Empty).ToList()
                : new List<string>();
            return new DomainInfo(domain, registered, expires, registrar, statuses, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogInformation(ex, "RDAP lookup for {Domain} failed", domain);
            return new DomainInfo(domain, null, null, null, Array.Empty<string>(), $"RDAP lookup failed: {ex.Message}");
        }
    }
}
