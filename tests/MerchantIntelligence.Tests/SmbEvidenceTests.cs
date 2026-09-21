using System.Text.Json;
using MerchantIntelligence.Kyb.Compliance;
using MerchantIntelligence.Kyb.Registry;
using MerchantIntelligence.MccValidation.Taxonomy;
using MerchantIntelligence.Kyb;
using MerchantIntelligence.Platform.Profiling;
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

public class OwnerIdentityTests
{
    private static readonly DateOnly Today = new(2026, 9, 19);
    private static readonly MerchantIntelligence.Platform.Profiling.MerchantProfile Small = new(
        MerchantIntelligence.Platform.Profiling.EntityType.MultiMemberLlc, false, MerchantIntelligence.Platform.Profiling.MerchantSegment.Small,
        MerchantIntelligence.Platform.Profiling.RegistryScope.Local, 1, [], [], []);

    private static MerchantIntelligence.Platform.Assessment.AssessmentIntake Intake(params MerchantIntelligence.Kyb.BeneficialOwner[] owners) =>
        new(new BusinessIdentity("Aljazzar Meat & Grill LLC", AddressLine: "4213 Bardstown Road", City: "Louisville", Region: "KY", PostalCode: "40218", Country: "US"),
            owners, "Restaurant", 5812, 600_000m, 28m, 400m, false, YearsInBusiness: 2);

    private static MerchantIntelligence.Platform.Owners.OwnerAssessment Assess(MerchantIntelligence.Platform.Assessment.AssessmentIntake i,
        Func<MerchantIntelligence.Kyb.BeneficialOwner, IReadOnlyList<MerchantIntelligence.Platform.Owners.PriorApplication>>? history = null) =>
        MerchantIntelligence.Platform.Owners.OwnerIdentityAssessor.Assess(i, Small, history ?? (_ => []), Today);

    [Fact]
    public void Complete_owner_has_no_findings_and_full_completeness()
    {
        var r = Assess(Intake(new BeneficialOwner("Jane Doe", new DateOnly(1980, 1, 1), "US", "Owner", 100, "12 Elm St, Louisville, KY 40205")));
        Assert.True(r.Covered);
        Assert.Equal(1.0, r.CompletenessPercent);
        Assert.Empty(r.Flags);
        Assert.Equal(46, r.Owners[0].Age);
        Assert.False(r.Owners[0].SharesBusinessAddress);
    }

    [Fact]
    public void Missing_attributes_are_medium_for_smb_and_lower_completeness()
    {
        var r = Assess(Intake(new BeneficialOwner("Jane Doe", Role: "Owner")));
        var f = Assert.Single(r.Flags, x => x.Code == "OWNER_IDENTITY_INCOMPLETE");
        Assert.Equal(RiskTier.Medium, f.Severity);
        Assert.Contains("date of birth", f.Message);
        Assert.Equal(0, r.CompletenessPercent);
    }

    [Fact]
    public void Age_versus_tenure_and_underage_are_flagged()
    {
        var young = Intake(new BeneficialOwner("Kid Owner", Today.AddYears(-17), "US", "Owner")) with { YearsInBusiness = 5 };
        var r = Assess(young);
        Assert.Contains(r.Flags, x => x.Code == "OWNER_UNDERAGE" && x.Severity == RiskTier.High);
        Assert.Contains(r.Flags, x => x.Code == "OWNER_AGE_VS_TENURE");
    }

    [Fact]
    public void Owner_at_business_address_marks_home_based()
    {
        var r = Assess(Intake(new BeneficialOwner("Jane Doe", new DateOnly(1980, 1, 1), "US", "Owner", 100, "4213 Bardstown Rd, Louisville KY 40218")));
        Assert.True(r.HomeBased);
        Assert.True(r.Owners[0].SharesBusinessAddress);
        Assert.Contains(r.Flags, x => x.Code == "OWNER_HOME_BASED");
    }

    [Fact]
    public void Prior_applications_become_duplicate_or_velocity_findings()
    {
        var one = new List<MerchantIntelligence.Platform.Owners.PriorApplication> { new("a1", "Other Grill LLC", DateTimeOffset.UtcNow.AddDays(-200)) };
        var dup = Assess(Intake(new BeneficialOwner("Jane Doe", new DateOnly(1980, 1, 1), "US", "Owner")), _ => one);
        Assert.Contains(dup.Flags, x => x.Code == "OWNER_DUPLICATE_APPLICATION" && x.Severity == RiskTier.Medium);

        var three = Enumerable.Range(1, 3).Select(n => new MerchantIntelligence.Platform.Owners.PriorApplication($"a{n}", $"Shop {n}", DateTimeOffset.UtcNow.AddDays(-n * 10))).ToList();
        var vel = Assess(Intake(new BeneficialOwner("Jane Doe", new DateOnly(1980, 1, 1), "US", "Owner")), _ => three);
        Assert.Contains(vel.Flags, x => x.Code == "OWNER_APPLICATION_VELOCITY" && x.Severity == RiskTier.High);
    }

    [Fact]
    public void No_owner_is_not_covered()
    {
        var r = Assess(Intake());
        Assert.False(r.Covered);
        Assert.Empty(r.Flags);
    }

    [Fact]
    public void Principal_registry_finds_same_person_behind_other_merchants_only()
    {
        using var db = new MerchantIntelligence.Platform.Storage.PlatformDatabase(new MerchantIntelligence.Platform.Storage.PlatformOptions { DatabasePath = ":memory:" });
        var reg = new MerchantIntelligence.Platform.Owners.PrincipalRegistry(db);
        var jane = new MerchantIntelligence.Kyb.BeneficialOwner("Jane Doe", new DateOnly(1980, 1, 1));
        reg.Remember("a1", "First Shop LLC", [jane], DateTimeOffset.UtcNow.AddDays(-30));
        reg.Remember("a2", "First Shop LLC", [jane], DateTimeOffset.UtcNow.AddDays(-10)); // re-assessment of the same merchant
        reg.Remember("a3", "Second Shop LLC", [new("Jane Doe")], DateTimeOffset.UtcNow.AddDays(-5)); // no DOB on file

        var prior = reg.PriorApplications(jane, "Third Shop LLC", "a4");
        Assert.Equal(3, prior.Count);
        Assert.Equal("Second Shop LLC", Assert.Single(reg.PriorApplications(jane, "First Shop LLC", "a5")).MerchantName);
        Assert.Empty(reg.PriorApplications(new("John Roe", new DateOnly(1970, 1, 1)), "Third Shop LLC", "a4"));
    }
}

public class AddressClassifierTests
{
    private static GeocodeHit Hit(string? category, string? type, params (string, string)[] extra) =>
        new(new GeoPoint(38.2, -85.7), category, type, null, extra.ToDictionary(e => e.Item1, e => e.Item2), null);

    private static PlaceRecord Poi(string name, string? category) =>
        new("OpenStreetMap (Overpass)", name, name, null, 38.2, -85.7, category, null, null, null, null);

    [Fact]
    public void Po_box_is_a_mail_drop_and_high_for_storefront_mcc()
    {
        var c = AddressClassifier.Classify("PO Box 1234", null, [], 5812);
        Assert.Equal(AddressType.Cmra, c.Type);
        Assert.Contains(c.Flags, f => f.Code == "ADDRESS_CMRA" && f.Severity == RiskTier.High);
        Assert.False(c.Covered);
    }

    [Fact]
    public void Cmra_operator_at_the_spot_is_a_mail_drop()
    {
        var c = AddressClassifier.Classify("123 Main St Ste 200", Hit("building", "commercial"), [Poi("The UPS Store", "shop / copyshop")], 7372);
        Assert.Equal(AddressType.Cmra, c.Type);
        Assert.Contains(c.Flags, f => f.Code == "ADDRESS_CMRA" && f.Severity == RiskTier.Medium);
    }

    [Fact]
    public void Residential_building_with_storefront_mcc_is_flagged_medium()
    {
        var c = AddressClassifier.Classify("45 Elm St Apt 3", Hit("building", "house"), [], 5814);
        Assert.Equal(AddressType.Residential, c.Type);
        Assert.True(c.Confidence >= 0.5);
        Assert.Contains(c.Flags, f => f.Code == "ADDRESS_RESIDENTIAL_STOREFRONT_MCC" && f.Severity == RiskTier.Medium);
    }

    [Fact]
    public void Residential_with_home_compatible_mcc_is_consistent()
    {
        var c = AddressClassifier.Classify("45 Elm St", Hit("building", "house"), [], 5811);
        Assert.Equal(AddressType.Residential, c.Type);
        Assert.Contains(c.Flags, f => f.Code == "ADDRESS_HOME_BASED" && f.Severity == RiskTier.Low);
    }

    [Fact]
    public void Restaurant_poi_at_a_commercial_building_is_commercial_with_no_findings()
    {
        var c = AddressClassifier.Classify("4213 Bardstown Rd", Hit("amenity", "restaurant"), [Poi("Aljazzar Grill", "amenity / restaurant")], 5812);
        Assert.Equal(AddressType.Commercial, c.Type);
        Assert.Empty(c.Flags);
        Assert.True(c.Covered);
    }

    [Fact]
    public void Residential_and_commercial_evidence_is_mixed_use()
    {
        var c = AddressClassifier.Classify("10 High St", Hit("building", "apartments", ("landuse", "retail")), [Poi("Corner Cafe", "amenity / cafe"), Poi("Barber", "shop / hairdresser")], 5812);
        Assert.Equal(AddressType.MixedUse, c.Type);
        Assert.Empty(c.Flags);
    }

    [Fact]
    public void No_evidence_is_unknown_not_commercial()
    {
        var c = AddressClassifier.Classify("1 Nowhere Ln", null, [], 5812);
        Assert.Equal(AddressType.Unknown, c.Type);
        Assert.False(c.Covered);
        Assert.Contains(c.Flags, f => f.Code == "ADDRESS_TYPE_UNKNOWN");
    }

    [Fact]
    public void Geocoder_hit_parses_extratags()
    {
        using var doc = JsonDocument.Parse("""{"lat":"38.2","lon":"-85.7","category":"building","type":"house","addresstype":"building","display_name":"x","extratags":{"building:levels":"2","landuse":"residential"}}""");
        var hit = NominatimGeocoder.ParseHit(doc.RootElement)!;
        Assert.Equal("building", hit.Category);
        Assert.Equal("house", hit.Type);
        Assert.Equal("residential", hit.ExtraTags["landuse"]);
        Assert.Equal(38.2, hit.Point.Latitude, 3);
    }

    [Fact]
    public void Presence_flags_include_address_type_findings()
    {
        var identity = new BusinessIdentity("Acme", AddressLine: "PO Box 9");
        var v = new BusinessVerificationResult(identity, VerificationStatus.Inconclusive, 0, null, null, null, [], []);
        var lp = new LocalPresenceResult(LocalPresenceStatus.NotFound, 0, null, [], AddressType: AddressClassifier.Classify("PO Box 9", null, [], 5999));
        var merged = BusinessVerificationService.WithLocalPresence(v, lp);
        Assert.Contains(merged.Flags, f => f.Code == "ADDRESS_CMRA");
    }
}
