using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MerchantIntelligence.Kyb.Registry;
using MerchantIntelligence.Kyb.Sanctions;
using MerchantIntelligence.MccValidation.Web;
using MerchantIntelligence.Platform.Workflows;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace MerchantIntelligence.Tests;

/// <summary>Planner validation, versioned storage and the effect of a published workflow on a real assessment run.</summary>
public sealed class WorkflowTests : IClassFixture<WebApplicationFactory<Program>>
{
    private sealed class FakeSiteHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html><head><title>Good Shoes</title></head><body><h1>Handmade shoes</h1><a href='/privacy'>Privacy</a></body></html>", Encoding.UTF8, "text/html")
            });
    }

    private sealed class UnavailableHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("offline");
    }

    private readonly HttpClient _client;
    private readonly IServiceProvider _services;

    public WorkflowTests(WebApplicationFactory<Program> factory)
    {
        var f = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Platform:DatabasePath", ":memory:");
            b.UseSetting("Platform:ModelsDirectory", Path.Combine(Path.GetTempPath(), $"mi-wf-models-{Guid.NewGuid():N}"));
            b.ConfigureServices(s =>
            {
                s.AddHttpClient(WebsiteContentFetcher.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new FakeSiteHandler());
                s.AddHttpClient(KybOptions.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new UnavailableHandler());
                s.AddHttpClient(SanctionsOptions.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new UnavailableHandler());
            });
        });
        _client = f.CreateClient();
        _services = f.Services;
    }

    private WorkflowPlanner Planner => _services.GetRequiredService<WorkflowPlanner>();

    private static WorkflowDefinition Default() => WorkflowRepository.LoadDefault();

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    // ---- planner ----

    [Fact]
    public void Default_workflow_is_valid_and_batches_independent_steps()
    {
        var plan = Planner.Plan(Default());
        Assert.Empty(plan.Warnings);
        Assert.Empty(plan.Disabled);
        // Pre-check and KYB agents run concurrently; inside each, list order is monotonic (prohibited needs website)
        Assert.Equal(["verification", "screening", "website", "match"], plan.Stages[0].Steps);
        Assert.Equal(["prohibited", "mcc"], plan.Stages[1].Steps);
        Assert.Equal(["score"], plan.Stages[^2].Steps);
        Assert.Equal(["case"], plan.Stages[^1].Steps);
        Assert.Equal(["precheck", "kyb", "financial", "decision"], plan.Agents.Select(a => a.Id));
        Assert.Equal([1, 1, 2, 3], plan.Agents.Select(a => a.Stage));
        Assert.Equal(["kyb"], plan.Agents.Single(a => a.Id == "financial").WaitsFor);       // credit needs match
        Assert.Equal(["financial", "kyb", "precheck"], plan.Agents.Single(a => a.Id == "decision").WaitsFor);
        Assert.StartsWith("flowchart LR", plan.Mermaid);
    }

    [Fact]
    public void Unknown_duplicate_and_missing_steps_are_rejected()
    {
        var unknown = Default();
        unknown.Steps.Add(new WorkflowStepConfig { Id = "astrology" });
        Assert.Contains("Unknown step 'astrology'", Assert.Throws<WorkflowValidationException>(() => Planner.Validate(unknown)).Message);

        var dup = Default();
        dup.Steps.Add(new WorkflowStepConfig { Id = "mcc" });
        Assert.Contains("more than once", Assert.Throws<WorkflowValidationException>(() => Planner.Validate(dup)).Message);

        var missing = Default();
        missing.Steps.RemoveAll(s => s.Id == "terms");
        Assert.Contains("missing", Assert.Throws<WorkflowValidationException>(() => Planner.Validate(missing)).Message);
    }

    [Fact]
    public void Dependency_ordered_after_dependent_is_rejected()
    {
        var def = Default();
        var score = def.Steps.Single(s => s.Id == "score");
        def.Steps.Remove(score);
        def.Steps.Insert(0, score); // score before credit, which it depends on
        var ex = Assert.Throws<WorkflowValidationException>(() => Planner.Validate(def));
        Assert.Contains("ordered after it", ex.Message);
    }

    [Fact]
    public void Self_and_unknown_dependencies_and_cycles_are_rejected()
    {
        var self = Default();
        self.Step("mcc")!.DependsOn = ["mcc"];
        Assert.Contains("cannot depend on itself", Assert.Throws<WorkflowValidationException>(() => Planner.Validate(self)).Message);

        var unknown = Default();
        unknown.Step("mcc")!.DependsOn = ["nope"];
        Assert.Contains("unknown step 'nope'", Assert.Throws<WorkflowValidationException>(() => Planner.Validate(unknown)).Message);

        var cycle = Default();
        cycle.Step("verification")!.DependsOn = ["screening"];
        cycle.Step("screening")!.DependsOn = ["verification"];
        Assert.Throws<WorkflowValidationException>(() => Planner.Validate(cycle));
    }

    [Fact]
    public void Disabling_a_dependency_warns_and_downstream_still_runs()
    {
        var def = Default();
        def.Step("bank")!.Enabled = false;
        var plan = Planner.Plan(def);
        Assert.Contains("bank", plan.Disabled);
        Assert.Contains(plan.Warnings, w => w.Contains("'plausibility'") && w.Contains("'bank'"));
        Assert.Contains(plan.Stages, s => s.Steps.Contains("plausibility"));
        Assert.DoesNotContain(plan.Stages, s => s.Steps.Contains("bank"));
    }

    [Fact]
    public void Unknown_parameter_is_rejected_and_known_one_accepted()
    {
        var def = Default();
        def.Step("website")!.Params = new() { ["colour"] = JsonDocument.Parse("\"blue\"").RootElement };
        Assert.Contains("no parameter 'colour'", Assert.Throws<WorkflowValidationException>(() => Planner.Validate(def)).Message);

        var ok = Default();
        var descriptor = Planner.Describe("plausibility");
        if (descriptor.Params.Count > 0)
        {
            ok.Step("plausibility")!.Params = new() { [descriptor.Params[0].Name] = JsonDocument.Parse(descriptor.Params[0].Default).RootElement };
            Planner.Validate(ok);
        }
    }

    // ---- repository / API ----

    [Fact]
    public async Task Active_defaults_to_embedded_definition_and_publish_creates_versions()
    {
        var active = await Json(await _client.GetAsync("/api/workflows/active"));
        Assert.Equal("default", active.GetProperty("version").GetString());
        Assert.Equal(13, active.GetProperty("steps").GetArrayLength());

        var catalog = await Json(await _client.GetAsync("/api/workflows/catalog"));
        Assert.Equal(13, catalog.GetArrayLength());

        var draft = Default();
        draft.Name = "No website scan";
        draft.Step("website")!.Enabled = false;
        var published = await Json(await _client.PostAsJsonAsync("/api/workflows/publish", new { workflow = draft, author = "tester", comment = "drop website" }));
        var v1 = published.GetProperty("version").GetInt32();
        Assert.True(published.GetProperty("active").GetBoolean());

        var history = await Json(await _client.GetAsync("/api/workflows/history"));
        Assert.Contains(history.EnumerateArray(), h => h.GetProperty("version").GetInt32() == v1 && h.GetProperty("active").GetBoolean());

        var stored = await Json(await _client.GetAsync($"/api/workflows/{v1}"));
        Assert.Equal("No website scan", stored.GetProperty("name").GetString());
        Assert.Equal(v1.ToString(), stored.GetProperty("version").GetString());

        var steps = await Json(await _client.GetAsync("/api/assessment/steps"));
        var website = steps.EnumerateArray().Single(s => s.GetProperty("id").GetString() == "website");
        Assert.False(website.GetProperty("enabled").GetBoolean());

        // rollback to the default definition (by republishing the built-in) and confirm history has both
        var back = await Json(await _client.PostAsJsonAsync("/api/workflows/publish", new { workflow = Default(), author = "tester", comment = "restore" }));
        Assert.True(back.GetProperty("version").GetInt32() > v1);
        var rolled = await Json(await _client.PostAsJsonAsync($"/api/workflows/rollback/{v1}", new { actor = "tester", reason = "test" }));
        Assert.True(rolled.GetProperty("version").GetInt32() > back.GetProperty("version").GetInt32());
        var nowActive = await Json(await _client.GetAsync("/api/workflows/active"));
        Assert.Equal("No website scan", nowActive.GetProperty("name").GetString());

        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/workflows/9999")).StatusCode);
    }

    [Fact]
    public async Task Validate_endpoint_reports_errors_without_publishing()
    {
        var bad = Default();
        bad.Steps.Add(new WorkflowStepConfig { Id = "mcc" });
        var res = await Json(await _client.PostAsJsonAsync("/api/workflows/validate", bad));
        Assert.False(res.GetProperty("valid").GetBoolean());
        Assert.Contains("more than once", res.GetProperty("error").GetString());

        var good = await Json(await _client.PostAsJsonAsync("/api/workflows/validate", Default()));
        Assert.True(good.GetProperty("valid").GetBoolean());
        Assert.True(good.GetProperty("plan").GetProperty("stages").GetArrayLength() >= 4);

        var publishBad = await _client.PostAsJsonAsync("/api/workflows/publish", new { workflow = bad, author = "tester" });
        Assert.Equal(HttpStatusCode.BadRequest, publishBad.StatusCode);
    }

    [Fact]
    public async Task Disabled_steps_become_skipped_coverage_gaps_and_order_follows_the_workflow()
    {
        var def = Default();
        def.Name = "Lean";
        def.Step("website")!.Enabled = false;
        def.Step("match")!.Enabled = false;
        await Json(await _client.PostAsJsonAsync("/api/workflows/publish", new { workflow = def, author = "tester" }));

        var root = await Json(await _client.PostAsJsonAsync("/api/assessment/run", new
        {
            business = new { legalName = "Good Shoes Ltd", country = "US", websiteUrl = "https://goodshoes.example" },
            owners = new[] { new { fullName = "Jane Cobbler", role = "Owner", ownershipPercent = 100 } },
            businessDescription = "Handmade leather shoes sold online.",
            merchantCategoryCode = 5661, annualVolume = 600000, averageTicket = 120, highestTicket = 900,
            employeeCount = 6, yearsInBusiness = 4, actor = "tester", createCase = false
        }));

        var steps = root.GetProperty("steps").EnumerateArray().Select(s => (Id: s.GetProperty("id").GetString()!, Status: s.GetProperty("status").GetString()!, Summary: s.GetProperty("summary").GetString()!)).ToList();
        Assert.Equal(13, steps.Count);
        Assert.Equal(def.Steps.Select(s => s.Id), steps.Select(s => s.Id));
        Assert.Equal("Skipped", steps.Single(s => s.Id == "website").Status);
        Assert.Contains("Disabled in workflow 'Lean'", steps.Single(s => s.Id == "website").Summary);
        Assert.Equal("Skipped", steps.Single(s => s.Id == "match").Status);
        Assert.Equal("Succeeded", steps.Single(s => s.Id == "score").Status);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("websiteCompliance").ValueKind);

        var wf = root.GetProperty("workflow");
        Assert.Equal("Lean", wf.GetProperty("name").GetString());
        Assert.DoesNotContain("website", wf.GetProperty("enabledSteps").EnumerateArray().Select(e => e.GetString()));

        // restore the default so other tests in this fixture see the full pipeline
        await Json(await _client.PostAsJsonAsync("/api/workflows/publish", new { workflow = Default(), author = "tester" }));
    }

    [Fact]
    public async Task Disabling_the_score_step_forces_refer()
    {
        var def = Default();
        def.Name = "No score";
        def.Step("score")!.Enabled = false;
        var plan = Planner.Plan(def);
        Assert.Contains(plan.Warnings, w => w.Contains("'score' is disabled"));
        await Json(await _client.PostAsJsonAsync("/api/workflows/publish", new { workflow = def, author = "tester" }));

        var root = await Json(await _client.PostAsJsonAsync("/api/assessment/run", new
        {
            business = new { legalName = "Good Shoes Ltd", country = "US" },
            owners = new[] { new { fullName = "Jane Cobbler", role = "Owner", ownershipPercent = 100 } },
            businessDescription = "Handmade leather shoes.", merchantCategoryCode = 5661, annualVolume = 600000, averageTicket = 120, highestTicket = 900,
            employeeCount = 6, yearsInBusiness = 4, actor = "tester", createCase = false
        }));
        Assert.Equal("Refer", root.GetProperty("decision").GetProperty("outcome").GetString());
        Assert.Contains("disabled", root.GetProperty("decision").GetProperty("summary").GetString());

        await Json(await _client.PostAsJsonAsync("/api/workflows/publish", new { workflow = Default(), author = "tester" }));
    }

    // ---- agents ----

    [Fact]
    public void Agent_catalog_has_four_agents_owning_every_step()
    {
        var agents = Planner.AgentCatalog;
        Assert.Equal(["precheck", "kyb", "financial", "decision"], agents.Select(a => a.Id));
        Assert.Equal(Planner.Catalog.Select(s => s.Id).OrderBy(x => x), agents.SelectMany(a => a.DefaultSteps).OrderBy(x => x));

        var noAgents = Default();
        noAgents.Agents = null;
        Assert.Equal(Default().Agents!.Select(a => a.Id), Planner.AgentsOf(noAgents).Select(a => a.Id));
    }

    [Fact]
    public void Agent_configuration_is_validated()
    {
        var unknown = Default();
        unknown.Agents!.Add(new WorkflowAgentConfig { Id = "oracle" });
        Assert.Contains("Unknown agent 'oracle'", Assert.Throws<WorkflowValidationException>(() => Planner.Validate(unknown)).Message);

        var dup = Default();
        dup.Agents!.Add(new WorkflowAgentConfig { Id = "kyb" });
        Assert.Contains("more than once", Assert.Throws<WorkflowValidationException>(() => Planner.Validate(dup)).Message);

        var twice = Default();
        twice.Agents!.Single(a => a.Id == "precheck").Steps.Add("match");
        Assert.Contains("owned by both", Assert.Throws<WorkflowValidationException>(() => Planner.Validate(twice)).Message);

        var orphan = Default();
        orphan.Agents!.Single(a => a.Id == "kyb").Steps.Remove("match");
        Assert.Contains("'match' is not owned", Assert.Throws<WorkflowValidationException>(() => Planner.Validate(orphan)).Message);

        var missing = Default();
        missing.Agents!.RemoveAll(a => a.Id == "decision");
        Assert.Contains("Agent 'decision' is missing", Assert.Throws<WorkflowValidationException>(() => Planner.Validate(missing)).Message);

        // website (pre-check) waiting on verification (KYB) while match (KYB) waits on mcc (pre-check) is a cycle between agents
        var cycle = Default();
        cycle.Step("website")!.DependsOn = ["verification"];
        cycle.Step("match")!.DependsOn = ["mcc"];
        Assert.Contains("depend on each other", Assert.Throws<WorkflowValidationException>(() => Planner.Validate(cycle)).Message);
    }

    [Fact]
    public void Disabling_an_agent_disables_its_steps_and_warns()
    {
        var def = Default();
        def.Agents!.Single(a => a.Id == "precheck").Enabled = false;
        var plan = Planner.Plan(def);
        Assert.Equal(["website", "prohibited", "mcc"], plan.Disabled);
        Assert.Contains(plan.Warnings, w => w.Contains("Agent 'precheck' is disabled"));
        Assert.Equal(0, plan.Agents.Single(a => a.Id == "precheck").Stage);
        Assert.Equal(["financial", "kyb"], plan.Agents.Single(a => a.Id == "decision").WaitsFor);
    }

    [Fact]
    public async Task Agents_report_advisories_for_missing_evidence_without_stopping_the_run()
    {
        var events = new List<string>();
        var request = new
        {
            business = new { legalName = "Good Shoes Ltd", country = "US" },   // no website, no statements, no owners
            businessDescription = "Shoes.", merchantCategoryCode = 5661, annualVolume = 600000, averageTicket = 120, highestTicket = 900,
            actor = "tester", createCase = false
        };
        using var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/assessment/run/stream") { Content = JsonContent.Create(request) }, HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        JsonElement? result = null;
        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.Length == 0) continue;
            var el = JsonDocument.Parse(line).RootElement.Clone();
            events.Add(el.GetProperty("type").GetString()!);
            if (el.GetProperty("type").GetString() == "result") result = el.GetProperty("result");
        }
        Assert.Contains("agent", events);
        Assert.NotNull(result);

        var agents = result.Value.GetProperty("agents").EnumerateArray().ToList();
        Assert.Equal(["precheck", "kyb", "financial", "decision"], agents.Select(a => a.GetProperty("id").GetString()));
        Assert.All(agents, a => Assert.Equal("Succeeded", a.GetProperty("status").GetString()));

        var precheck = agents[0].GetProperty("findings").EnumerateArray().Select(f => f.GetProperty("code").GetString()).ToList();
        Assert.Contains("NO_WEBSITE", precheck);
        Assert.Contains("NO_BANK_STATEMENT", precheck);
        Assert.Contains("NO_OWNERS", precheck);
        Assert.Contains("THIN_DESCRIPTION", precheck);

        // the run went all the way: score produced, decision is the rules engine's, advisories did not abort anything
        Assert.Equal("Succeeded", result.Value.GetProperty("steps").EnumerateArray().Single(s => s.GetProperty("id").GetString() == "score").GetProperty("status").GetString());
        Assert.Contains(result.Value.GetProperty("decision").GetProperty("outcome").GetString(), new[] { "Approve", "Refer", "Decline" });
        Assert.StartsWith(result.Value.GetProperty("decision").GetProperty("outcome").GetString()!, agents[3].GetProperty("summary").GetString());
    }

    [Fact]
    public async Task Disabled_agent_is_reported_and_its_steps_skip()
    {
        var def = Default();
        def.Name = "No financial agent";
        def.Agents!.Single(a => a.Id == "financial").Enabled = false;
        await Json(await _client.PostAsJsonAsync("/api/workflows/publish", new { workflow = def, author = "tester" }));

        var planned = await Json(await _client.GetAsync("/api/assessment/agents"));
        Assert.False(planned.EnumerateArray().Single(a => a.GetProperty("id").GetString() == "financial").GetProperty("enabled").GetBoolean());

        var root = await Json(await _client.PostAsJsonAsync("/api/assessment/run", new
        {
            business = new { legalName = "Good Shoes Ltd", country = "US" },
            owners = new[] { new { fullName = "Jane Cobbler", role = "Owner", ownershipPercent = 100 } },
            businessDescription = "Handmade leather shoes.", merchantCategoryCode = 5661, annualVolume = 600000, averageTicket = 120, highestTicket = 900,
            employeeCount = 6, yearsInBusiness = 4, actor = "tester", createCase = false
        }));
        var credit = root.GetProperty("steps").EnumerateArray().Single(s => s.GetProperty("id").GetString() == "credit");
        Assert.Equal("Skipped", credit.GetProperty("status").GetString());
        Assert.Contains("Agent 'financial' disabled", credit.GetProperty("summary").GetString());
        var financial = root.GetProperty("agents").EnumerateArray().Single(a => a.GetProperty("id").GetString() == "financial");
        Assert.Equal("Skipped", financial.GetProperty("status").GetString());

        await Json(await _client.PostAsJsonAsync("/api/workflows/publish", new { workflow = Default(), author = "tester" }));
    }
}
