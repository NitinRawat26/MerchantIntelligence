using System.Text.Json;
using MerchantIntelligence.Kyb.Compliance;
using MerchantIntelligence.Kyb.Registry;
using MerchantIntelligence.MccValidation.Taxonomy;
using Xunit;

namespace MerchantIntelligence.Tests;

public class KentuckySosProviderTests
{
    [Fact]
    public void Covers_only_us_kentucky_applicants()
    {
        var provider = new KentuckySosRegistryProvider(new NoHttp(), new KybOptions());
        Assert.True(provider.Covers(new BusinessIdentity("X", Region: "KY", Country: "US")));
        Assert.True(provider.Covers(new BusinessIdentity("X", Region: "Kentucky")));
        Assert.False(provider.Covers(new BusinessIdentity("X", Region: "TX", Country: "US")));
        Assert.False(provider.Covers(new BusinessIdentity("X", Region: "KY", Country: "GB")));
        Assert.False(provider.Covers(new BusinessIdentity("X")));
    }

    [Fact]
    public void Queries_fall_back_from_full_name_to_distinctive_tokens()
    {
        var q = KentuckySosRegistryProvider.QueriesFor(new BusinessIdentity("Aljazzar Meat & Grill LLC", "Al-Jazzar Grill"));
        Assert.Equal("Aljazzar Meat & Grill LLC", q[0]);
        Assert.Contains("Aljazzar Meat", q);
        Assert.Contains("AljazzarMeat", q);
        Assert.Contains("Aljazzar", q);
        Assert.Contains("Al-Jazzar Grill", q);
        Assert.DoesNotContain("LLC", q);
        Assert.True(q.Count <= 6);
    }

    [Fact]
    public void Numeric_registration_number_is_searched_first()
    {
        var q = KentuckySosRegistryProvider.QueriesFor(new BusinessIdentity("Aljazzar Meat & Grill LLC", RegistrationNumber: "1367874"));
        Assert.Equal("1367874", q[0]);
    }

    [Fact]
    public void Significant_tokens_drop_entity_suffixes_and_conjunctions()
    {
        Assert.Equal(new[] { "ALJAZZAR", "MEAT", "GRILL" }, KentuckySosRegistryProvider.SignificantTokens("ALJAZZAR MEAT & GRILL LLC"));
    }

    [Fact]
    public void Parses_search_result_rows()
    {
        const string html = """
            <table><tr><th>Name</th><th>Org</th><th>Status</th><th>Type</th></tr>
            <tr><td><a href="Profile.aspx?ctr=1367874">ALJAZZAR MEAT &amp; GRILL LLC</a></td><td>1367874</td><td>A - Active</td><td>KLC</td></tr>
            <tr><td><a href="Profile.aspx?ctr=1367874&amp;an=1">ALJAZZAR MEATS &amp; GRILL</a></td><td>1367874</td><td>A - Active</td><td>ASSUMED</td></tr>
            </table>
            """;
        var hits = KentuckySosRegistryProvider.ParseSearchResults(html);
        Assert.Equal(2, hits.Count);
        Assert.Equal("ALJAZZAR MEAT & GRILL LLC", hits[0].Name);
        Assert.Equal("1367874", hits[0].OrganizationNumber);
        Assert.Equal("Profile.aspx?ctr=1367874&an=1", hits[1].ProfilePath);
    }

    [Fact]
    public void Parses_profile_general_information()
    {
        const string html = """
            <html><body><script>var x = 1;</script>
            <h2>General Information</h2>
            <div>Organization Number</div><div>1367874</div>
            <div>Name</div><div>ALJAZZAR MEAT &amp; GRILL LLC</div>
            <div>Company Type</div><div>KLC - Kentucky Limited Liability Company</div>
            <div>Industry</div><div>Eating and Drinking Places</div>
            <div>Number of Employees</div><div>Small (0-19)</div>
            <div>Primary County</div><div>Jefferson</div>
            <div>Status</div><div>A - Active</div>
            <div>Standing</div><div>G - Good</div>
            <div>Organization Date</div><div>5/28/2024</div>
            <div>Principal Office</div><div>4213 BARDSTOWN ROAD</div><div>LOUISVILLE, KY 40218</div>
            <div>Registered Agent</div><div>NOUR KURDI</div>
            </body></html>
            """;
        var p = KentuckySosRegistryProvider.ParseProfile(html);
        Assert.Equal("1367874", p["Organization Number"]);
        Assert.Equal("ALJAZZAR MEAT & GRILL LLC", p["Name"]);
        Assert.Equal("Eating and Drinking Places", p["Industry"]);
        Assert.Equal("Small (0-19)", p["Number of Employees"]);
        Assert.Equal("G - Good", p["Standing"]);
        Assert.Equal("4213 BARDSTOWN ROAD, LOUISVILLE, KY 40218", p["Principal Office"]);
        Assert.Equal("NOUR KURDI", p["Registered Agent"]);
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("network must not be used in this test");
    }
}

public class RegistryStandingTests
{
    private static RegistryRecord Record(IReadOnlyDictionary<string, string>? extra) =>
        new("Kentucky SOS", "1367874", "ALJAZZAR MEAT & GRILL LLC", "A - Active", new DateOnly(2024, 5, 28), "US-KY", "1367874", null, "KLC", null, extra);

    [Fact]
    public void Bad_standing_is_flagged_medium()
    {
        var f = BusinessVerificationService.StandingFlag(Record(new Dictionary<string, string> { ["standing"] = "B - Bad" }));
        Assert.NotNull(f);
        Assert.Equal("REGISTRY_BAD_STANDING", f!.Code);
        Assert.Equal(RiskTier.Medium, f.Severity);
    }

    [Fact]
    public void Good_or_absent_standing_adds_nothing()
    {
        Assert.Null(BusinessVerificationService.StandingFlag(Record(new Dictionary<string, string> { ["standing"] = "G - Good" })));
        Assert.Null(BusinessVerificationService.StandingFlag(Record(null)));
    }
}

public class DigitalFootprintTests
{
    [Theory]
    [InlineData("owner@gmail.com", "gmail.com")]
    [InlineData("  Info@AljazzarGrill.com ", "aljazzargrill.com")]
    [InlineData("https://www.aljazzargrill.com/menu", "aljazzargrill.com")]
    [InlineData("aljazzargrill.com", "aljazzargrill.com")]
    [InlineData(null, null)]
    [InlineData("not an email", null)]
    public void Extracts_registrable_domain_from_email_or_url(string? input, string? expected) =>
        Assert.Equal(expected, RdapDomainLookup.DomainOf(input));

    [Fact]
    public void Free_mail_domains_are_recognised()
    {
        Assert.True(RdapDomainLookup.IsFreeMail("gmail.com"));
        Assert.True(RdapDomainLookup.IsFreeMail("outlook.com"));
        Assert.False(RdapDomainLookup.IsFreeMail("aljazzargrill.com"));
    }

    [Fact]
    public void Free_mail_footprint_records_low_flag_without_network()
    {
        var footprint = new DigitalFootprint("gmail.com", true, null, null, null, null,
            new[] { new KybFlag("EMAIL_FREEMAIL", "free mail", RiskTier.Low) });
        var presence = new LocalPresenceResult(LocalPresenceStatus.NotChecked, 0, null, Array.Empty<PlaceSourceResult>(), null, footprint);
        var v = new BusinessVerificationResult(new BusinessIdentity("X", ContactEmail: "a@gmail.com"), VerificationStatus.Inconclusive, 0, null, null, null,
            Array.Empty<RegistrySourceResult>(), Array.Empty<KybFlag>());
        var merged = BusinessVerificationService.WithLocalPresence(v, presence);
        Assert.Contains(merged.Flags, f => f.Code == "EMAIL_FREEMAIL");
        Assert.Equal(VerificationStatus.Inconclusive, merged.Status);
    }
}

public class PlaceReputationTests
{
    [Fact]
    public void Foursquare_reputation_is_parsed()
    {
        using var doc = JsonDocument.Parse("""
            {"fsq_place_id":"abc","name":"Aljazzar Grill","rating":8.7,"popularity":0.93,
             "stats":{"total_ratings":142,"total_tips":30},"hours":{"open_now":true},"date_created":"2019-04-02T10:00:00Z"}
            """);
        var rep = FoursquareLocalPresenceProvider.Reputation(doc.RootElement);
        Assert.NotNull(rep);
        Assert.Equal(8.7, rep!.Rating);
        Assert.Equal(10, rep.RatingScale);
        Assert.Equal(142, rep.RatingCount);
        Assert.Equal(0.93, rep.Popularity);
        Assert.Equal(new DateOnly(2019, 4, 2), rep.ListedSince);
        Assert.True(rep.OpenNow);
    }

    [Fact]
    public void Missing_reputation_fields_yield_null()
    {
        using var doc = JsonDocument.Parse("""{"fsq_place_id":"abc","name":"Aljazzar Grill"}""");
        Assert.Null(FoursquareLocalPresenceProvider.Reputation(doc.RootElement));
    }

    [Fact]
    public void Confirmed_presence_with_reputation_adds_reputation_flag()
    {
        var rep = new PlaceReputation(8.7, 10, 142, 0.93, new DateOnly(2019, 4, 2), true);
        var record = new PlaceRecord("Foursquare", "abc", "Aljazzar Grill", "4213 Bardstown Rd", 38.2, -85.7, "Restaurant", null, null, null, null, rep);
        var presence = new LocalPresenceResult(LocalPresenceStatus.Confirmed, 90, new PlaceMatch(record, 1, 1, 12, 0.95),
            new[] { new PlaceSourceResult("Foursquare", true, Array.Empty<PlaceMatch>()) }, null);
        var v = new BusinessVerificationResult(new BusinessIdentity("Aljazzar Meat & Grill LLC", "Aljazzar Grill", AddressLine: "4213 Bardstown Rd"), VerificationStatus.NotFound, 0, null, null, null,
            Array.Empty<RegistrySourceResult>(), Array.Empty<KybFlag>());
        var merged = BusinessVerificationService.WithLocalPresence(v, presence);
        var flag = Assert.Single(merged.Flags, f => f.Code == "LOCAL_PRESENCE_REPUTATION");
        Assert.Contains("8.7/10", flag.Message);
        Assert.Contains("142 ratings", flag.Message);
        Assert.DoesNotContain(merged.Flags, f => f.Code == "LOCAL_PRESENCE_LOW_RATING");
    }
}
