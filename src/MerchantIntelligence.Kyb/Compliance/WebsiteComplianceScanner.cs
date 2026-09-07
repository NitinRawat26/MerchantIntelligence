using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using MerchantIntelligence.Kyb.Prohibited;
using MerchantIntelligence.MccValidation.Taxonomy;
using MerchantIntelligence.MccValidation.Web;
using Microsoft.Extensions.Logging;

namespace MerchantIntelligence.Kyb.Compliance;

public enum CheckStatus { Pass, Warn, Fail, Skipped }

public sealed record ComplianceCheck(string Code, string Title, CheckStatus Status, string Detail, RiskTier Severity, Uri? Evidence = null);

public sealed record DomainInfo(string Domain, DateTimeOffset? Registered, DateTimeOffset? Expires, string? Registrar, IReadOnlyList<string> Statuses, string? Error);

public sealed record WebsiteComplianceResult(
    Uri WebsiteUrl,
    bool Reachable,
    int Score,
    string Grade,
    IReadOnlyList<ComplianceCheck> Checks,
    DomainInfo? Domain,
    ProhibitedBusinessResult ProhibitedBusiness,
    IReadOnlyList<Uri> PagesAnalyzed);

/// <summary>
/// Checks a merchant website against the disclosures Visa/Mastercard require of e-commerce merchants
/// (Visa Core Rules 5.x "Website requirements"; Mastercard Rules 5.11), plus hygiene signals such as
/// TLS, domain age (RDAP) and prohibited-content keywords.
/// </summary>
public sealed class WebsiteComplianceScanner
{
    private static readonly (Regex Kind, string[] Paths)[] WellKnownPolicyPaths =
    {
        (new Regex("privacy", RegexOptions.IgnoreCase), new[] { "/privacy", "/privacy-policy", "/policies/privacy-policy", "/pages/privacy-policy", "/legal/privacy" }),
        (new Regex("terms|conditions|tos", RegexOptions.IgnoreCase), new[] { "/terms", "/terms-of-service", "/terms-and-conditions", "/policies/terms-of-service", "/pages/terms-of-service", "/legal/terms" }),
        (new Regex("refund|return|cancel", RegexOptions.IgnoreCase), new[] { "/refund-policy", "/returns", "/policies/refund-policy", "/pages/returns", "/return-policy" }),
        (new Regex("shipping|delivery", RegexOptions.IgnoreCase), new[] { "/shipping", "/shipping-policy", "/policies/shipping-policy", "/pages/shipping", "/delivery" }),
    };

    private static readonly Regex PolicyLink = new(
        @"(privacy|terms|conditions|tos|refund|return|cancel|shipping|delivery|contact|about|faq|legal|checkout|cart|basket)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex Email = new(@"\b[a-z0-9._%+-]+@[a-z0-9.-]+\.[a-z]{2,}\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Phone = new(@"(\+?\d[\d\s().-]{8,}\d)", RegexOptions.Compiled);
    private static readonly Regex StreetAddress = new(@"\b\d{1,6}\s+[a-z0-9.\- ]{2,40}\b(street|st|avenue|ave|road|rd|boulevard|blvd|lane|ln|drive|dr|way|suite|ste|floor|plaza|square|sq)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Currency = new(@"(\$|€|£|¥|₹|USD|EUR|GBP|CAD|AUD|INR)\s?\d", RegexOptions.Compiled);
    private static readonly Regex Price = new(@"\d+[.,]\d{2}", RegexOptions.Compiled);
    private static readonly Regex CardLogos = new(@"\b(visa|mastercard|master card|amex|american express|discover|paypal|stripe|apple pay|google pay|secure checkout|ssl secured|pci)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PlaceholderText = new(@"\b(lorem ipsum|coming soon|under construction|this domain is for sale|parked (free|domain)|default web site page|welcome to nginx|it works!)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex RefundTerms = new(@"\b(refund|return|money[- ]back|cancel(lation)?|exchange)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DeliveryTerms = new(@"\b(shipping|delivery|dispatch|ships within|business days|tracking)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PrivacyTerms = new(@"\b(personal (data|information)|cookies?|gdpr|ccpa|data protection|we collect)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ExportRestriction = new(@"\b(export restrictions?|we (do not|don't) ship to|countries we ship|restricted countries|embargo)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly IHttpClientFactory _factory;
    private readonly ProhibitedBusinessDetector _prohibited;
    private readonly ILogger<WebsiteComplianceScanner> _logger;

    public WebsiteComplianceScanner(IHttpClientFactory factory, ProhibitedBusinessDetector prohibited, ILogger<WebsiteComplianceScanner> logger)
    {
        _factory = factory;
        _prohibited = prohibited;
        _logger = logger;
    }

    public async Task<WebsiteComplianceResult> ScanAsync(Uri url, string? businessDescription = null, int? declaredMcc = null, string? declaredLegalName = null, CancellationToken ct = default)
    {
        var client = _factory.CreateClient(WebsiteContentFetcher.HttpClientName);
        var checks = new List<ComplianceCheck>();
        var pages = new Dictionary<Uri, ExtractedPage>();

        var httpsUrl = new UriBuilder(url) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri;
        var (home, homeUrl, tlsError) = await FetchWithTlsCheckAsync(client, httpsUrl, url, ct);
        var domainTask = LookupDomainAsync(url.Host, ct);

        if (home is null)
        {
            checks.Add(new ComplianceCheck("SITE_UNREACHABLE", "Website reachable", CheckStatus.Fail, $"Could not load {url}: {tlsError ?? "no response"}.", RiskTier.High));
            var domainDown = await domainTask;
            AddDomainChecks(checks, domainDown);
            return Build(url, false, checks, domainDown, _prohibited.Analyze(null, businessDescription, declaredMcc), Array.Empty<Uri>());
        }

        pages[homeUrl] = home;
        checks.Add(tlsError is null
            ? new ComplianceCheck("TLS", "HTTPS / TLS", CheckStatus.Pass, "Site served over HTTPS.", RiskTier.High)
            : new ComplianceCheck("TLS", "HTTPS / TLS", CheckStatus.Fail, $"HTTPS failed ({tlsError}); content only available over plain HTTP.", RiskTier.High));

        var policyLinks = home.InternalLinks
            .Where(l => PolicyLink.IsMatch(l.AbsolutePath) || PolicyLink.IsMatch(l.Query))
            .Distinct()
            .OrderBy(l => l.AbsolutePath.Length)
            .Take(10)
            .ToList();
        foreach (var link in policyLinks)
        {
            if (pages.ContainsKey(link)) continue;
            var page = await FetchPageAsync(client, link, ct);
            if (page is not null) pages[link] = page;
        }

        // JS-rendered footers hide policy links from a static crawl; probe the conventional paths
        // (incl. Shopify's /policies/*) for any policy kind we have not yet found.
        foreach (var (kind, paths) in WellKnownPolicyPaths)
        {
            if (pages.Keys.Any(u => kind.IsMatch(u.AbsolutePath))) continue;
            foreach (var path in paths)
            {
                var candidate = new Uri(homeUrl, path);
                if (pages.ContainsKey(candidate)) continue;
                var page = await FetchPageAsync(client, candidate, ct);
                if (page is null || page.BodyText.Length < 200 || page.BodyText == home.BodyText) continue;
                pages[candidate] = page;
                break;
            }
        }
        var discoveredPaths = pages.Keys.Where(u => u != homeUrl).Select(u => u.AbsolutePath).ToList();

        var allText = string.Join(" ", pages.Values.Select(p => p.ToClassifierText()));
        var linkPaths = home.InternalLinks.Select(l => l.AbsolutePath + l.Query).Concat(discoveredPaths).Distinct().ToList();

        // --- Card-brand mandated disclosures ---
        AddPolicyCheck(checks, pages, linkPaths, "PRIVACY_POLICY", "Privacy policy", @"privacy", PrivacyTerms, RiskTier.Medium,
            "Card brands and GDPR/CCPA require a privacy policy describing data collection.");
        AddPolicyCheck(checks, pages, linkPaths, "TERMS_CONDITIONS", "Terms & conditions", @"terms|conditions|tos|legal", null, RiskTier.Medium,
            "Terms of sale must be presented to the cardholder before purchase.");
        AddPolicyCheck(checks, pages, linkPaths, "REFUND_POLICY", "Refund / return / cancellation policy", @"refund|return|cancel", RefundTerms, RiskTier.High,
            "Visa/Mastercard require a clearly disclosed refund and cancellation policy; missing policy is a top chargeback driver.");
        AddPolicyCheck(checks, pages, linkPaths, "DELIVERY_POLICY", "Shipping / delivery policy", @"shipping|delivery", DeliveryTerms, RiskTier.Medium,
            "Delivery timeframes must be disclosed for physical goods.");

        var hasEmail = Email.IsMatch(allText) || home.InternalLinks.Count > 0 && allText.Contains("@", StringComparison.Ordinal);
        var hasPhone = Phone.IsMatch(allText);
        var hasAddress = StreetAddress.IsMatch(allText);
        var contactCount = new[] { hasEmail, hasPhone, hasAddress }.Count(b => b);
        checks.Add(new ComplianceCheck("CUSTOMER_SERVICE_CONTACT", "Customer service contact details",
            contactCount >= 2 ? CheckStatus.Pass : contactCount == 1 ? CheckStatus.Warn : CheckStatus.Fail,
            $"Email: {(hasEmail ? "yes" : "no")}, phone: {(hasPhone ? "yes" : "no")}, street address: {(hasAddress ? "yes" : "no")}. Card brands require an email or phone and a physical address.",
            RiskTier.High));

        var currencyHit = Currency.IsMatch(allText);
        var priceHit = Price.IsMatch(allText);
        checks.Add(new ComplianceCheck("CURRENCY_DISCLOSURE", "Prices and transaction currency",
            currencyHit ? CheckStatus.Pass : priceHit ? CheckStatus.Warn : CheckStatus.Skipped,
            currencyHit ? "Prices shown with a currency symbol/code." : priceHit ? "Prices found but no currency indicator." : "No prices found on scanned pages (may be a lead-gen or service site).",
            RiskTier.Low));

        checks.Add(new ComplianceCheck("CARD_ACCEPTANCE_MARKS", "Card acceptance marks / secure checkout indicators",
            CardLogos.IsMatch(allText) ? CheckStatus.Pass : CheckStatus.Warn,
            CardLogos.IsMatch(allText) ? "Payment brand or secure-checkout text present." : "No payment brand marks or secure-checkout messaging detected.",
            RiskTier.Low));

        var checkoutLinks = home.InternalLinks.Where(l => Regex.IsMatch(l.AbsolutePath, "checkout|cart|basket|buy|order|shop|pricing|subscribe", RegexOptions.IgnoreCase)).ToList();
        checks.Add(new ComplianceCheck("CHECKOUT_PRESENT", "Purchase flow discoverable",
            checkoutLinks.Count > 0 ? CheckStatus.Pass : CheckStatus.Warn,
            checkoutLinks.Count > 0 ? $"Found {checkoutLinks.Count} shop/checkout link(s)." : "No cart/checkout/pricing links found; verify how customers actually pay (possible hidden or off-site checkout).",
            RiskTier.Medium, checkoutLinks.FirstOrDefault()));

        checks.Add(new ComplianceCheck("EXPORT_RESTRICTIONS", "Export / shipping restrictions disclosed",
            ExportRestriction.IsMatch(allText) ? CheckStatus.Pass : CheckStatus.Skipped,
            ExportRestriction.IsMatch(allText) ? "Country/export restrictions mentioned." : "No export restriction statement found (required only if the merchant restricts destinations).",
            RiskTier.Low));

        if (!string.IsNullOrWhiteSpace(declaredLegalName))
        {
            var tokens = Matching.NameMatcher.Tokens(declaredLegalName);
            var present = tokens.Count > 0 && tokens.Count(t => allText.Contains(t, StringComparison.OrdinalIgnoreCase)) >= Math.Ceiling(tokens.Count / 2.0);
            checks.Add(new ComplianceCheck("LEGAL_NAME_DISCLOSED", "Legal entity name shown on site",
                present ? CheckStatus.Pass : CheckStatus.Warn,
                present ? $"'{declaredLegalName}' appears on the site." : $"Declared legal name '{declaredLegalName}' not found on scanned pages; cardholders may not recognise the billing descriptor.",
                RiskTier.Medium));
        }

        // --- Hygiene ---
        var placeholder = PlaceholderText.Match(allText);
        checks.Add(new ComplianceCheck("PLACEHOLDER_CONTENT", "Site content is live (not placeholder)",
            placeholder.Success ? CheckStatus.Fail : home.BodyText.Length < 300 ? CheckStatus.Warn : CheckStatus.Pass,
            placeholder.Success ? $"Placeholder text detected: '{placeholder.Value}'." : home.BodyText.Length < 300 ? $"Very thin homepage ({home.BodyText.Length} chars of text)." : $"Homepage has {home.BodyText.Length} chars of text across {pages.Count} page(s).",
            RiskTier.Medium));

        var domain = await domainTask;
        AddDomainChecks(checks, domain);

        // Legal boilerplate (terms, privacy) routinely mentions lotteries, firearms, etc.; classify commercial pages only.
        var commercialText = string.Join(" ", pages
            .Where(p => p.Key == homeUrl || !Regex.IsMatch(p.Key.AbsolutePath, "privacy|terms|conditions|tos|legal", RegexOptions.IgnoreCase))
            .Select(p => p.Value.ToClassifierText()));
        var prohibited = _prohibited.Analyze(commercialText, businessDescription, declaredMcc);
        foreach (var flag in prohibited.Flags)
            checks.Add(new ComplianceCheck(flag.Code, "Prohibited / restricted content", flag.Severity == RiskTier.High ? CheckStatus.Fail : CheckStatus.Warn, flag.Message, flag.Severity));
        if (prohibited.Flags.Count == 0)
            checks.Add(new ComplianceCheck("PROHIBITED_CONTENT", "Prohibited / restricted content", CheckStatus.Pass, "No prohibited or restricted business keywords detected.", RiskTier.High));

        return Build(url, true, checks, domain, prohibited, pages.Keys.ToList());
    }

    private static void AddPolicyCheck(List<ComplianceCheck> checks, Dictionary<Uri, ExtractedPage> pages, List<string> linkPaths,
        string code, string title, string linkPattern, Regex? contentPattern, RiskTier severity, string why)
    {
        var regex = new Regex(linkPattern, RegexOptions.IgnoreCase);
        var page = pages.FirstOrDefault(p => regex.IsMatch(p.Key.AbsolutePath + p.Key.Query) && p.Key != pages.Keys.First());
        if (page.Value is not null)
        {
            var substantive = page.Value.BodyText.Length > 400 && (contentPattern is null || contentPattern.Matches(page.Value.BodyText).Count >= 2);
            checks.Add(new ComplianceCheck(code, title, substantive ? CheckStatus.Pass : CheckStatus.Warn,
                substantive ? $"Dedicated page found ({page.Value.BodyText.Length} chars)." : $"Page found but looks thin or generic ({page.Value.BodyText.Length} chars). {why}",
                severity, page.Key));
            return;
        }
        var linkOnly = linkPaths.Any(l => regex.IsMatch(l));
        var inline = contentPattern is not null && pages.Values.Any(p => contentPattern.Matches(p.BodyText).Count >= 3);
        if (linkOnly)
            checks.Add(new ComplianceCheck(code, title, CheckStatus.Warn, $"Link present but the page could not be fetched. {why}", severity));
        else if (inline)
            checks.Add(new ComplianceCheck(code, title, CheckStatus.Warn, $"No dedicated page, but related wording appears inline. {why}", severity));
        else
            checks.Add(new ComplianceCheck(code, title, CheckStatus.Fail, $"Not found. {why}", severity));
    }

    private static void AddDomainChecks(List<ComplianceCheck> checks, DomainInfo? domain)
    {
        if (domain is null || domain.Error is not null)
        {
            checks.Add(new ComplianceCheck("DOMAIN_AGE", "Domain age (RDAP)", CheckStatus.Skipped, domain?.Error ?? "RDAP lookup not performed.", RiskTier.Medium));
            return;
        }
        if (domain.Registered is DateTimeOffset reg)
        {
            var ageDays = (DateTimeOffset.UtcNow - reg).TotalDays;
            var status = ageDays < 90 ? CheckStatus.Fail : ageDays < 365 ? CheckStatus.Warn : CheckStatus.Pass;
            checks.Add(new ComplianceCheck("DOMAIN_AGE", "Domain age (RDAP)", status,
                $"Registered {reg:yyyy-MM-dd} ({ageDays / 365.25:F1} years) via {domain.Registrar ?? "unknown registrar"}.{(ageDays < 365 ? " Very new domains correlate with bust-out fraud." : string.Empty)}",
                RiskTier.Medium));
        }
        if (domain.Expires is DateTimeOffset exp && exp < DateTimeOffset.UtcNow.AddDays(60))
            checks.Add(new ComplianceCheck("DOMAIN_EXPIRING", "Domain expiry", CheckStatus.Warn, $"Domain expires {exp:yyyy-MM-dd}.", RiskTier.Low));
        if (domain.Statuses.Any(s => s.Contains("hold", StringComparison.OrdinalIgnoreCase) || s.Contains("redemption", StringComparison.OrdinalIgnoreCase)))
            checks.Add(new ComplianceCheck("DOMAIN_STATUS", "Domain status", CheckStatus.Fail, $"Registry status: {string.Join(", ", domain.Statuses)}.", RiskTier.High));
    }

    private static WebsiteComplianceResult Build(Uri url, bool reachable, List<ComplianceCheck> checks, DomainInfo? domain, ProhibitedBusinessResult prohibited, IReadOnlyList<Uri> pages)
    {
        double max = 0, got = 0;
        foreach (var c in checks.Where(c => c.Status != CheckStatus.Skipped))
        {
            var weight = c.Severity switch { RiskTier.High => 3, RiskTier.Medium => 2, _ => 1 };
            max += weight;
            got += c.Status switch { CheckStatus.Pass => weight, CheckStatus.Warn => weight * 0.5, _ => 0 };
        }
        var score = max == 0 ? 0 : (int)Math.Round(100 * got / max);
        var grade = score >= 90 ? "A" : score >= 75 ? "B" : score >= 60 ? "C" : score >= 40 ? "D" : "F";
        return new WebsiteComplianceResult(url, reachable, score, grade, checks, domain, prohibited, pages);
    }

    private async Task<(ExtractedPage? Page, Uri Url, string? TlsError)> FetchWithTlsCheckAsync(HttpClient client, Uri httpsUrl, Uri original, CancellationToken ct)
    {
        string? tlsError = null;
        try
        {
            var page = await FetchPageAsync(client, httpsUrl, ct, throwOnError: true);
            if (page is not null) return (page, httpsUrl, null);
        }
        catch (HttpRequestException ex)
        {
            tlsError = ex.InnerException?.Message ?? ex.Message;
        }
        catch (TaskCanceledException)
        {
            tlsError = "timeout";
        }

        var httpUrl = new UriBuilder(original) { Scheme = Uri.UriSchemeHttp, Port = -1 }.Uri;
        var fallback = await FetchPageAsync(client, httpUrl, ct);
        return (fallback, httpUrl, tlsError ?? "HTTPS returned no HTML");
    }

    private async Task<ExtractedPage?> FetchPageAsync(HttpClient client, Uri url, CancellationToken ct, bool throwOnError = false)
    {
        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) return null;
            var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (mediaType.Length > 0 && !mediaType.Contains("html", StringComparison.OrdinalIgnoreCase)) return null;
            var html = await response.Content.ReadAsStringAsync(ct);
            return HtmlTextExtractor.Extract(html, response.RequestMessage?.RequestUri ?? url);
        }
        catch (Exception ex) when (!throwOnError && ex is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            _logger.LogInformation(ex, "Compliance fetch {Url} failed", url);
            return null;
        }
    }

    private async Task<DomainInfo?> LookupDomainAsync(string host, CancellationToken ct)
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
