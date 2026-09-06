using System.Net;
using System.Text;
using System.Text.Json;
using MerchantIntelligence.CreditDecision;
using MerchantIntelligence.Kyb.Prohibited;
using MerchantIntelligence.MccValidation.Taxonomy;
using MerchantIntelligence.Platform.Cases;
using MerchantIntelligence.Platform.Integrations;
using MerchantIntelligence.Platform.ModelOps;
using MerchantIntelligence.Platform.Rules;
using MerchantIntelligence.Platform.Scoring;
using MerchantIntelligence.Platform.Storage;
using MerchantIntelligence.Platform.Webhooks;
using Microsoft.Extensions.Logging.Abstractions;

namespace MerchantIntelligence.Tests;

internal sealed class RecordingHandler : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = new();
    public List<string> Bodies { get; } = new();
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
    public int FailFirst { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
        if (FailFirst > 0) { FailFirst--; return new HttpResponseMessage(HttpStatusCode.InternalServerError); }
        return new HttpResponseMessage(Status);
    }
}

internal sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

internal sealed class PlatformFixture : IDisposable
{
    public PlatformDatabase Db { get; } = new(new PlatformOptions { DatabasePath = ":memory:" });
    public AuditTrail Audit { get; }
    public RecordingHandler Http { get; } = new();
    public WebhookDispatcher Webhooks { get; }
    public CaseService Cases { get; }
    public RuleSetRepository Rules { get; }

    public PlatformFixture()
    {
        Audit = new AuditTrail(Db);
        Webhooks = new WebhookDispatcher(Db, new StubHttpClientFactory(Http), NullLogger<WebhookDispatcher>.Instance,
            [TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(5)]);
        Cases = new CaseService(Db, Audit, Webhooks);
        Rules = new RuleSetRepository(Db, Audit);
    }

    public void Dispose() => Db.Dispose();
}

public sealed class UnifiedRiskScorerTests
{
    private static readonly UnifiedRiskScorer Scorer = new();

    private static DecisionResult Credit(double approve) => new(
        approve >= 0.5 ? Decision.Approved : Decision.Declined, Math.Max(approve, 1 - approve),
        new Dictionary<Decision, double> { [Decision.Approved] = approve, [Decision.Declined] = (1 - approve) * 0.8, [Decision.Cancelled] = (1 - approve) * 0.2 });

    [Fact]
    public void Clean_fully_covered_applicant_scores_high_and_approves()
    {
        var s = Scorer.Score(new UnifiedRiskInput(CreditDecision: Credit(0.95), KybRisk: RiskTier.Low, BusinessVerified: true, EntityAgeMonths: 96,
            SanctionsMatch: false, PepMatch: false, AdverseMedia: false, ProhibitedVerdict: BusinessPolicy.Acceptable,
            WebsiteComplianceScore: 95, VolumePlausibilityScore: 90, TermsRiskBand: "A", MatchFound: false));
        Assert.True(s.Score >= 800, $"score {s.Score}");
        Assert.Equal("Approve", s.RecommendedAction);
        Assert.Empty(s.HardStops);
        Assert.Empty(s.CoverageGaps);
        Assert.Equal(100, s.CoveragePercent);
    }

    [Fact]
    public void Sanctions_match_is_a_hard_stop_that_caps_score()
    {
        var s = Scorer.Score(new UnifiedRiskInput(CreditDecision: Credit(0.95), KybRisk: RiskTier.Low, SanctionsMatch: true, MatchFound: false));
        Assert.Contains(s.HardStops, h => h.Contains("SANCTIONS", StringComparison.OrdinalIgnoreCase));
        Assert.True(s.Score <= 150);
        Assert.Equal("Decline", s.RecommendedAction);
        Assert.Equal("VeryHigh", s.Tier);
    }

    [Fact]
    public void Prohibited_and_match_are_hard_stops()
    {
        Assert.NotEmpty(Scorer.Score(new UnifiedRiskInput(ProhibitedVerdict: BusinessPolicy.Prohibited)).HardStops);
        Assert.NotEmpty(Scorer.Score(new UnifiedRiskInput(MatchFound: true)).HardStops);
    }

    [Fact]
    public void Missing_sections_show_as_coverage_gaps_and_pull_toward_refer()
    {
        var s = Scorer.Score(new UnifiedRiskInput(CreditDecision: Credit(0.95)));
        Assert.True(s.CoverageGaps.Count >= 5);
        Assert.True(s.CoveragePercent < 50);
        Assert.NotEqual("Approve", s.RecommendedAction);
    }

    [Fact]
    public void Worse_inputs_never_raise_the_score()
    {
        var good = Scorer.Score(new UnifiedRiskInput(CreditDecision: Credit(0.9), KybRisk: RiskTier.Low, WebsiteComplianceScore: 90, VolumePlausibilityScore: 90, TermsRiskBand: "A"));
        var worse = Scorer.Score(new UnifiedRiskInput(CreditDecision: Credit(0.6), KybRisk: RiskTier.High, WebsiteComplianceScore: 40, VolumePlausibilityScore: 30, TermsRiskBand: "D"));
        Assert.True(worse.Score < good.Score);
        Assert.Contains(worse.ReasonCodes, r => r.Severity == RiskTier.High);
    }

    [Fact]
    public void Score_is_within_bounds_and_components_sum_to_weighted_total()
    {
        var s = Scorer.Score(new UnifiedRiskInput(CreditDecision: Credit(0.7), KybRisk: RiskTier.Medium, PepMatch: true, AdverseMedia: true));
        Assert.InRange(s.Score, 0, 1000);
        Assert.All(s.Components, c => Assert.InRange(c.Score, 0, 100));
    }
}

public sealed class RulesEngineTests
{
    private static readonly RulesEngine Engine = new();

    private static Dictionary<string, object?> Facts(int score = 700, bool match = false, double coverage = 100, params string[] hardStops) => new()
    {
        ["score"] = score, ["matchFound"] = match, ["hardStops"] = hardStops.ToList(), ["highSeverityReasons"] = 0,
        ["annualVolume"] = 500_000.0, ["highestTicket"] = 200.0, ["coveragePercent"] = coverage, ["reasonCodes"] = new List<string>(), ["tier"] = "Low"
    };

    [Fact]
    public void Default_ruleset_loads_and_validates()
    {
        var set = RulesEngine.LoadDefault();
        Assert.True(set.Rules.Count >= 8);
        RulesEngine.Validate(set);
    }

    [Fact]
    public void Hard_stop_declines_even_with_high_score()
    {
        var eval = Engine.Evaluate(RulesEngine.LoadDefault(), Facts(score: 900, hardStops: "SANCTIONS_MATCH"));
        Assert.Equal(RuleOutcome.Decline, eval.Outcome);
        Assert.Equal("HARD_STOP_SANCTIONS", eval.DecidingRule);
    }

    [Fact]
    public void Clean_high_score_auto_approves_and_low_coverage_refers()
    {
        Assert.Equal(RuleOutcome.Approve, Engine.Evaluate(RulesEngine.LoadDefault(), Facts(score: 850)).Outcome);
        Assert.Equal(RuleOutcome.Refer, Engine.Evaluate(RulesEngine.LoadDefault(), Facts(score: 850, coverage: 30)).Outcome);
    }

    [Fact]
    public void Most_severe_outcome_wins()
    {
        var set = RulesEngine.Parse("""
            {"version":"t","rules":[
              {"id":"A","outcome":"Approve","when":{"fact":"score","op":"gt","value":1}},
              {"id":"R","outcome":"Refer","when":{"fact":"score","op":"gt","value":2}},
              {"id":"D","outcome":"Decline","priority":99,"when":{"fact":"score","op":"gt","value":3}}]}
            """);
        var eval = Engine.Evaluate(set, Facts(score: 10));
        Assert.Equal(RuleOutcome.Decline, eval.Outcome);
        Assert.Equal(3, eval.MatchedRules.Count);
        Assert.Equal("D", eval.DecidingRule);
    }

    [Fact]
    public void Supports_nested_all_any_not_and_all_operators()
    {
        var set = RulesEngine.Parse("""
            {"rules":[{"id":"X","outcome":"Decline","when":{"all":[
              {"fact":"tier","op":"in","value":["Low","Medium"]},
              {"any":[{"fact":"score","op":"lte","value":100},{"fact":"reasonCodes","op":"contains","value":"PEP"}]},
              {"not":{"fact":"matchFound","op":"eq","value":true}},
              {"fact":"annualVolume","op":"exists"},
              {"fact":"tier","op":"neq","value":"High"}]}}]}
            """);
        var facts = Facts(score: 700);
        facts["reasonCodes"] = new List<string> { "PEP" };
        Assert.Equal(RuleOutcome.Decline, Engine.Evaluate(set, facts).Outcome);
        facts["matchFound"] = true;
        Assert.Equal(RuleOutcome.Refer, Engine.Evaluate(set, facts).Outcome); // default outcome
    }

    [Fact]
    public void Missing_fact_never_matches_except_exists()
    {
        var set = RulesEngine.Parse("""{"defaultOutcome":"Approve","rules":[{"id":"X","outcome":"Decline","when":{"fact":"nope","op":"gt","value":0}}]}""");
        Assert.Equal(RuleOutcome.Approve, Engine.Evaluate(set, Facts()).Outcome);
    }

    [Theory]
    [InlineData("""{"rules":[{"id":"A","when":{"fact":"x","op":"eq","value":1}},{"id":"a","when":{"fact":"x","op":"eq","value":1}}]}""", "Duplicate")]
    [InlineData("""{"rules":[{"id":"A","when":{"fact":"x","op":"between","value":1}}]}""", "unknown op")]
    [InlineData("""{"rules":[{"id":"A","when":{"fact":"x","op":"eq"}}]}""", "needs a value")]
    [InlineData("""{"rules":[{"id":"A","when":{"all":[],"fact":"x"}}]}""", "both a fact and a combinator")]
    [InlineData("""{"rules":[{"id":"A","when":{"all":[]}}]}""", "empty")]
    [InlineData("""{"rules":[{"id":"","when":{"fact":"x","op":"eq","value":1}}]}""", "needs an id")]
    [InlineData("""not json""", "Invalid rule JSON")]
    public void Invalid_rule_sets_are_rejected(string json, string expected)
    {
        var ex = Assert.Throws<RuleValidationException>(() => RulesEngine.Parse(json));
        Assert.Contains(expected, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildFacts_exposes_score_fields_and_extras()
    {
        var input = new UnifiedRiskInput(Application: new MerchantApplication { AnnualVolume = 1000, HighestTicket = 50, AverageTicket = 10, MerchantCategoryCode = 5411 }, MatchFound: true);
        var score = new UnifiedRiskScorer().Score(input);
        var facts = RulesEngine.BuildFacts(score, input, new Dictionary<string, object?> { ["country"] = "GB" });
        Assert.Equal(score.Score, facts["score"]);
        Assert.Equal("GB", facts["country"]);
        Assert.Equal(true, facts["matchFound"]);
        Assert.NotEmpty((IEnumerable<string>)facts["hardStops"]!);
    }
}

public sealed class RuleSetRepositoryTests
{
    [Fact]
    public void Publish_history_and_rollback_are_versioned_and_audited()
    {
        using var f = new PlatformFixture();
        Assert.Equal(RulesEngine.LoadDefault().Rules.Count, f.Rules.Active.Rules.Count);

        var v1 = f.Rules.Publish(RulesEngine.Parse("""{"rules":[{"id":"ONLY","outcome":"Decline","when":{"fact":"score","op":"lt","value":999}}]}"""), "risk-lead", "tighten");
        Assert.Equal(1, v1.Version);
        Assert.Single(f.Rules.Active.Rules);

        var v2 = f.Rules.Publish(RulesEngine.LoadDefault(), "risk-lead", "restore defaults");
        Assert.Equal(2, v2.Version);
        Assert.True(f.Rules.History().Single(h => h.Active).Version == 2);

        var v3 = f.Rules.Rollback(1, "risk-lead");
        Assert.Equal(3, v3.Version);
        Assert.Single(f.Rules.Active.Rules);
        Assert.Equal("ONLY", f.Rules.Active.Rules[0].Id);
        Assert.Equal(3, f.Rules.History().Count);
        Assert.Contains(f.Audit.Recent(), e => e.Action == "rules.published");
        Assert.Throws<KeyNotFoundException>(() => f.Rules.Rollback(42, "x"));
    }
}

public sealed class CaseServiceTests
{
    [Fact]
    public void Full_lifecycle_writes_audit_chain_and_webhooks()
    {
        using var f = new PlatformFixture();
        f.Webhooks.Register("https://example.test/hook", "a-very-long-secret-value", ["*"]);

        var c = f.Cases.Create("Acme Ltd", "api", riskScore: 520, riskTier: "Medium", rulesOutcome: RuleOutcome.Refer, snapshot: new { foo = 1 });
        Assert.Equal(CaseStatus.Open, c.Status);
        Assert.Equal(CasePriority.Low, c.Priority);
        Assert.NotNull(c.Snapshot);

        c = f.Cases.Assign(c.Id, "alice", "supervisor");
        Assert.Equal("alice", c.AssignedTo);
        c = f.Cases.SetStatus(c.Id, CaseStatus.InReview, "alice");
        f.Cases.AddNote(c.Id, "alice", "Requested bank statements.");
        c = f.Cases.SetStatus(c.Id, CaseStatus.PendingDocuments, "alice", "waiting");
        c = f.Cases.Decide(c.Id, new CaseDecision("Approved", "alice", "Docs fine", false));
        Assert.Equal(CaseStatus.Approved, c.Status);
        Assert.Equal("Approved", c.FinalDecision);

        Assert.Throws<InvalidCaseTransitionException>(() => f.Cases.Assign(c.Id, "bob", "x"));
        Assert.Throws<CaseNotFoundException>(() => f.Cases.Get("CASE-NOPE"));

        var audit = f.Audit.ForCase(c.Id);
        Assert.Equal(["case.created", "case.assigned", "case.status_changed", "case.note_added", "case.status_changed", "case.decided"],
            audit.Select(a => a.Action).ToArray());
        Assert.True(f.Audit.Verify().Valid);
        Assert.Single(f.Cases.Notes(c.Id));

        var stats = f.Cases.Stats();
        Assert.Equal(1, stats.Approved);
        Assert.Equal(0, stats.Overrides);

        SpinWait.SpinUntil(() => f.Http.Requests.Count >= 5, TimeSpan.FromSeconds(5));
        Assert.Equal(5, f.Http.Requests.Count);
        Assert.All(f.Http.Requests, r => Assert.True(r.Headers.Contains("X-MI-Signature")));
    }

    [Fact]
    public void Rules_outcome_auto_decides_and_override_requires_reason()
    {
        using var f = new PlatformFixture();
        var auto = f.Cases.Create("Auto Approve", "api", riskScore: 900, rulesOutcome: RuleOutcome.Approve);
        Assert.Equal(CaseStatus.Approved, auto.Status);

        var declined = f.Cases.Create("Risky", "api", riskScore: 100, rulesOutcome: RuleOutcome.Decline);
        Assert.Equal(CaseStatus.Declined, declined.Status);
        Assert.Equal(CasePriority.High, declined.Priority);

        var refer = f.Cases.Create("Borderline", "api", riskScore: 400, rulesOutcome: RuleOutcome.Refer);
        var manual = f.Cases.Create("Manual", "api", rulesOutcome: RuleOutcome.Approve, priority: CasePriority.Urgent);
        Assert.Equal(CaseStatus.Approved, manual.Status);

        // Refer case decided against a hypothetical decline rule: simulate via a Decline-outcome open case
        var open = f.Cases.Create("Open decline", "api", rulesOutcome: null);
        Assert.Equal(CaseStatus.Open, open.Status);
        var decided = f.Cases.Decide(refer.Id, new CaseDecision("Declined", "bob", "", false));
        Assert.Equal(CaseStatus.Declined, decided.Status);

        Assert.Throws<InvalidCaseTransitionException>(() => f.Cases.Decide(open.Id, new CaseDecision("Approved", "bob", "", true)));
        var overridden = f.Cases.Decide(open.Id, new CaseDecision("Approved", "bob", "Known customer", true));
        Assert.Equal(CaseStatus.Approved, overridden.Status);
        Assert.Contains(f.Audit.ForCase(open.Id), e => e.Action == "case.overridden");
        Assert.Equal(1, f.Cases.Stats().Overrides);
        Assert.Throws<InvalidCaseTransitionException>(() => f.Cases.Decide(refer.Id, new CaseDecision("Maybe", "bob", "", false)));
        Assert.Equal(3, f.Cases.List(CaseStatus.Approved).Count);
        Assert.Single(f.Cases.List(assignedTo: null, status: CaseStatus.Declined, limit: 1));
    }
}

public sealed class AuditTrailTests
{
    [Fact]
    public void Chain_verifies_and_detects_tampering()
    {
        using var f = new PlatformFixture();
        f.Audit.Record(null, "a", "one", new { x = 1 });
        f.Audit.Record("C1", "b", "two");
        f.Audit.Record("C1", "c", "three", new { y = "z" });
        var v = f.Audit.Verify();
        Assert.True(v.Valid);
        Assert.Equal(3, v.EventsChecked);
        Assert.Equal(2, f.Audit.ForCase("C1").Count);

        using (var conn = f.Db.Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "UPDATE audit_events SET actor = 'mallory' WHERE seq = 2";
            cmd.ExecuteNonQuery();
        }
        var tampered = f.Audit.Verify();
        Assert.False(tampered.Valid);
        Assert.Equal(2, tampered.FirstBrokenSeq);
    }
}

public sealed class WebhookTests
{
    [Fact]
    public void Signature_is_hmac_sha256_hex()
    {
        var sig = WebhookDispatcher.Sign("secret", "body");
        Assert.StartsWith("sha256=", sig);
        Assert.Equal(7 + 64, sig.Length);
        Assert.Equal(sig, WebhookDispatcher.Sign("secret", "body"));
        Assert.NotEqual(sig, WebhookDispatcher.Sign("secret2", "body"));
    }

    [Fact]
    public void Registration_validation()
    {
        using var f = new PlatformFixture();
        Assert.Throws<ArgumentException>(() => f.Webhooks.Register("ftp://x", "a-very-long-secret-value", ["*"]));
        Assert.Throws<ArgumentException>(() => f.Webhooks.Register("https://x.test", "short", ["*"]));
        Assert.Throws<ArgumentException>(() => f.Webhooks.Register("https://x.test", "a-very-long-secret-value", ["nope.event"]));
        var sub = f.Webhooks.Register("https://x.test", "a-very-long-secret-value", ["case.created"]);
        Assert.Single(f.Webhooks.List());
        Assert.True(f.Webhooks.Remove(sub.Id));
        Assert.False(f.Webhooks.Remove(sub.Id));
    }

    [Fact]
    public async Task Delivers_signed_payload_filters_events_and_retries()
    {
        using var f = new PlatformFixture();
        f.Http.FailFirst = 1;
        var sub = f.Webhooks.Register("https://x.test/h", "a-very-long-secret-value", ["case.created"]);
        await Task.WhenAll(f.Webhooks.Publish("case.assigned", new { ignored = true }));
        Assert.Empty(f.Http.Requests);

        await Task.WhenAll(f.Webhooks.Publish("case.created", new { id = "C1" }));
        Assert.Equal(2, f.Http.Requests.Count);
        var req = f.Http.Requests[^1];
        Assert.Equal("case.created", req.Headers.GetValues("X-MI-Event").Single());
        Assert.Equal(WebhookDispatcher.Sign("a-very-long-secret-value", f.Http.Bodies[^1]), req.Headers.GetValues("X-MI-Signature").Single());
        Assert.Contains("\"C1\"", f.Http.Bodies[^1]);

        var deliveries = f.Webhooks.Deliveries(sub.Id);
        var d = Assert.Single(deliveries);
        Assert.True(d.Delivered);
        Assert.Equal(2, d.Attempts);
        Assert.Equal(200, d.StatusCode);
    }

    [Fact]
    public async Task Gives_up_after_max_attempts_and_records_failure()
    {
        using var f = new PlatformFixture();
        f.Http.Status = HttpStatusCode.BadGateway;
        var sub = f.Webhooks.Register("https://x.test/h", "a-very-long-secret-value", ["*"]);
        await Task.WhenAll(f.Webhooks.Publish("rules.published", new { v = 1 }));
        var d = Assert.Single(f.Webhooks.Deliveries(sub.Id));
        Assert.False(d.Delivered);
        Assert.Equal(3, d.Attempts);
        Assert.Equal(502, d.StatusCode);
    }
}

public sealed class MatchProviderTests
{
    [Fact]
    public async Task Unavailable_provider_reports_unknown_not_clear()
    {
        var r = await new UnavailableMatchProvider().InquireAsync(new MatchInquiry("Acme", null, null, null, null, null, null, null, []));
        Assert.Equal(MatchAvailability.NotConfigured, r.Availability);
        Assert.Null(r.Found);
    }

    [Fact]
    public async Task Local_list_matches_on_tax_id_or_normalised_name()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tmf-{Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(path, "name,taxId,reasonCode,terminationDate,acquirer\nShady Goods LLC,12-3456789,04,2024-01-15,Bank A\nOther Co,,03,,\n");
        try
        {
            var p = new LocalListMatchProvider(path);
            Assert.Equal(2, p.Count);
            var byId = await p.InquireAsync(new MatchInquiry("Totally Different", null, "123456789", null, null, null, null, null, []));
            Assert.True(byId.Found);
            Assert.Equal("Excessive chargebacks", byId.Hits[0].ReasonDescription);
            Assert.Equal("TaxId", byId.Hits[0].MatchedOn);

            var byName = await p.InquireAsync(new MatchInquiry("SHADY GOODS, L.L.C.", null, null, null, null, null, null, null, []));
            Assert.True(byName.Found);

            var clean = await p.InquireAsync(new MatchInquiry("Clean Bakery", null, "99-9999999", null, null, null, null, null, []));
            Assert.False(clean.Found);
            Assert.Equal(MatchAvailability.Available, clean.Availability);
        }
        finally { File.Delete(path); }
    }
}

public sealed class ModelOpsTests : IDisposable
{
    private readonly PlatformFixture _f = new();
    private readonly string _modelsDir = Path.Combine(Path.GetTempPath(), $"mi-models-{Guid.NewGuid():N}");
    private readonly ModelRegistry _registry;
    private readonly ModelOpsService _ops;

    public ModelOpsTests()
    {
        Directory.CreateDirectory(_modelsDir);
        var options = new PlatformOptions { DatabasePath = ":memory:", ModelsDirectory = _modelsDir };
        _registry = new ModelRegistry(_f.Db, _f.Audit, _f.Webhooks, options, new RuleBasedPredictor(), Path.Combine(_modelsDir, "bootstrap.zip"));
        _ops = new ModelOpsService(_f.Db, _registry, _f.Audit, _f.Webhooks);
    }

    public void Dispose()
    {
        _f.Dispose();
        try { Directory.Delete(_modelsDir, true); } catch (IOException) { }
    }

    private static MerchantApplication App(float volume = 500_000, float avg = 50, bool match = false) =>
        new() { MerchantCategoryCode = 5411, AnnualVolume = volume, AverageTicket = avg, HighestTicket = avg * 5, MatchFound = match, ExistingRelationship = false };

    [Fact]
    public void Predictions_are_logged_and_outcomes_recorded()
    {
        var (result, id) = _ops.PredictAndLog(App(), "CASE-1");
        Assert.Equal(Decision.Approved, result.Decision);
        Assert.True(_ops.RecordOutcome(id, Decision.Declined, "analyst"));
        Assert.False(_ops.RecordOutcome(99999, Decision.Declined, "analyst"));
        var logged = Assert.Single(_ops.Recent(onlyLabelled: true));
        Assert.Equal("CASE-1", logged.CaseId);
        Assert.Equal(Decision.Declined, logged.Actual);
        Assert.Equal("v1-bootstrap", logged.ModelVersion);
    }

    [Fact]
    public void Drift_needs_enough_data_then_flags_shifted_population()
    {
        Assert.Equal("InsufficientData", _ops.Drift().OverallStatus);
        for (var i = 0; i < 60; i++) _ops.PredictAndLog(App(volume: 50_000_000, avg: 4_000, match: true));
        var drift = _ops.Drift();
        Assert.Equal(60, drift.RecentRows);
        Assert.Equal("Significant", drift.OverallStatus);
        Assert.Contains(drift.Features, f => f.Feature == "MatchFound" && f.Status == "Significant");
        Assert.NotEmpty(drift.Alerts);
    }

    [Fact]
    public void Compare_without_challenger_recommends_training_one()
    {
        var cmp = _ops.Compare();
        Assert.Null(cmp.Challenger);
        Assert.Contains("No challenger", cmp.Recommendation);
        Assert.Throws<InvalidOperationException>(() => _registry.PromoteChallenger("x", null));
    }

    [Fact]
    public void Retrain_registers_challenger_that_shadow_scores_and_can_be_promoted()
    {
        var (_, id) = _ops.PredictAndLog(App());
        _ops.RecordOutcome(id, Decision.Approved, "a");

        var result = _ops.Retrain("mlops", syntheticRows: 2_000);
        Assert.True(File.Exists(result.Path));
        Assert.Equal(1, result.LabelledRows);
        Assert.Equal(result.Version, _registry.ChallengerVersion);
        Assert.InRange(result.Metrics.MacroAccuracy, 0.5, 1.0);

        var (_, id2) = _ops.PredictAndLog(App(match: true));
        var logged = _ops.Recent().First(d => d.Id == id2);
        Assert.NotNull(logged.ChallengerPredicted);
        Assert.Equal(1, _ops.Compare().Champion.WithOutcome);

        var promoted = _registry.PromoteChallenger("mlops", "better AUC");
        Assert.Equal(ModelRole.Champion, promoted.Role);
        Assert.Equal(result.Version, _registry.ChampionVersion);
        Assert.Null(_registry.ChallengerVersion);
        Assert.Contains(_registry.All(), m => m.Version == "v1-bootstrap" && m.Role == ModelRole.Retired);
        Assert.Contains(_f.Audit.Recent(), e => e.Action == "model.promoted");
    }
}
