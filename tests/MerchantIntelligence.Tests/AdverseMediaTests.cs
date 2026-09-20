using MerchantIntelligence.Kyb.Sanctions;
using Microsoft.Extensions.Logging.Abstractions;

namespace MerchantIntelligence.Tests;

public sealed class AdverseMediaAnalyzerTests
{
    private static readonly ScreeningSubject Person = new("John Doe", null, "US", true, "Owner");
    private static readonly ScreeningSubject Company = new("Blue Ocean Bakery LLC", null, "US", false, "Business");

    private static AdverseMediaArticle Raw(string title, string? snippet = null) =>
        new(title, new Uri("https://example.com/a"), "example.com", null, "neutral", Snippet: snippet);

    [Fact]
    public void Risk_term_in_headline_with_subject_is_negative_with_context()
    {
        var a = AdverseMediaAnalyzer.Grade(Person, Raw("John Doe indicted in wire fraud scheme"));
        Assert.Equal("negative", a.Tone);
        Assert.Contains("indicted", a.MatchedTerms!);
        Assert.Contains("wire fraud", a.MatchedTerms!);
        Assert.Contains("Criminal proceedings", a.Category);
        Assert.Contains("Financial crime", a.Category);
        Assert.Equal("John Doe indicted in wire fraud scheme", a.Context);
    }

    [Fact]
    public void Risk_term_in_same_sentence_of_body_is_negative()
    {
        var a = AdverseMediaAnalyzer.Grade(Company, Raw(
            "Local bakery news",
            "<p>The council met on Tuesday. Regulators fined Blue Ocean Bakery over undisclosed payments. Weather was mild.</p>"));
        Assert.Equal("negative", a.Tone);
        Assert.Equal("Regulators fined Blue Ocean Bakery over undisclosed payments.", a.Context);
        Assert.DoesNotContain("<p>", a.Snippet);
    }

    [Fact]
    public void Risk_term_elsewhere_in_article_is_only_a_mention()
    {
        var a = AdverseMediaAnalyzer.Grade(Person, Raw(
            "City business roundup",
            "John Doe opened a second café downtown. Separately, a rival chain faces a lawsuit over wages."));
        Assert.Equal("mention", a.Tone);
        Assert.Equal(new[] { "lawsuit" }, a.MatchedTerms);
    }

    [Fact]
    public void Article_without_subject_or_terms_is_neutral()
    {
        Assert.Equal("neutral", AdverseMediaAnalyzer.Grade(Person, Raw("Fraud ring busted in another state")).Tone);
        Assert.Equal("neutral", AdverseMediaAnalyzer.Grade(Person, Raw("John Doe wins baking award")).Tone);
    }

    [Fact]
    public void Legal_suffixes_do_not_block_company_match()
    {
        var a = AdverseMediaAnalyzer.Grade(Company, Raw("Blue Ocean Bakery hit with chargeback probe"));
        Assert.Equal("negative", a.Tone);
    }
}

public sealed class CompositeAdverseMediaProviderTests
{
    private sealed class StubSource(string name, Func<IReadOnlyList<AdverseMediaArticle>> result) : IAdverseMediaSource
    {
        public string Name => name;
        public Task<IReadOnlyList<AdverseMediaArticle>> SearchAsync(ScreeningSubject subject, CancellationToken ct) => Task.FromResult(result());
    }

    private static readonly ScreeningSubject Subject = new("Jane Roe", null, "US", true, "Owner");

    private static AdverseMediaArticle A(string title, string url) => new(title, new Uri(url), new Uri(url).Host, null, "neutral");

    [Fact]
    public async Task Partial_failure_still_succeeds_and_reports_failed_source()
    {
        var provider = new CompositeAdverseMediaProvider(
        [
            new StubSource("Good", () => [A("Jane Roe arrested after scam", "https://a.example/1")]),
            new StubSource("Bad", () => throw new HttpRequestException("429"))
        ], NullLogger<CompositeAdverseMediaProvider>.Instance);

        var r = await provider.SearchAsync(Subject, CancellationToken.None);
        Assert.True(r.Succeeded);
        Assert.Equal(1, r.NegativeCount);
        Assert.Contains("Bad: 429", r.Error);
        Assert.Equal(2, r.Providers!.Count);
        Assert.False(r.Providers.Single(p => p.Provider == "Bad").Succeeded);
        Assert.Equal("Good", r.Articles[0].Provider);
    }

    [Fact]
    public async Task All_sources_failing_is_not_success()
    {
        var provider = new CompositeAdverseMediaProvider(
            [new StubSource("X", () => throw new HttpRequestException("down"))], NullLogger<CompositeAdverseMediaProvider>.Instance);
        var r = await provider.SearchAsync(Subject, CancellationToken.None);
        Assert.False(r.Succeeded);
        Assert.StartsWith("All adverse-media sources failed", r.Error);
    }

    [Fact]
    public async Task Same_story_from_two_sources_is_deduplicated_and_negatives_sort_first()
    {
        var provider = new CompositeAdverseMediaProvider(
        [
            new StubSource("One", () => [A("Jane Roe opens new shop in town centre", "https://a.example/x"), A("Jane Roe convicted of embezzlement at former employer", "https://a.example/1")]),
            new StubSource("Two", () => [A("Jane Roe convicted of embezzlement at former employer - Daily News", "https://b.example/2")])
        ], NullLogger<CompositeAdverseMediaProvider>.Instance);

        var r = await provider.SearchAsync(Subject, CancellationToken.None);
        Assert.Equal(2, r.ArticleCount);
        Assert.Equal(1, r.NegativeCount);
        Assert.Equal("negative", r.Articles[0].Tone);
    }

    [Fact]
    public void Flags_carry_terms_and_excerpt()
    {
        var media = new AdverseMediaResult("t", true, 2, 1,
        [
            new("Jane Roe indicted for money laundering", new Uri("https://a.example/1"), "a.example", new DateTimeOffset(2025, 1, 2, 0, 0, 0, TimeSpan.Zero), "negative",
                MatchedTerms: ["indicted", "money laundering"], Category: "Criminal proceedings, Financial crime", Context: "Jane Roe indicted for money laundering", Provider: "Google News"),
            new("Town news", new Uri("https://a.example/2"), "a.example", null, "mention", MatchedTerms: ["lawsuit"], Provider: "Bing News")
        ], MentionCount: 1);

        var flags = SanctionsScreeningService.AdverseMediaFlags(Subject, media).ToList();
        var main = Assert.Single(flags, f => f.Code == "ADVERSE_MEDIA");
        Assert.Contains("indicted, money laundering", main.Message);
        Assert.Contains("Google News", main.Message);
        Assert.Contains("\"Jane Roe indicted for money laundering\"", main.Message);
        Assert.Contains("2025-01-02", main.Message);
        var mention = Assert.Single(flags, f => f.Code == "ADVERSE_MEDIA_MENTION");
        Assert.Contains("lawsuit", mention.Message);
    }

    [Fact]
    public void Rss_reader_strips_google_publisher_suffix()
    {
        const string xml = """
            <rss><channel><item>
              <title>Jane Roe sued over unpaid invoices - Daily News</title>
              <link>https://news.example/story</link>
              <pubDate>Mon, 01 Sep 2025 10:00:00 GMT</pubDate>
              <description>&lt;a href="x"&gt;Jane Roe sued over unpaid invoices&lt;/a&gt;</description>
              <source url="https://daily.example">Daily News</source>
            </item></channel></rss>
            """;
        var items = RssReader.Read(xml, "Google News", item =>
        {
            var source = item.Element("source")?.Value;
            var title = item.Element("title")!.Value;
            if (source is { Length: > 0 } && title.EndsWith(" - " + source)) title = title[..^(source.Length + 3)];
            return (title, source);
        });
        var a = Assert.Single(items);
        Assert.Equal("Jane Roe sued over unpaid invoices", a.Title);
        Assert.Equal("Daily News", a.Source);
        Assert.Equal(2025, a.Published!.Value.Year);
    }
}

public sealed class AdverseMediaExplainabilityTests
{
    private static readonly ScreeningSubject Owner = new("Jane Roe", null, "US", true, "Owner");

    private static ScreeningReport Report(AdverseMediaResult media) =>
        new(
            [new SubjectScreeningResult(Owner, false, [], media, [])],
            [new SanctionsListStatus("OFAC SDN", 100, DateTimeOffset.UtcNow, null)],
            MerchantIntelligence.MccValidation.Taxonomy.RiskTier.Low,
            []);

    [Fact]
    public void Summary_lists_answering_sources_and_flags_unavailable_ones_as_partial()
    {
        var media = new AdverseMediaResult("Adverse media (multi-source)", true, 12, 2, [], "1 of 2 source(s) unavailable",
            [new AdverseMediaProviderStatus("GDELT DOC 2.0", false, 0, "429"), new AdverseMediaProviderStatus("Google News", true, 12, null)], 1);

        var text = MerchantIntelligence.Platform.Assessment.AssessmentComposer.MediaSummary(Report(media));

        Assert.Contains("2 negative, 1 indirect mention(s) of 12", text);
        Assert.Contains("from Google News", text);
        Assert.Contains("unavailable: GDELT DOC 2.0", text);
    }

    [Fact]
    public void Narrative_and_evidence_quote_the_sentence_that_ties_the_name_to_the_risk_term()
    {
        var article = new AdverseMediaArticle("Jane Roe indicted", new Uri("https://news.example/1"), "news.example",
            new DateTimeOffset(2024, 5, 1, 0, 0, 0, TimeSpan.Zero), "negative",
            MatchedTerms: ["indicted"], Category: "Criminal proceedings", Context: "Jane Roe was indicted on Tuesday.", Provider: "Google News");
        var mention = article with { Title = "Roe cafe review", Url = new Uri("https://news.example/2"), Tone = "mention", Context = null };
        var media = new AdverseMediaResult("Adverse media (multi-source)", true, 2, 1, [mention, article], null, null, 1);

        var lines = MerchantIntelligence.Platform.Assessment.AssessmentComposer.MediaNarrative(Report(media)).ToList();
        var evidence = MerchantIntelligence.Platform.Assessment.AssessmentComposer.MediaEvidence(Report(media)).ToList();

        var line = Assert.Single(lines);
        Assert.Contains("Jane Roe (Owner)", line);
        Assert.Contains("indicted ×1", line);
        Assert.Contains("\"Jane Roe was indicted on Tuesday.\" (news.example, 2024-05-01, via Google News)", line);
        Assert.Equal(2, evidence.Count);
        Assert.Equal("negative", evidence[0].Tone);
        Assert.Equal("Criminal proceedings", evidence[0].Category);
        Assert.Equal("https://news.example/1", evidence[0].Url);
    }

    [Fact]
    public void Failed_lookup_yields_no_narrative_or_evidence()
    {
        var media = new AdverseMediaResult("Adverse media (multi-source)", false, 0, 0, [], "All adverse-media sources failed");
        Assert.Empty(MerchantIntelligence.Platform.Assessment.AssessmentComposer.MediaNarrative(Report(media)));
        Assert.Empty(MerchantIntelligence.Platform.Assessment.AssessmentComposer.MediaEvidence(Report(media)));
    }
}
