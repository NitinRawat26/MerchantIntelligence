using MerchantIntelligence.MccValidation.Classification;
using MerchantIntelligence.MccValidation.Edgar;
using MerchantIntelligence.MccValidation.Taxonomy;
using MerchantIntelligence.MccValidation.Validation;
using MerchantIntelligence.MccValidation.Web;
using Microsoft.Extensions.Logging.Abstractions;

namespace MerchantIntelligence.Tests;

public sealed class MccCatalogTests
{
    [Fact]
    public void Catalog_and_crosswalk_load_and_agree()
    {
        var catalog = MccCatalog.Default;

        Assert.True(catalog.Entries.Count > 100);
        Assert.Equal(444, catalog.Crosswalk.Count);
        Assert.All(catalog.Crosswalk, x => Assert.True(catalog.Contains(x.Mcc), $"SIC {x.Sic} maps to unknown MCC {x.Mcc}"));
        Assert.All(catalog.Entries, e => Assert.NotEmpty(e.Keywords));
    }

    [Theory]
    [InlineData(5812, 5812)] // eating places
    [InlineData(7011, 7011)] // hotels
    [InlineData(6022, 6011)] // state commercial banks -> financial institutions
    [InlineData(7372, 7372)] // prepackaged software
    [InlineData(3571, 5045)] // electronic computers -> computer wholesale
    [InlineData(2834, 5122)] // pharmaceutical preparations -> drugs
    public void Sic_maps_to_expected_mcc(int sic, int expectedMcc) =>
        Assert.Equal(expectedMcc, MccCatalog.Default.MapSicToMcc(sic));

    [Fact]
    public void Unknown_sic_returns_null() => Assert.Null(MccCatalog.Default.MapSicToMcc(1));
}

public sealed class HtmlTextExtractorTests
{
    private const string Html = """
        <html><head><title>Luigi's Trattoria</title>
        <meta name="description" content="Family owned Italian restaurant">
        <script type="application/ld+json">{"@context":"https://schema.org","@type":"Restaurant","name":"Luigi's"}</script>
        <script>var x = 'should not appear';</script>
        <style>.hidden{display:none}</style>
        </head><body>
        <nav><a href="/about">About</a><a href="https://other.example/x">External</a><a href="#top">Top</a></nav>
        <h1>Welcome to Luigi's</h1>
        <p>Fresh pasta &amp; pizza, served daily.</p>
        <footer>Copyright</footer>
        </body></html>
        """;

    [Fact]
    public void Extracts_title_meta_headings_text_and_schema_types()
    {
        var page = HtmlTextExtractor.Extract(Html, new Uri("https://luigis.example"));

        Assert.Equal("Luigi's Trattoria", page.Title);
        Assert.Equal("Family owned Italian restaurant", page.MetaDescription);
        Assert.Contains("Welcome to Luigi's", page.Headings);
        Assert.Contains("Restaurant", page.SchemaOrgTypes);
        Assert.Contains("Fresh pasta & pizza", page.BodyText);
        Assert.DoesNotContain("should not appear", page.BodyText);
        Assert.DoesNotContain("display:none", page.BodyText);
        Assert.DoesNotContain("Copyright", page.BodyText);
    }

    [Fact]
    public void Internal_links_exclude_external_and_fragment_links()
    {
        var page = HtmlTextExtractor.Extract(Html, new Uri("https://luigis.example"));

        Assert.Single(page.InternalLinks);
        Assert.Equal("https://luigis.example/about", page.InternalLinks[0].ToString());
    }
}

public sealed class EdgarBusinessSectionTests
{
    [Fact]
    public void Picks_the_longest_item1_section_skipping_table_of_contents()
    {
        var body = string.Join(" ", Enumerable.Repeat("We design and sell widgets to retailers worldwide.", 20));
        var html = $"""
            <html><body>
            <p>Item 1. Business 3</p><p>Item 1A. Risk Factors 10</p>
            <h2>Item 1. Business</h2><p>{body}</p>
            <h2>Item 1A. Risk Factors</h2><p>Many risks.</p>
            </body></html>
            """;

        var section = EdgarClient.ExtractBusinessSection(html);

        Assert.NotNull(section);
        Assert.StartsWith("We design and sell widgets", section);
        Assert.DoesNotContain("Many risks", section);
    }

    [Fact]
    public void Returns_null_when_no_business_section()
    {
        Assert.Null(EdgarClient.ExtractBusinessSection("<html><body><p>Nothing here</p></body></html>"));
    }

    [Fact]
    public void Extracts_company_website_from_available_information_paragraph()
    {
        const string html = """
            <html><body>
            <p>Reports are available free of charge on the SEC's website at www.sec.gov.</p>
            <p>Our Internet address is www.widgetco.com. Information on our website is not incorporated by reference.</p>
            </body></html>
            """;

        Assert.Equal("www.widgetco.com", EdgarClient.ExtractWebsite(html)?.Host);
        Assert.Null(EdgarClient.ExtractWebsite("<p>Our website has no address listed.</p>"));
    }
}

public sealed class EvidenceProviderTests
{
    private static WebsiteContent Site(string html) =>
        new(new Uri("https://example.com"), new[] { new Uri("https://example.com") },
            new[] { HtmlTextExtractor.Extract(html, new Uri("https://example.com")) });

    [Fact]
    public async Task Keyword_provider_ranks_restaurant_for_restaurant_site()
    {
        var provider = new KeywordTaxonomyProvider(MccCatalog.Default);
        var site = Site("<html><body><h1>Luigi's Restaurant</h1><p>View our menu. Pizza, pasta, dining and reservations. Cafe and grill.</p></body></html>");

        var evidence = await provider.EvaluateAsync(new ValidationContext(5812, site.RequestedUrl, site), default);

        Assert.True(evidence.Succeeded);
        Assert.Equal(5812, evidence.Candidates[0].Mcc);
    }

    [Fact]
    public async Task Keyword_provider_fails_gracefully_on_empty_site()
    {
        var provider = new KeywordTaxonomyProvider(MccCatalog.Default);
        var site = new WebsiteContent(new Uri("https://example.com"), Array.Empty<Uri>(), Array.Empty<ExtractedPage>());

        var evidence = await provider.EvaluateAsync(new ValidationContext(5812, site.RequestedUrl, site), default);

        Assert.False(evidence.Succeeded);
        Assert.NotNull(evidence.Error);
    }

    [Fact]
    public async Task Structured_data_provider_maps_schema_types()
    {
        var provider = new StructuredDataProvider(MccCatalog.Default);
        var site = Site("""<html><head><script type="application/ld+json">{"@type":"Hotel"}</script></head><body>x</body></html>""");

        var evidence = await provider.EvaluateAsync(new ValidationContext(7011, site.RequestedUrl, site), default);

        Assert.Equal(7011, Assert.Single(evidence.Candidates).Mcc);
    }

    [Fact]
    public async Task Edgar_provider_uses_crosswalk_for_known_filer()
    {
        var index = new FakeIndex(new EdgarFilerSummary(320193, "Apple Inc.", 3571, "Electronic Computers", "apple.com"));
        var provider = new EdgarSicProvider(index, MccCatalog.Default);
        var site = Site("<html><body>x</body></html>");

        var evidence = await provider.EvaluateAsync(new ValidationContext(5045, new Uri("https://www.apple.com"), site), default);

        Assert.Equal(5045, Assert.Single(evidence.Candidates).Mcc);
        Assert.Contains(evidence.Highlights, h => h.Contains("SIC 3571"));
    }

    [Fact]
    public void Domain_normalisation_strips_www_and_scheme()
    {
        Assert.Equal("apple.com", FileEdgarDomainIndex.NormalizeHost("WWW.Apple.com"));
        Assert.Equal("apple.com", FileEdgarDomainIndex.NormalizeHost("https://www.apple.com/"));
    }

    private sealed class FakeIndex(EdgarFilerSummary filer) : IEdgarDomainIndex
    {
        public Task<EdgarFilerSummary?> FindByDomainAsync(string host, CancellationToken ct) =>
            Task.FromResult(FileEdgarDomainIndex.NormalizeHost(host) == filer.Domain ? filer : null);
    }
}

public sealed class EvidenceAggregatorTests
{
    private sealed class StubProvider(string name, double weight) : IMccEvidenceProvider
    {
        public string Name => name;
        public double Weight => weight;
        public Task<ProviderEvidence> EvaluateAsync(ValidationContext context, CancellationToken ct) => throw new NotSupportedException();
    }

    private static readonly Uri Url = new("https://example.com");
    private readonly EvidenceAggregator _aggregator = new(MccCatalog.Default);

    private static (IMccEvidenceProvider, ProviderEvidence) Vote(string name, double weight, params (int Mcc, double Score)[] candidates) =>
        (new StubProvider(name, weight),
         new ProviderEvidence(name, true, candidates.Select(c => new MccCandidate(c.Mcc, "", c.Score)).ToList(), Array.Empty<string>()));

    [Fact]
    public void Unanimous_support_is_consistent_with_high_accuracy()
    {
        var result = _aggregator.Aggregate(5812, Url, new[]
        {
            Vote("a", 1.0, (5812, 0.8), (5814, 0.2)),
            Vote("b", 1.5, (5812, 0.9))
        }, Array.Empty<Uri>());

        Assert.Equal(MccVerdict.Consistent, result.Verdict);
        Assert.True(result.AccuracyPercent > 70, $"accuracy {result.AccuracyPercent}");
        Assert.Equal(5812, result.SuggestedMccs[0].Mcc);
        Assert.DoesNotContain(result.RiskFlags, f => f.Code == "MCC_MISMATCH");
    }

    [Fact]
    public void Contradicting_evidence_is_inconsistent_and_flags_mismatch()
    {
        var result = _aggregator.Aggregate(5411, Url, new[]
        {
            Vote("a", 1.0, (7372, 0.9)),
            Vote("b", 1.5, (7372, 0.7), (7399, 0.3))
        }, Array.Empty<Uri>());

        Assert.Equal(MccVerdict.Inconsistent, result.Verdict);
        Assert.True(result.AccuracyPercent < 25);
        Assert.Contains(result.RiskFlags, f => f.Code == "MCC_MISMATCH");
    }

    [Fact]
    public void Hidden_high_risk_category_is_flagged()
    {
        var result = _aggregator.Aggregate(5411, Url, new[] { Vote("a", 1.0, (7995, 1.0)) }, Array.Empty<Uri>());

        Assert.Contains(result.RiskFlags, f => f.Code == "HIDDEN_HIGH_RISK" && f.Severity == RiskTier.High);
    }

    [Fact]
    public void Same_category_neighbour_is_questionable_not_inconsistent()
    {
        // Declared full-service restaurant, evidence says fast food: same "Retail" category.
        var result = _aggregator.Aggregate(5812, Url, new[] { Vote("a", 1.0, (5814, 1.0)) }, Array.Empty<Uri>());

        Assert.Equal(MccVerdict.Questionable, result.Verdict);
    }

    [Fact]
    public void No_usable_evidence_is_insufficient_and_lists_failed_providers()
    {
        (IMccEvidenceProvider, ProviderEvidence) failed = (new StubProvider("web", 1.0), ProviderEvidence.Failed("web", "timeout"));
        var result = _aggregator.Aggregate(5812, Url, new[] { failed }, Array.Empty<Uri>());

        Assert.Equal(MccVerdict.Insufficient, result.Verdict);
        Assert.Equal(0, result.AccuracyPercent);
        Assert.Contains(result.RiskFlags, f => f.Code == "PROVIDER_UNAVAILABLE" && f.Message.Contains("web"));
        Assert.Contains(result.RiskFlags, f => f.Code == "INSUFFICIENT_EVIDENCE");
    }

    [Fact]
    public void Declared_high_risk_mcc_is_flagged()
    {
        var result = _aggregator.Aggregate(7995, Url, new[] { Vote("a", 1.0, (7995, 1.0)) }, Array.Empty<Uri>());

        Assert.Contains(result.RiskFlags, f => f.Code == "HIGH_RISK_MCC");
    }
}

public sealed class MccTextClassifierTests
{
    [Fact]
    public void Trains_predicts_and_roundtrips()
    {
        var records = new List<MccTrainingRecord>();
        var rng = new Random(1);
        string[] food = { "restaurant menu pizza pasta dining reservations chef", "cafe coffee brunch dinner table service cuisine", "grill burgers dining room family restaurant" };
        string[] software = { "cloud software platform saas api developers", "enterprise software analytics machine learning data", "subscription software cybersecurity cloud services" };
        string[] hotel = { "hotel rooms guests resort lodging suites", "hospitality resort spa rooms booking guests", "boutique hotel accommodation nightly rates" };
        for (var i = 0; i < 30; i++)
        {
            records.Add(new MccTrainingRecord { Text = food[rng.Next(food.Length)] + " " + i, Mcc = "5812" });
            records.Add(new MccTrainingRecord { Text = software[rng.Next(software.Length)] + " " + i, Mcc = "7372" });
            records.Add(new MccTrainingRecord { Text = hotel[rng.Next(hotel.Length)] + " " + i, Mcc = "7011" });
        }

        var (classifier, metrics) = MccTextClassifier.Train(records);
        Assert.Equal(3, classifier.ClassCount);
        Assert.True(metrics.MicroAccuracy > 0.9, $"accuracy {metrics.MicroAccuracy}");

        var path = Path.Combine(Path.GetTempPath(), $"mcc-{Guid.NewGuid():N}.zip");
        try
        {
            classifier.Save(path);
            var reloaded = MccTextClassifier.Load(path);
            var prediction = reloaded.Predict("Italian restaurant with a seasonal menu and pizza oven");

            Assert.Equal(5812, prediction.Top.Mcc);
            Assert.InRange(prediction.Ranked.Sum(r => r.Probability), 0.99f, 1.01f);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
