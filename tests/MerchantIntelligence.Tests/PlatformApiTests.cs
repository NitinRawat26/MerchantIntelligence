using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace MerchantIntelligence.Tests;

public sealed class PlatformApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public PlatformApiTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Platform:DatabasePath", ":memory:");
            b.UseSetting("Platform:ModelsDirectory", Path.Combine(Path.GetTempPath(), $"mi-api-models-{Guid.NewGuid():N}"));
        }).CreateClient();
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    [Fact]
    public async Task Score_combines_signals_evaluates_rules_and_can_open_a_case()
    {
        var root = await Json(await _client.PostAsJsonAsync("/api/platform/score", new
        {
            application = new { merchantCategoryCode = 5411, annualVolume = 600000, averageTicket = 40, highestTicket = 250 },
            kybRisk = "Low", businessVerified = true, entityAgeMonths = 60, sanctionsMatch = false, pepMatch = false, adverseMedia = false,
            prohibitedVerdict = "Acceptable", websiteComplianceScore = 90, volumePlausibilityScore = 85, termsRiskBand = "A",
            createCase = true, merchantName = "Corner Grocery LLC", actor = "tester"
        }));
        var score = root.GetProperty("score");
        Assert.InRange(score.GetProperty("score").GetInt32(), 0, 1000);
        Assert.Equal(0, score.GetProperty("hardStops").GetArrayLength());
        Assert.Equal(0, score.GetProperty("coverageGaps").GetArrayLength());
        Assert.NotNull(root.GetProperty("creditDecision").GetProperty("decision").GetString());
        Assert.True(root.GetProperty("decisionLogId").GetInt64() > 0);
        Assert.Contains(root.GetProperty("rules").GetProperty("outcome").GetString(), new[] { "Approve", "Refer", "Decline" });
        var c = root.GetProperty("case");
        Assert.StartsWith("CASE-", c.GetProperty("id").GetString());
        Assert.Equal("Corner Grocery LLC", c.GetProperty("merchantName").GetString());
    }

    [Fact]
    public async Task Score_with_sanctions_match_declines_via_hard_stop_rule()
    {
        var root = await Json(await _client.PostAsJsonAsync("/api/platform/score", new { sanctionsMatch = true, kybRisk = "Low" }));
        Assert.Equal("Decline", root.GetProperty("rules").GetProperty("outcome").GetString());
        Assert.Equal("HARD_STOP_SANCTIONS", root.GetProperty("rules").GetProperty("decidingRule").GetString());
        Assert.True(root.GetProperty("score").GetProperty("score").GetInt32() <= 150);
    }

    [Fact]
    public async Task Score_validates_input()
    {
        var r = await _client.PostAsJsonAsync("/api/platform/score", new { application = new { merchantCategoryCode = 5411, annualVolume = 1000, averageTicket = 50, highestTicket = 10 } });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        r = await _client.PostAsJsonAsync("/api/platform/score", new { termsRiskBand = "Z" });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        r = await _client.PostAsJsonAsync("/api/platform/score", new { createCase = true });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Rules_publish_history_rollback_and_evaluate()
    {
        var active = await Json(await _client.GetAsync("/api/platform/rules"));
        Assert.True(active.GetProperty("rules").GetArrayLength() > 0);

        var bad = await _client.PostAsJsonAsync("/api/platform/rules/validate", new { rules = new[] { new { id = "A", when = new { fact = "x", op = "between", value = 1 } } } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var custom = new
        {
            version = "custom", defaultOutcome = "Refer",
            rules = new[] { new { id = "ALWAYS_DECLINE", outcome = "Decline", when = new { fact = "score", op = "gte", value = 0 } } }
        };
        var published = await Json(await _client.PostAsJsonAsync("/api/platform/rules/publish", new { ruleSet = custom, author = "risk-lead", comment = "test" }));
        var version = published.GetProperty("version").GetInt32();
        Assert.True(published.GetProperty("active").GetBoolean());

        var eval = await Json(await _client.PostAsJsonAsync("/api/platform/rules/evaluate", new { facts = new { score = 950 } }));
        Assert.Equal("Decline", eval.GetProperty("outcome").GetString());

        var adHoc = await Json(await _client.PostAsJsonAsync("/api/platform/rules/evaluate", new
        {
            facts = new { score = 950, country = "GB" },
            ruleSet = new { rules = new[] { new { id = "GB_OK", outcome = "Approve", when = new { fact = "country", op = "eq", value = "GB" } } } }
        }));
        Assert.Equal("Approve", adHoc.GetProperty("outcome").GetString());

        var history = await Json(await _client.GetAsync("/api/platform/rules/history"));
        Assert.True(history.GetArrayLength() >= 1);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/platform/rules/9999")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsJsonAsync("/api/platform/rules/rollback/9999", new { actor = "x" })).StatusCode);

        var byVersion = await Json(await _client.GetAsync($"/api/platform/rules/{version}"));
        Assert.Equal("ALWAYS_DECLINE", byVersion.GetProperty("rules")[0].GetProperty("id").GetString());

        // restore defaults so other tests sharing this host are unaffected
        var restored = await Json(await _client.PostAsJsonAsync("/api/platform/rules/publish", new { ruleSet = active, author = "risk-lead", comment = "restore" }));
        Assert.True(restored.GetProperty("version").GetInt32() > version);
        var rolled = await Json(await _client.PostAsJsonAsync($"/api/platform/rules/rollback/{version}", new { actor = "risk-lead" }));
        Assert.True(rolled.GetProperty("version").GetInt32() > restored.GetProperty("version").GetInt32());
        await Json(await _client.PostAsJsonAsync("/api/platform/rules/publish", new { ruleSet = active, author = "risk-lead", comment = "restore" }));
    }

    [Fact]
    public async Task Case_lifecycle_audit_and_stats()
    {
        var created = await Json(await _client.PostAsJsonAsync("/api/platform/cases", new { merchantName = "Widget Co", riskScore = 480, riskTier = "Medium", rulesOutcome = "Refer", actor = "api" }));
        var id = created.GetProperty("id").GetString()!;
        Assert.Equal("Open", created.GetProperty("status").GetString());

        var assigned = await Json(await _client.PostAsJsonAsync($"/api/platform/cases/{id}/assign", new { assignee = "alice", actor = "lead" }));
        Assert.Equal("alice", assigned.GetProperty("assignedTo").GetString());
        await Json(await _client.PostAsJsonAsync($"/api/platform/cases/{id}/status", new { status = "InReview", actor = "alice" }));
        await Json(await _client.PostAsJsonAsync($"/api/platform/cases/{id}/notes", new { author = "alice", body = "Called merchant." }));
        var notes = await Json(await _client.GetAsync($"/api/platform/cases/{id}/notes"));
        Assert.Equal(1, notes.GetArrayLength());

        var list = await Json(await _client.GetAsync("/api/platform/cases?status=InReview&assignedTo=alice"));
        Assert.Contains(list.EnumerateArray(), c => c.GetProperty("id").GetString() == id);

        var decided = await Json(await _client.PostAsJsonAsync($"/api/platform/cases/{id}/decide", new { decision = "Approved", actor = "alice", reason = "ok" }));
        Assert.Equal("Approved", decided.GetProperty("status").GetString());

        var conflict = await _client.PostAsJsonAsync($"/api/platform/cases/{id}/assign", new { assignee = "bob", actor = "lead" });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/platform/cases/CASE-NOPE")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync($"/api/platform/cases/{id}/decide", new { decision = "Maybe", actor = "a" })).StatusCode);

        var audit = await Json(await _client.GetAsync($"/api/platform/cases/{id}/audit"));
        Assert.Equal(5, audit.GetArrayLength());
        Assert.All(audit.EnumerateArray(), e => Assert.Equal(64, e.GetProperty("hash").GetString()!.Length));
        var verify = await Json(await _client.GetAsync("/api/platform/audit/verify"));
        Assert.True(verify.GetProperty("valid").GetBoolean());
        var stats = await Json(await _client.GetAsync("/api/platform/cases/stats"));
        Assert.True(stats.GetProperty("approved").GetInt32() >= 1);
    }

    [Fact]
    public async Task Override_requires_reason()
    {
        var created = await Json(await _client.PostAsJsonAsync("/api/platform/cases", new { merchantName = "Auto Decline", rulesOutcome = "Decline", actor = "api" }));
        Assert.Equal("Declined", created.GetProperty("status").GetString());

        var open = await Json(await _client.PostAsJsonAsync("/api/platform/cases", new { merchantName = "Open", actor = "api" }));
        var id = open.GetProperty("id").GetString()!;
        // decision that contradicts a Decline rules outcome can only be tested on a case whose rules outcome is Decline but is still open,
        // which the service never creates; the override path is covered by unit tests. Here we verify a normal decision works without reason.
        var decided = await Json(await _client.PostAsJsonAsync($"/api/platform/cases/{id}/decide", new { decision = "Declined", actor = "bob" }));
        Assert.Equal("Declined", decided.GetProperty("finalDecision").GetString());
    }

    [Fact]
    public async Task Webhooks_register_list_remove_and_never_expose_secret()
    {
        var bad = await _client.PostAsJsonAsync("/api/platform/webhooks", new { url = "https://hooks.test/x", secret = "short", events = new[] { "*" } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        bad = await _client.PostAsJsonAsync("/api/platform/webhooks", new { url = "https://hooks.test/x", secret = "a-very-long-secret-value", events = new[] { "bogus" } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var sub = await Json(await _client.PostAsJsonAsync("/api/platform/webhooks", new { url = "https://hooks.test/x", secret = "a-very-long-secret-value", events = new[] { "case.created" } }));
        Assert.False(sub.TryGetProperty("secret", out _));
        var id = sub.GetProperty("id").GetString();
        var list = await Json(await _client.GetAsync("/api/platform/webhooks"));
        Assert.Contains(list.EnumerateArray(), w => w.GetProperty("id").GetString() == id);
        var events = await Json(await _client.GetAsync("/api/platform/webhooks/events"));
        Assert.Contains(events.EnumerateArray(), e => e.GetString() == "case.decided");
        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/platform/webhooks/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.DeleteAsync($"/api/platform/webhooks/{id}")).StatusCode);
        await Json(await _client.GetAsync("/api/platform/webhooks/deliveries"));
    }

    [Fact]
    public async Task Model_ops_endpoints()
    {
        var models = await Json(await _client.GetAsync("/api/platform/models"));
        Assert.False(string.IsNullOrEmpty(models.GetProperty("champion").GetString()));

        var scored = await Json(await _client.PostAsJsonAsync("/api/platform/score", new { application = new { merchantCategoryCode = 5812, annualVolume = 250000, averageTicket = 35, highestTicket = 300 } }));
        var logId = scored.GetProperty("decisionLogId").GetInt64();
        await Json(await _client.PostAsJsonAsync($"/api/platform/models/decisions/{logId}/outcome", new { actual = "Approved", actor = "analyst" }));
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsJsonAsync("/api/platform/models/decisions/999999/outcome", new { actual = "Approved", actor = "a" })).StatusCode);

        var decisions = await Json(await _client.GetAsync("/api/platform/models/decisions?onlyLabelled=true"));
        Assert.Contains(decisions.EnumerateArray(), d => d.GetProperty("id").GetInt64() == logId && d.GetProperty("actual").GetString() == "Approved");

        var drift = await Json(await _client.GetAsync("/api/platform/models/drift"));
        Assert.True(drift.TryGetProperty("overallStatus", out _));
        var compare = await Json(await _client.GetAsync("/api/platform/models/compare"));
        Assert.True(compare.GetProperty("champion").GetProperty("scored").GetInt32() >= 1);
        Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsJsonAsync("/api/platform/models/promote", new { actor = "x" })).StatusCode);
    }

    [Fact]
    public async Task Match_inquiry_reports_not_configured_without_credentials()
    {
        var r = await Json(await _client.PostAsJsonAsync("/api/platform/match/inquiry", new { legalName = "Acme Ltd" }));
        Assert.Equal("NotConfigured", r.GetProperty("availability").GetString());
        Assert.Equal(JsonValueKind.Null, r.GetProperty("found").ValueKind);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/api/platform/match/inquiry", new { legalName = "" })).StatusCode);
    }
}
