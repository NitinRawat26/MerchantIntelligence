using System.Text;

namespace MerchantIntelligence.Platform.Workflows;

/// <summary>
/// Validates a <see cref="WorkflowDefinition"/> against the step and agent catalogues and resolves it into an
/// execution plan: agents are ordered by the dependencies between the steps they own (agents with no such
/// dependency run concurrently); inside an agent a step is placed in the first stage after all of its enabled
/// dependencies and never before the step listed above it, so list order is honoured while independent
/// neighbours run concurrently.
/// </summary>
public sealed class WorkflowPlanner
{
    private readonly Dictionary<string, WorkflowStepDescriptor> _catalog;
    private readonly Dictionary<string, WorkflowAgentDescriptor> _agents;
    private readonly List<string> _agentOrder;

    public WorkflowPlanner(IEnumerable<IAssessmentStep> steps, IEnumerable<IAssessmentAgent> agents)
    {
        _catalog = steps.ToDictionary(s => s.Descriptor.Id, s => s.Descriptor);
        var list = agents.Select(a => a.Descriptor).ToList();
        _agents = list.ToDictionary(a => a.Id);
        _agentOrder = list.Select(a => a.Id).ToList();
    }

    public IReadOnlyList<WorkflowStepDescriptor> Catalog => _catalog.Values.ToList();

    public IReadOnlyList<WorkflowAgentDescriptor> AgentCatalog => _agentOrder.Select(id => _agents[id]).ToList();

    public WorkflowStepDescriptor Describe(string id) => _catalog[id];

    /// <summary>Effective dependencies of a configured step (override or catalogue default), enabled or not.</summary>
    public IReadOnlyList<string> DependenciesOf(WorkflowStepConfig step) => step.DependsOn ?? _catalog[step.Id].DependsOn;

    /// <summary>The agent grouping in force for a definition: the configured one, or the catalogue defaults.</summary>
    public IReadOnlyList<WorkflowAgentConfig> AgentsOf(WorkflowDefinition def) =>
        def.Agents ?? _agentOrder.Select(id => new WorkflowAgentConfig { Id = id, Enabled = true, Steps = _agents[id].DefaultSteps.ToList() }).ToList();

    /// <summary>Owner agent of each step.</summary>
    public IReadOnlyDictionary<string, string> OwnersOf(WorkflowDefinition def) =>
        AgentsOf(def).SelectMany(a => a.Steps.Select(s => (s, a.Id))).ToDictionary(x => x.s, x => x.Id);

    /// <summary>A step runs when it is enabled and its agent is enabled.</summary>
    public bool IsActive(WorkflowDefinition def, string stepId)
    {
        var agents = AgentsOf(def);
        var step = def.Step(stepId);
        return step is { Enabled: true } && agents.Any(a => a.Enabled && a.Steps.Contains(stepId));
    }

    public WorkflowPlan Plan(WorkflowDefinition def)
    {
        Validate(def);
        var warnings = new List<string>();
        var agents = AgentsOf(def);
        var owner = OwnersOf(def);
        var enabledAgents = agents.Where(a => a.Enabled).Select(a => a.Id).ToHashSet();
        var active = def.Steps.Where(s => s.Enabled && enabledAgents.Contains(owner[s.Id])).ToList();
        var activeIds = active.Select(s => s.Id).ToHashSet();
        var disabled = def.Steps.Where(s => !activeIds.Contains(s.Id)).Select(s => s.Id).ToList();

        foreach (var step in active)
        {
            var missing = DependenciesOf(step).Where(d => !activeIds.Contains(d)).ToList();
            if (missing.Count > 0)
                warnings.Add($"'{step.Id}' will run without input from {string.Join(", ", missing.Select(m => $"'{m}'"))} (disabled) – results are degraded.");
        }
        foreach (var required in _catalog.Values.Where(c => c.Required && !activeIds.Contains(c.Id)))
            warnings.Add($"'{required.Id}' is disabled; every assessment will end in Refer with no score.");
        foreach (var agent in agents.Where(a => !a.Enabled))
            warnings.Add($"Agent '{agent.Id}' is disabled: {string.Join(", ", agent.Steps.Select(s => $"'{s}'"))} will not run.");
        if (def.HaltOnHardStop)
            warnings.Add("Halt on hard stop is on: once sanctions, MATCH or a prohibited category is confirmed, remaining evidence steps are skipped.");

        // ---- agent graph: A waits for B when an active step of A depends on an active step owned by B ----
        var waits = agents.ToDictionary(a => a.Id, _ => new SortedSet<string>());
        foreach (var step in active)
            foreach (var dep in DependenciesOf(step).Where(activeIds.Contains))
                if (owner[dep] != owner[step.Id]) waits[owner[step.Id]].Add(owner[dep]);

        var agentStage = new Dictionary<string, int>();
        foreach (var a in agents.Where(a => a.Enabled)) StageOf(a.Id, waits, agentStage, []);

        // ---- step stages: intra-agent stages, offset by the agent stage ----
        var intra = agents.Where(a => a.Enabled).ToDictionary(a => a.Id, a => IntraStages(def, a.Steps.Where(activeIds.Contains).ToList()));
        var offsets = new Dictionary<int, int>();
        var offset = 0;
        foreach (var s in agentStage.Values.Distinct().OrderBy(x => x))
        {
            offsets[s] = offset;
            offset += agentStage.Where(kv => kv.Value == s).Max(kv => Math.Max(1, intra[kv.Key].Count));
        }
        var stages = Enumerable.Range(0, offset).Select(_ => new List<string>()).ToList();
        foreach (var step in active)
        {
            var a = owner[step.Id];
            var idx = offsets[agentStage[a]] + intra[a].FindIndex(st => st.Contains(step.Id));
            stages[idx].Add(step.Id);
        }
        var planned = stages.Where(s => s.Count > 0).Select((s, i) => new WorkflowStage(i + 1, s)).ToList();

        var agentPlans = agents.Select(a => new WorkflowAgentPlan(a.Id, _agents[a.Id].Name, a.Enabled,
            a.Enabled ? agentStage[a.Id] + 1 : 0,
            a.Steps.Where(activeIds.Contains).ToList(),
            waits[a.Id].ToList())).ToList();

        return new WorkflowPlan(planned, warnings, disabled, Mermaid(def, agentPlans, activeIds), agentPlans);
    }

    /// <summary>Stages for a subset of steps (list order preserved), considering only dependencies inside the subset.</summary>
    public List<List<string>> IntraStages(WorkflowDefinition def, IReadOnlyList<string> stepIds)
    {
        var set = stepIds.ToHashSet();
        var stageOf = new Dictionary<string, int>();
        var stages = new List<List<string>>();
        var previous = 0;
        foreach (var step in def.Steps.Where(s => set.Contains(s.Id)))
        {
            var depStage = DependenciesOf(step).Where(set.Contains).Select(d => stageOf[d] + 1).DefaultIfEmpty(0).Max();
            var stage = Math.Max(depStage, previous);
            stageOf[step.Id] = stage;
            while (stages.Count <= stage) stages.Add(new List<string>());
            stages[stage].Add(step.Id);
            previous = stage;
        }
        return stages;
    }

    private static int StageOf(string agent, Dictionary<string, SortedSet<string>> waits, Dictionary<string, int> memo, HashSet<string> path)
    {
        if (memo.TryGetValue(agent, out var s)) return s;
        if (!path.Add(agent)) throw new WorkflowValidationException($"Agents {string.Join(" → ", path.Append(agent).Select(a => $"'{a}'"))} depend on each other's steps. Move the steps so the dependency runs one way only.");
        var stage = waits[agent].Select(w => StageOf(w, waits, memo, path) + 1).DefaultIfEmpty(0).Max();
        path.Remove(agent);
        return memo[agent] = stage;
    }

    public void Validate(WorkflowDefinition def)
    {
        if (string.IsNullOrWhiteSpace(def.Name)) throw new WorkflowValidationException("Workflow name is required.");
        if (def.Steps.Count == 0) throw new WorkflowValidationException("Workflow must contain at least one step.");

        var seen = new HashSet<string>();
        foreach (var step in def.Steps)
        {
            if (string.IsNullOrWhiteSpace(step.Id)) throw new WorkflowValidationException("Every step needs an id.");
            if (!_catalog.ContainsKey(step.Id)) throw new WorkflowValidationException($"Unknown step '{step.Id}'. Known steps: {string.Join(", ", _catalog.Keys)}.");
            if (!seen.Add(step.Id)) throw new WorkflowValidationException($"Step '{step.Id}' appears more than once.");
            foreach (var dep in step.DependsOn ?? [])
            {
                if (dep == step.Id) throw new WorkflowValidationException($"Step '{step.Id}' cannot depend on itself.");
                if (!_catalog.ContainsKey(dep)) throw new WorkflowValidationException($"Step '{step.Id}' depends on unknown step '{dep}'.");
            }
            foreach (var p in step.Params?.Keys ?? Enumerable.Empty<string>())
                if (_catalog[step.Id].Params.All(d => d.Name != p))
                    throw new WorkflowValidationException($"Step '{step.Id}' has no parameter '{p}'. Allowed: {string.Join(", ", _catalog[step.Id].Params.Select(d => d.Name)).NullIfEmpty() ?? "none"}.");
        }
        foreach (var missing in _catalog.Keys.Where(k => !seen.Contains(k)))
            throw new WorkflowValidationException($"Step '{missing}' is missing from the workflow – list every step and disable the ones you do not want.");

        ValidateAgents(def);

        // Active dependencies must appear earlier in the list (this also rules out cycles).
        var position = def.Steps.Select((s, i) => (s.Id, i)).ToDictionary(x => x.Id, x => x.i);
        var active = def.Steps.Where(s => IsActive(def, s.Id)).Select(s => s.Id).ToHashSet();
        foreach (var step in def.Steps.Where(s => active.Contains(s.Id)))
            foreach (var dep in DependenciesOf(step).Where(active.Contains))
                if (position[dep] > position[step.Id])
                    throw new WorkflowValidationException($"Step '{step.Id}' depends on '{dep}', which is ordered after it. Move '{dep}' above '{step.Id}' or disable the dependency.");

        // Agent graph must be acyclic.
        var owner = OwnersOf(def);
        var agents = AgentsOf(def);
        var waits = agents.ToDictionary(a => a.Id, _ => new SortedSet<string>());
        foreach (var step in def.Steps.Where(s => active.Contains(s.Id)))
            foreach (var dep in DependenciesOf(step).Where(active.Contains))
                if (owner[dep] != owner[step.Id]) waits[owner[step.Id]].Add(owner[dep]);
        var memo = new Dictionary<string, int>();
        foreach (var a in agents.Where(a => a.Enabled)) StageOf(a.Id, waits, memo, []);
    }

    private void ValidateAgents(WorkflowDefinition def)
    {
        if (def.Agents is null) return;
        var seen = new HashSet<string>();
        var owned = new Dictionary<string, string>();
        foreach (var agent in def.Agents)
        {
            if (string.IsNullOrWhiteSpace(agent.Id)) throw new WorkflowValidationException("Every agent needs an id.");
            if (!_agents.ContainsKey(agent.Id)) throw new WorkflowValidationException($"Unknown agent '{agent.Id}'. Known agents: {string.Join(", ", _agentOrder)}.");
            if (!seen.Add(agent.Id)) throw new WorkflowValidationException($"Agent '{agent.Id}' appears more than once.");
            foreach (var step in agent.Steps)
            {
                if (!_catalog.ContainsKey(step)) throw new WorkflowValidationException($"Agent '{agent.Id}' owns unknown step '{step}'.");
                if (owned.TryGetValue(step, out var other)) throw new WorkflowValidationException($"Step '{step}' is owned by both '{other}' and '{agent.Id}' – a step belongs to exactly one agent.");
                owned[step] = agent.Id;
            }
        }
        foreach (var missing in _agentOrder.Where(a => !seen.Contains(a)))
            throw new WorkflowValidationException($"Agent '{missing}' is missing from the workflow – list every agent and disable the ones you do not want.");
        foreach (var step in _catalog.Keys.Where(k => !owned.ContainsKey(k)))
            throw new WorkflowValidationException($"Step '{step}' is not owned by any agent – assign it to one.");
    }

    private string Mermaid(WorkflowDefinition def, IReadOnlyList<WorkflowAgentPlan> agents, HashSet<string> active)
    {
        var sb = new StringBuilder("flowchart LR\n  intake([Intake])\n");
        foreach (var agent in agents)
        {
            var title = agent.Enabled ? $"{agent.Name} · stage {agent.Stage}" : $"{agent.Name} (off)";
            sb.Append($"  subgraph {agent.Id}[\"{title}\"]\n");
            var ids = def.Steps.Select(s => s.Id).Where(id => AgentsOf(def).First(a => a.Id == agent.Id).Steps.Contains(id));
            foreach (var id in ids)
                sb.Append(active.Contains(id) ? $"    {id}[\"{_catalog[id].Name}\"]\n" : $"    {id}[\"{_catalog[id].Name} (off)\"]:::off\n");
            sb.Append("  end\n");
            if (agent.Enabled && agent.WaitsFor.Count == 0) sb.Append($"  intake --> {agent.Id}\n");
            foreach (var w in agent.WaitsFor) sb.Append($"  {w} --> {agent.Id}\n");
        }
        foreach (var step in def.Steps.Where(s => active.Contains(s.Id)))
            foreach (var dep in DependenciesOf(step))
                sb.Append(active.Contains(dep) ? $"  {dep} --> {step.Id}\n" : $"  {dep} -.-> {step.Id}\n");
        sb.Append("  classDef off fill:#eee,stroke:#bbb,color:#888,stroke-dasharray: 4 4\n");
        return sb.ToString();
    }
}

file static class StringExtensions
{
    public static string? NullIfEmpty(this string s) => s.Length == 0 ? null : s;
}
