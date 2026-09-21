using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MerchantIntelligence.Kyb.Registry;
using MerchantIntelligence.Kyb.Sanctions;
using MerchantIntelligence.MccValidation.Web;
using MerchantIntelligence.Platform.Rules;
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
        // Profile runs alone first; Pre-check and KYB agents then run concurrently; inside each (Parallel by default) only dependencies sequence steps
        Assert.Equal(["entity"], plan.Stages[0].Steps);
        Assert.Equal(["segment"], plan.Stages[1].Steps);
        Assert.Equal(["verification", "screening", "website", "mcc", "match", "owners"], plan.Stages[2].Steps);
        Assert.Equal(["prohibited", "presence"], plan.Stages[3].Steps);   // prohibited needs website, presence waits for verification
        Assert.Equal(["score"], plan.Stages[^2].Steps);
        Assert.Equal(["case"], plan.Stages[^1].Steps);
        Assert.Equal(["profile", "precheck", "kyb", "financial", "decision"], plan.Agents.Select(a => a.Id));
        Assert.Equal([1, 2, 2, 3, 4], plan.Agents.Select(a => a.Stage));
        Assert.Equal(["profile"], plan.Agents.Single(a => a.Id == "precheck").WaitsFor);
        Assert.Equal(["kyb", "profile"], plan.Agents.Single(a => a.Id == "financial").WaitsFor);       // credit needs match
        Assert.Equal(["financial", "kyb", "precheck", "profile"], plan.Agents.Single(a => a.Id == "decision").WaitsFor);
        Assert.StartsWith("flowchart LR", plan.Mermaid);
    }

    [Fact]
    public void Stored_workflow_predating_a_step_is_upgraded_with_the_step_in_its_default_agent()
    {
        var old = Default();
        old.Steps.RemoveAll(s => s.Id == "presence");
        old.Agents!.Single(a => a.Id == "kyb").Steps.Remove("presence");
        Assert.Contains("missing", Assert.Throws<WorkflowValidationException>(() => Planner.Validate(old)).Message);

        var upgraded = Planner.Upgrade(old);
        Assert.NotSame(old, upgraded);
        Assert.Equal(17, upgraded.Steps.Count);
        Assert.True(upgraded.Steps.FindIndex(s => s.Id == "presence") > upgraded.Steps.FindIndex(s => s.Id == "verification"));
        Assert.Contains("presence", upgraded.Agents!.Single(a => a.Id == "kyb").Steps);
        Assert.Empty(Planner.Plan(upgraded).Warnings);
        Assert.Same(upgraded, Planner.Upgrade(upgraded));
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
    public void List_position_is_not_significant_dependencies_still_schedule()
    {
        var def = Default();
        var score = def.Steps.Single(s => s.Id == "score");
        def.Steps.Remove(score);
        def.Steps.Insert(0, score); // score listed before credit, which it depends on
        var plan = Planner.Plan(def);
        var stageOf = plan.Stages.SelectMany((s, i) => s.Steps.Select(id => (id, i))).ToDictionary(x => x.id, x => x.i);
        Assert.True(stageOf["credit"] < stageOf["score"]);
    }

    // ---- transitions, step order, slots ----

    [Fact]
    public void Ordered_agent_follows_lane_order_and_slots_but_dependencies_win()
    {
        var def = Default();
        var financial = def.Agents!.Single(a => a.Id == "financial");
        financial.StepOrder = AgentStepOrder.Ordered;
        financial.Steps = ["plausibility", "bank", "financials", "credit"]; // plausibility needs bank + financials
        def.Step("bank")!.Slot = 1;
        def.Step("financials")!.Slot = 1;         // same slot → run together

        var plan = Planner.Plan(def);
        var stages = plan.Agents.Single(a => a.Id == "financial").StepStages;
        Assert.Equal(["bank", "financials"], stages[0]);
        Assert.Equal(["plausibility", "credit"], stages[1]); // plausibility pushed after its inputs, next to credit (slot 2)
        Assert.Equal(2, stages.Count);
        Assert.Contains(plan.Warnings, w => w.Contains("'plausibility' is slotted before") && w.Contains("'bank'"));

        var parallel = Default();
        var pre = parallel.Agents!.Single(a => a.Id == "precheck");
        pre.StepOrder = AgentStepOrder.Ordered;   // website, prohibited, mcc → three sequential slots
        Assert.Equal([["website"], ["prohibited"], ["mcc"]], Planner.Plan(parallel).Agents.Single(a => a.Id == "precheck").StepStages);
    }

    [Fact]
    public void Transitions_order_agents_and_are_validated()
    {
        var def = Default();
        def.Transitions = [new() { From = "precheck", To = "kyb", When = TransitionCondition.Success }, new() { From = "kyb", To = "financial" }, new() { From = "financial", To = "decision" }];
        var plan = Planner.Plan(def);
        Assert.Equal([1, 2, 3, 4, 5], plan.Agents.Select(a => a.Stage));
        Assert.Equal(["precheck", "profile"], plan.Agents.Single(a => a.Id == "kyb").WaitsFor);
        Assert.Single(plan.Agents.Single(a => a.Id == "kyb").RunsWhen);
        Assert.Contains(plan.Warnings, w => w.Contains("'kyb' runs only when 'precheck' on success"));
        Assert.Contains("on success", plan.Mermaid);

        var unknown = Default();
        unknown.Transitions = [new() { From = "precheck", To = "nope" }];
        Assert.Throws<WorkflowValidationException>(() => Planner.Validate(unknown));

        var self = Default();
        self.Transitions = [new() { From = "kyb", To = "kyb" }];
        Assert.Throws<WorkflowValidationException>(() => Planner.Validate(self));

        var cycle = Default();
        cycle.Transitions = [new() { From = "precheck", To = "kyb" }, new() { From = "kyb", To = "precheck" }];
        Assert.Contains("depend on each other", Assert.Throws<WorkflowValidationException>(() => Planner.Validate(cycle)).Message);

        var againstDeps = Default();
        againstDeps.Transitions = [new() { From = "decision", To = "kyb" }]; // score depends on kyb's match
        Assert.Throws<WorkflowValidationException>(() => Planner.Validate(againstDeps));
    }

    [Fact]
    public void Stop_gates_are_validated_and_new_fields_round_trip_through_json()
    {
        var def = Default();
        def.Step("screening")!.StopGate = new StopGateConfig { When = StopGateTrigger.Flag };
        Assert.Contains("names no flag code", Assert.Throws<WorkflowValidationException>(() => Planner.Validate(def)).Message);

        var onScore = Default();
        onScore.Step("score")!.StopGate = new StopGateConfig();
        Assert.Contains("cannot carry a stop-gate", Assert.Throws<WorkflowValidationException>(() => Planner.Validate(onScore)).Message);

        var ok = Default();
        ok.Step("screening")!.StopGate = new StopGateConfig { When = StopGateTrigger.HardStop, Scope = StopGateScope.Workflow, ForceOutcome = ForcedOutcome.Decline };
        ok.Step("terms")!.StopGate = new StopGateConfig { When = StopGateTrigger.HighSeverityFlag };
        ok.Agents!.Single(a => a.Id == "kyb").StepOrder = AgentStepOrder.Ordered;
        ok.Step("match")!.Slot = 3;
        var plan = Planner.Plan(ok);
        Assert.Contains(plan.Warnings, w => w.Contains("Stop-gate on 'screening'") && w.Contains("the whole workflow") && w.Contains("Decline"));
        Assert.Contains(plan.Warnings, w => w.Contains("Stop-gate on 'terms' watches flags"));

        var json = JsonSerializer.Serialize(ok, RulesEngine.JsonOptions);
        var back = JsonSerializer.Deserialize<WorkflowDefinition>(json, RulesEngine.JsonOptions)!;
        Assert.Equal(AgentStepOrder.Ordered, back.Agents!.Single(a => a.Id == "kyb").StepOrder);
        Assert.Equal(3, back.Step("match")!.Slot);
        Assert.Equal(ForcedOutcome.Decline, back.Step("screening")!.StopGate!.ForceOutcome);
        Assert.Equal(4, back.Transitions!.Count);

        // stored definitions from before these fields existed still load and get the default flow
        var legacy = JsonSerializer.Deserialize<WorkflowDefinition>("""{"name":"Old","version":"1","steps":[{"id":"score"}]}""", RulesEngine.JsonOptions)!;
        var upgraded = Planner.Upgrade(legacy);
        Assert.Equal(17, upgraded.Steps.Count);
        Assert.Equal("profile", upgraded.Agents![0].Id);
        Assert.All(upgraded.Agents!, a => Assert.Equal(AgentStepOrder.Parallel, a.StepOrder));
        Assert.Empty(Planner.Plan(upgraded).Agents.SelectMany(a => a.RunsWhen)); // no transitions → dependency-driven order, as before
        Assert.Equal([1, 2, 2, 3, 4], Planner.Plan(upgraded).Agents.Select(a => a.Stage));
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
        Assert.Equal(17, active.GetProperty("steps").GetArrayLength());

        var catalog = await Json(await _client.GetAsync("/api/workflows/catalog"));
        Assert.Equal(17, catalog.GetArrayLength());

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
        Assert.Equal(17, steps.Count);
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
    public void Agent_catalog_has_five_agents_owning_every_step()
    {
        var agents = Planner.AgentCatalog;
        Assert.Equal(["profile", "precheck", "kyb", "financial", "decision"], agents.Select(a => a.Id));
        Assert.Equal("profile", Planner.ProfileAgentId);
        Assert.Equal(["entity", "segment"], Planner.ProfileSteps.OrderBy(x => x));
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
        Assert.Equal(["financial", "kyb", "profile"], plan.Agents.Single(a => a.Id == "decision").WaitsFor);
    }

    [Fact]
    public void Profile_agent_must_run_first_and_cannot_be_disabled_gated_or_fed()
    {
        var def = Default();
        def.Agents!.Single(a => a.Id == "profile").Enabled = false;
        Assert.Contains("cannot be disabled", Assert.Throws<WorkflowValidationException>(() => Planner.Validate(def)).Message);

        def = Default();
        def.Step("segment")!.Enabled = false;
        Assert.Contains("cannot be disabled", Assert.Throws<WorkflowValidationException>(() => Planner.Validate(def)).Message);

        def = Default();
        def.Transitions!.Add(new() { From = "precheck", To = "profile" });
        Assert.Contains("always runs first", Assert.Throws<WorkflowValidationException>(() => Planner.Validate(def)).Message);

        def = Default();
        def.Step("entity")!.DependsOn = ["website"];
        Assert.Contains("cannot depend on evidence step", Assert.Throws<WorkflowValidationException>(() => Planner.Validate(def)).Message);

        def = Default();
        def.Step("segment")!.StopGate = new StopGateConfig { When = StopGateTrigger.Failed };
        Assert.Contains("cannot carry a stop-gate", Assert.Throws<WorkflowValidationException>(() => Planner.Validate(def)).Message);

        def = Default();
        def.Agents!.Single(a => a.Id == "profile").Steps.Add("website");
        def.Agents!.Single(a => a.Id == "precheck").Steps.Remove("website");
        Assert.Contains("cannot be owned by the profiling agent", Assert.Throws<WorkflowValidationException>(() => Planner.Validate(def)).Message);

        // even with every transition removed and a step that declares no dependencies, nothing is scheduled before the profile
        def = Default();
        def.Transitions = [];
        var plan = Planner.Plan(def);
        Assert.Equal(1, plan.Agents.Single(a => a.Id == "profile").Stage);
        Assert.All(plan.Agents.Where(a => a.Id != "profile"), a => Assert.Contains("profile", a.WaitsFor));
        Assert.Equal(["entity"], plan.Stages[0].Steps);
        Assert.DoesNotContain("segment --> website", plan.Mermaid); // implicit dependency is not drawn as a step edge
        Assert.Contains("profile --> precheck", plan.Mermaid);
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
        Assert.Equal(["profile", "precheck", "kyb", "financial", "decision"], agents.Select(a => a.GetProperty("id").GetString()));
        Assert.All(agents, a => Assert.Equal("Succeeded", a.GetProperty("status").GetString()));

        var profile = agents[0].GetProperty("findings").EnumerateArray().Select(f => f.GetProperty("code").GetString()).ToList();
        Assert.Contains("SMB_NO_BANK_STATEMENT", profile);
        Assert.Contains("NOT_APPLICABLE_WEBSITE", profile);
        Assert.Equal("Skipped", result.Value.GetProperty("steps").EnumerateArray().Single(s => s.GetProperty("id").GetString() == "website").GetProperty("status").GetString());

        var precheck = agents[1].GetProperty("findings").EnumerateArray().Select(f => f.GetProperty("code").GetString()).ToList();
        Assert.Contains("NO_WEBSITE", precheck);
        Assert.Contains("NO_BANK_STATEMENT", precheck);
        Assert.Contains("NO_OWNERS", precheck);
        Assert.Contains("THIN_DESCRIPTION", precheck);

        // the run went all the way: score produced, decision is the rules engine's, advisories did not abort anything
        Assert.Equal("Succeeded", result.Value.GetProperty("steps").EnumerateArray().Single(s => s.GetProperty("id").GetString() == "score").GetProperty("status").GetString());
        Assert.Contains(result.Value.GetProperty("decision").GetProperty("outcome").GetString(), new[] { "Approve", "Refer", "Decline" });
        Assert.StartsWith(result.Value.GetProperty("decision").GetProperty("outcome").GetString()!, agents[4].GetProperty("summary").GetString());
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

    [Fact]
    public async Task Agent_whose_transition_does_not_hold_is_skipped_with_its_steps()
    {
        var def = Default();
        def.Name = "Financial only on KYB failure";
        def.Transitions!.Single(t => t is { From: "kyb", To: "financial" }).When = TransitionCondition.Fail;
        await Json(await _client.PostAsJsonAsync("/api/workflows/publish", new { workflow = def, author = "tester" }));

        var root = await Json(await _client.PostAsJsonAsync("/api/assessment/run", new
        {
            business = new { legalName = "Good Shoes Ltd", country = "US" },
            owners = new[] { new { fullName = "Jane Cobbler", role = "Owner", ownershipPercent = 100 } },
            businessDescription = "Handmade leather shoes.", merchantCategoryCode = 5661, annualVolume = 600000, averageTicket = 120, highestTicket = 900,
            employeeCount = 6, yearsInBusiness = 4, actor = "tester", createCase = false
        }));
        var agents = root.GetProperty("agents").EnumerateArray().ToDictionary(a => a.GetProperty("id").GetString()!);
        Assert.Equal("Succeeded", agents["kyb"].GetProperty("status").GetString());
        Assert.Equal("Skipped", agents["financial"].GetProperty("status").GetString());
        Assert.Contains("'kyb' on fail", agents["financial"].GetProperty("summary").GetString());
        var credit = root.GetProperty("steps").EnumerateArray().Single(s => s.GetProperty("id").GetString() == "credit");
        Assert.Equal("Skipped", credit.GetProperty("status").GetString());
        Assert.Contains("agent 'financial' did not run", credit.GetProperty("summary").GetString());
        Assert.NotEqual("Skipped", agents["decision"].GetProperty("status").GetString()); // still reached via precheck/kyb → decision (always)

        await Json(await _client.PostAsJsonAsync("/api/workflows/publish", new { workflow = Default(), author = "tester" }));
    }

    [Fact]
    public async Task Stop_gate_skips_remaining_steps_and_forces_the_outcome()
    {
        var def = Default();
        def.Name = "Decline prohibited early";
        def.Agents!.Single(a => a.Id == "precheck").StepOrder = AgentStepOrder.Ordered; // website → prohibited → mcc
        def.Step("prohibited")!.StopGate = new StopGateConfig { When = StopGateTrigger.HardStop, Scope = StopGateScope.Workflow, ForceOutcome = ForcedOutcome.Decline };
        await Json(await _client.PostAsJsonAsync("/api/workflows/publish", new { workflow = def, author = "tester" }));

        var root = await Json(await _client.PostAsJsonAsync("/api/assessment/run", new
        {
            business = new { legalName = "QuickCash Advance LLC", country = "US" },
            owners = new[] { new { fullName = "Sam Lender", role = "Owner", ownershipPercent = 100 } },
            businessDescription = "Payday loans, cash advance and short-term high-interest lending with same-day payday advance.", merchantCategoryCode = 6012,
            annualVolume = 600000, averageTicket = 80, highestTicket = 400, employeeCount = 6, yearsInBusiness = 2, actor = "tester", createCase = false
        }));
        var steps = root.GetProperty("steps").EnumerateArray().ToDictionary(s => s.GetProperty("id").GetString()!);
        Assert.Equal("Succeeded", steps["prohibited"].GetProperty("status").GetString());
        Assert.Equal("Skipped", steps["mcc"].GetProperty("status").GetString());
        Assert.Contains("stop-gate on 'prohibited'", steps["mcc"].GetProperty("summary").GetString());
        Assert.Equal("Skipped", steps["bank"].GetProperty("status").GetString());        // workflow scope reaches other agents
        Assert.Equal("Succeeded", steps["score"].GetProperty("status").GetString());     // required steps still run
        Assert.Equal("Decline", root.GetProperty("decision").GetProperty("outcome").GetString());
        var gates = root.GetProperty("workflow").GetProperty("stopGates").EnumerateArray().ToList();
        Assert.Single(gates);
        Assert.Equal("prohibited", gates[0].GetProperty("stepId").GetString());
        Assert.Contains("PROHIBITED_BUSINESS", gates[0].GetProperty("reason").GetString());
        var precheck = root.GetProperty("agents").EnumerateArray().Single(a => a.GetProperty("id").GetString() == "precheck");
        Assert.Contains(precheck.GetProperty("findings").EnumerateArray(), f => f.GetProperty("code").GetString() == "STOP_GATE");

        await Json(await _client.PostAsJsonAsync("/api/workflows/publish", new { workflow = Default(), author = "tester" }));
    }
}
