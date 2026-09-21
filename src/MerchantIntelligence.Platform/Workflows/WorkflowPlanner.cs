using System.Text;

namespace MerchantIntelligence.Platform.Workflows;

/// <summary>
/// Validates a <see cref="WorkflowDefinition"/> against the step and agent catalogues and resolves it into an
/// execution plan. Agents are ordered by the transitions between them plus the dependencies between the steps they
/// own (agents with neither run concurrently). Inside an agent, a <see cref="AgentStepOrder.Parallel"/> agent lets
/// data dependencies alone decide the stages; an <see cref="AgentStepOrder.Ordered"/> agent runs its steps in list
/// (or slot) order, one slot at a time. Dependencies always win over slots: a step is never scheduled before an input.
/// </summary>
public sealed class WorkflowPlanner
{
    private readonly Dictionary<string, WorkflowStepDescriptor> _catalog;
    private readonly Dictionary<string, WorkflowAgentDescriptor> _agents;
    private readonly List<string> _agentOrder;
    private readonly HashSet<string> _profileSteps;

    public WorkflowPlanner(IEnumerable<IAssessmentStep> steps, IEnumerable<IAssessmentAgent> agents)
    {
        _catalog = steps.ToDictionary(s => s.Descriptor.Id, s => s.Descriptor);
        var list = agents.Select(a => a.Descriptor).ToList();
        // the profiling agent always leads the catalogue, whatever order it was registered in
        list = list.Where(a => a.Kind == AgentKind.Profiling).Concat(list.Where(a => a.Kind != AgentKind.Profiling)).ToList();
        _agents = list.ToDictionary(a => a.Id);
        _agentOrder = list.Select(a => a.Id).ToList();
        _profileSteps = _catalog.Values.Where(s => s.Profiling).Select(s => s.Id).ToHashSet();

        var profilers = list.Where(a => a.Kind == AgentKind.Profiling).ToList();
        if (profilers.Count > 1)
            throw new InvalidOperationException($"Exactly one profiling agent is allowed; found {string.Join(", ", profilers.Select(p => p.Id))}.");
        ProfileAgentId = profilers.SingleOrDefault()?.Id;
    }

    public IReadOnlyList<WorkflowStepDescriptor> Catalog => _catalog.Values.ToList();

    public IReadOnlyList<WorkflowAgentDescriptor> AgentCatalog => _agentOrder.Select(id => _agents[id]).ToList();

    /// <summary>The agent that profiles the applicant and must run before every other agent; null when none is registered.</summary>
    public string? ProfileAgentId { get; }

    /// <summary>Steps that build the merchant profile; every other step implicitly depends on them.</summary>
    public IReadOnlySet<string> ProfileSteps => _profileSteps;

    public bool IsProfileStep(string id) => _profileSteps.Contains(id);

    public WorkflowStepDescriptor Describe(string id) => _catalog[id];

    /// <summary>
    /// Effective dependencies of a configured step (override or catalogue default), enabled or not. Every non-profile step
    /// additionally depends on the profile steps, so nothing can be scheduled before the applicant has been profiled.
    /// </summary>
    public IReadOnlyList<string> DependenciesOf(WorkflowStepConfig step)
    {
        var declared = DeclaredDependenciesOf(step);
        if (_profileSteps.Count == 0 || _profileSteps.Contains(step.Id)) return declared;
        return declared.Concat(_profileSteps.Where(p => !declared.Contains(p))).ToList();
    }

    /// <summary>Dependencies as written by the author or the catalogue, without the implicit profile dependency.</summary>
    public IReadOnlyList<string> DeclaredDependenciesOf(WorkflowStepConfig step) => step.DependsOn ?? _catalog[step.Id].DependsOn;

    /// <summary>The agent grouping in force for a definition: the configured one, or the catalogue defaults.</summary>
    public IReadOnlyList<WorkflowAgentConfig> AgentsOf(WorkflowDefinition def) =>
        def.Agents ?? _agentOrder.Select(id => new WorkflowAgentConfig { Id = id, Enabled = true, Steps = _agents[id].DefaultSteps.ToList() }).ToList();

    /// <summary>The agent-to-agent transitions in force (none when the definition predates them).</summary>
    public static IReadOnlyList<WorkflowTransition> TransitionsOf(WorkflowDefinition def) => def.Transitions ?? [];

    /// <summary>
    /// Adds catalogue steps (and agents) that a stored definition predates: each new step is inserted right after the last
    /// step it depends on, enabled with the default failure policy, and assigned to the agent that owns it by default.
    /// Returns the same instance when nothing is missing.
    /// </summary>
    public WorkflowDefinition Upgrade(WorkflowDefinition def)
    {
        var known = def.Steps.Select(s => s.Id).ToHashSet();
        var newSteps = _catalog.Keys.Where(k => !known.Contains(k)).ToList();
        var newAgents = _agentOrder.Where(a => def.Agents is null || def.Agents.All(x => x.Id != a)).ToList();
        if (newSteps.Count == 0 && newAgents.Count == 0) return def;

        var steps = def.Steps.Select(s => new WorkflowStepConfig
        {
            Id = s.Id, Enabled = s.Enabled, OnFail = s.OnFail, DependsOn = s.DependsOn?.ToList(), Params = s.Params is null ? null : new(s.Params),
            Slot = s.Slot, StopGate = s.StopGate is null ? null : new StopGateConfig { When = s.StopGate.When, Code = s.StopGate.Code, Scope = s.StopGate.Scope, ForceOutcome = s.StopGate.ForceOutcome }
        }).ToList();
        // a definition without an agents block gets the default ownership made explicit; agents missing from an existing block start empty
        var agents = def.Agents?.Select(a => new WorkflowAgentConfig { Id = a.Id, Enabled = a.Enabled, Steps = a.Steps.ToList(), StepOrder = a.StepOrder }).ToList() ?? [];
        foreach (var id in newAgents)
        {
            var cfg = new WorkflowAgentConfig { Id = id, Enabled = true, Steps = def.Agents is null ? _agents[id].DefaultSteps.Where(known.Contains).ToList() : [] };
            if (_agents[id].Kind == AgentKind.Profiling) agents.Insert(0, cfg); else agents.Add(cfg);
        }

        foreach (var id in newSteps.OrderBy(id => _profileSteps.Contains(id) ? 0 : 1))
        {
            var deps = _catalog[id].DependsOn;
            var after = steps.Select((s, i) => (s.Id, i)).Where(x => deps.Contains(x.Id)).Select(x => x.i).DefaultIfEmpty(-1).Max();
            steps.Insert(after + 1, new WorkflowStepConfig { Id = id });

            var owner = _agentOrder.FirstOrDefault(a => _agents[a].DefaultSteps.Contains(id)) ?? _agentOrder[0];
            var cfg = agents?.FirstOrDefault(a => a.Id == owner);
            if (cfg is not null && !cfg.Steps.Contains(id))
            {
                var pos = cfg.Steps.Select((s, i) => (s, i)).Where(x => deps.Contains(x.s)).Select(x => x.i).DefaultIfEmpty(-1).Max();
                cfg.Steps.Insert(pos + 1, id);
            }
        }

        return new WorkflowDefinition
        {
            Name = def.Name, Version = def.Version, Description = def.Description, HaltOnHardStop = def.HaltOnHardStop, Steps = steps, Agents = agents,
            Transitions = def.Transitions?.Select(t => new WorkflowTransition { From = t.From, To = t.To, When = t.When }).ToList()
        };
    }

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
        var transitions = TransitionsOf(def);
        var enabledAgents = agents.Where(a => a.Enabled).Select(a => a.Id).ToHashSet();
        var active = def.Steps.Where(s => s.Enabled && enabledAgents.Contains(owner[s.Id])).ToList();
        var activeIds = active.Select(s => s.Id).ToHashSet();
        var disabled = def.Steps.Where(s => !activeIds.Contains(s.Id)).Select(s => s.Id).ToList();

        foreach (var step in active)
        {
            var missing = DeclaredDependenciesOf(step).Where(d => !activeIds.Contains(d)).ToList();
            if (missing.Count > 0)
                warnings.Add($"'{step.Id}' will run without input from {string.Join(", ", missing.Select(m => $"'{m}'"))} (disabled) – results are degraded.");
        }
        foreach (var required in _catalog.Values.Where(c => c.Required && !activeIds.Contains(c.Id)))
            warnings.Add($"'{required.Id}' is disabled; every assessment will end in Refer with no score.");
        foreach (var agent in agents.Where(a => !a.Enabled))
            warnings.Add($"Agent '{agent.Id}' is disabled: {string.Join(", ", agent.Steps.Select(s => $"'{s}'"))} will not run.");
        if (def.HaltOnHardStop)
            warnings.Add("Halt on hard stop is on: once sanctions, MATCH or a prohibited category is confirmed, remaining evidence steps are skipped.");
        foreach (var step in active.Where(s => s.StopGate is not null))
        {
            var g = step.StopGate!;
            if (g.When is StopGateTrigger.Flag or StopGateTrigger.HighSeverityFlag && !StopGates.RaisesFlags(step.Id))
                warnings.Add($"Stop-gate on '{step.Id}' watches flags, but that step never raises any – the gate can only fire on its other triggers.");
            var effect = g.ForceOutcome == ForcedOutcome.None ? "" : $", outcome forced to {g.ForceOutcome}";
            warnings.Add($"Stop-gate on '{step.Id}': when {StopGates.Describe(g)}, remaining evidence steps of {(g.Scope == StopGateScope.Agent ? $"agent '{owner[step.Id]}'" : "the whole workflow")} are skipped{effect}.");
        }

        // ---- agent graph: A waits for B when a transition B → A exists or an active step of A depends on an active step owned by B ----
        var waits = agents.ToDictionary(a => a.Id, _ => new SortedSet<string>());
        foreach (var step in active)
            foreach (var dep in DependenciesOf(step).Where(activeIds.Contains))
                if (owner[dep] != owner[step.Id]) waits[owner[step.Id]].Add(owner[dep]);
        foreach (var t in transitions.Where(t => enabledAgents.Contains(t.From) && enabledAgents.Contains(t.To)))
            waits[t.To].Add(t.From);

        foreach (var a in agents.Where(a => a.Enabled))
        {
            var incoming = transitions.Where(t => t.To == a.Id).ToList();
            if (incoming.Count == 0) continue;
            var live = incoming.Where(t => enabledAgents.Contains(t.From)).ToList();
            if (live.Count == 0)
                warnings.Add($"Agent '{a.Id}' only runs after {string.Join(", ", incoming.Select(t => $"'{t.From}'"))}, which is disabled – '{a.Id}' will never run.");
            else if (live.All(t => t.When != TransitionCondition.Always))
            {
                var required = a.Steps.Where(activeIds.Contains).Where(s => _catalog[s].Required).ToList();
                var tail = required.Count > 0 ? $" It owns {string.Join(", ", required.Select(r => $"'{r}'"))}, so those runs end in Refer with no score." : "";
                warnings.Add($"Agent '{a.Id}' runs only when {string.Join(" or ", live.Select(t => $"'{t.From}' {StopGates.Describe(t.When)}"))}; otherwise it is skipped.{tail}");
            }
        }

        var agentStage = new Dictionary<string, int>();
        foreach (var a in agents.Where(a => a.Enabled)) StageOf(a.Id, waits, agentStage, []);

        // ---- step stages: intra-agent stages, offset by the agent stage ----
        var intra = agents.Where(a => a.Enabled).ToDictionary(a => a.Id, a => IntraStages(def, a, activeIds, warnings));
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
            waits[a.Id].ToList(),
            transitions.Where(t => t.To == a.Id).ToList(),
            a.Enabled ? intra[a.Id] : [])).ToList();

        return new WorkflowPlan(planned, warnings, disabled, Mermaid(def, agentPlans, activeIds), agentPlans);
    }

    /// <summary>
    /// Stages for the active steps of one agent. Parallel: dependencies only. Ordered: list/slot order, then dependencies push
    /// steps later (each such correction is reported as a warning so the editor can show why a slot was not honoured).
    /// </summary>
    public List<List<string>> IntraStages(WorkflowDefinition def, WorkflowAgentConfig agent, IReadOnlySet<string> activeIds, List<string>? warnings = null)
    {
        var set = agent.Steps.Where(activeIds.Contains).ToHashSet();
        var stageOf = new Dictionary<string, int>();

        var topological = InDependencyOrder(def, set);

        if (agent.StepOrder == AgentStepOrder.Parallel)
        {
            foreach (var step in topological)
                stageOf[step.Id] = DependenciesOf(step).Where(set.Contains).Select(d => stageOf[d] + 1).DefaultIfEmpty(0).Max();
        }
        else
        {
            // slot order: explicit slots are kept, unset ones follow the previous step
            var slotOf = new Dictionary<string, int>();
            var previous = -1;
            foreach (var id in agent.Steps.Where(set.Contains))
            {
                var slot = def.Step(id)!.Slot ?? previous + 1;
                slotOf[id] = slot;
                previous = slot;
            }
            var ranks = slotOf.Values.Distinct().OrderBy(x => x).Select((s, i) => (s, i)).ToDictionary(x => x.s, x => x.i);
            // dependencies can only push a step later; resolve in dependency order
            foreach (var step in topological)
            {
                var wanted = ranks[slotOf[step.Id]];
                var deps = DependenciesOf(step).Where(set.Contains).ToList();
                var forced = deps.Select(d => stageOf[d] + 1).DefaultIfEmpty(0).Max();
                stageOf[step.Id] = Math.Max(wanted, forced);
                if (forced > wanted)
                    warnings?.Add($"'{step.Id}' is slotted before {string.Join(", ", deps.Where(d => stageOf[d] >= wanted).Select(d => $"'{d}'"))} in agent '{agent.Id}', but needs their output – it runs after them.");
            }
        }

        var laneOrder = agent.Steps.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        return stageOf.GroupBy(kv => kv.Value).OrderBy(g => g.Key)
            .Select(g => g.Select(kv => kv.Key).OrderBy(id => laneOrder[id]).ToList()).ToList();
    }

    /// <summary>The given steps ordered so that every step follows its (in-set) dependencies; ties keep definition order.</summary>
    private List<WorkflowStepConfig> InDependencyOrder(WorkflowDefinition def, IReadOnlySet<string> set)
    {
        var pending = def.Steps.Where(s => set.Contains(s.Id)).ToList();
        var done = new HashSet<string>();
        var ordered = new List<WorkflowStepConfig>();
        while (pending.Count > 0)
        {
            var ready = pending.Where(s => DependenciesOf(s).Where(set.Contains).All(done.Contains)).ToList();
            if (ready.Count == 0)
                throw new WorkflowValidationException($"Steps {string.Join(", ", pending.Select(s => $"'{s.Id}'"))} depend on each other in a cycle.");
            foreach (var s in ready) { ordered.Add(s); done.Add(s.Id); pending.Remove(s); }
        }
        return ordered;
    }

    private static int StageOf(string agent, Dictionary<string, SortedSet<string>> waits, Dictionary<string, int> memo, HashSet<string> path)
    {
        if (memo.TryGetValue(agent, out var s)) return s;
        if (!path.Add(agent)) throw new WorkflowValidationException($"Agents {string.Join(" → ", path.Append(agent).Select(a => $"'{a}'"))} depend on each other (through transitions or step dependencies). Make the flow run one way only.");
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
            if (step.Slot is < 0) throw new WorkflowValidationException($"Step '{step.Id}' has a negative slot.");
            if (step.StopGate is { When: StopGateTrigger.Flag, Code: null or "" })
                throw new WorkflowValidationException($"Stop-gate on '{step.Id}' triggers on a flag but names no flag code.");
            if (step.StopGate is not null && _catalog[step.Id].Required)
                throw new WorkflowValidationException($"Step '{step.Id}' decides the outcome and cannot carry a stop-gate.");
        }
        foreach (var missing in _catalog.Keys.Where(k => !seen.Contains(k)))
            throw new WorkflowValidationException($"Step '{missing}' is missing from the workflow – list every step and disable the ones you do not want.");

        ValidateAgents(def);
        ValidateTransitions(def);
        ValidateProfile(def);

        // Active dependencies must be acyclic; list position is not significant (slots and agents[].steps order schedule).
        var active = def.Steps.Where(s => IsActive(def, s.Id)).Select(s => s.Id).ToHashSet();
        InDependencyOrder(def, active);

        // Agent graph (transitions + cross-agent dependencies) must be acyclic.
        var owner = OwnersOf(def);
        var agents = AgentsOf(def);
        var enabledAgents = agents.Where(a => a.Enabled).Select(a => a.Id).ToHashSet();
        var waits = agents.ToDictionary(a => a.Id, _ => new SortedSet<string>());
        foreach (var step in def.Steps.Where(s => active.Contains(s.Id)))
            foreach (var dep in DependenciesOf(step).Where(active.Contains))
                if (owner[dep] != owner[step.Id]) waits[owner[step.Id]].Add(owner[dep]);
        foreach (var t in TransitionsOf(def).Where(t => enabledAgents.Contains(t.From) && enabledAgents.Contains(t.To)))
            waits[t.To].Add(t.From);
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

    /// <summary>
    /// The profiling agent scopes the run, so it must be first and alone: it owns exactly the profile steps, is never
    /// disabled, depends on nothing outside itself, receives no transition and carries no stop-gate.
    /// </summary>
    private void ValidateProfile(WorkflowDefinition def)
    {
        if (ProfileAgentId is null) return;
        var agents = AgentsOf(def);
        var profile = agents.Single(a => a.Id == ProfileAgentId);
        if (!profile.Enabled)
            throw new WorkflowValidationException($"Agent '{ProfileAgentId}' profiles the applicant and decides which checks apply; it must run first and cannot be disabled.");
        foreach (var s in profile.Steps.Where(s => !_profileSteps.Contains(s)))
            throw new WorkflowValidationException($"Step '{s}' gathers evidence and cannot be owned by the profiling agent '{ProfileAgentId}'.");
        foreach (var s in _profileSteps.Where(s => !profile.Steps.Contains(s)))
            throw new WorkflowValidationException($"Step '{s}' builds the merchant profile and must be owned by agent '{ProfileAgentId}'.");
        foreach (var t in TransitionsOf(def).Where(t => t.To == ProfileAgentId))
            throw new WorkflowValidationException($"Transition '{t.From}' → '{ProfileAgentId}' is not allowed: the profiling agent always runs first, nothing can run before it.");
        foreach (var step in def.Steps.Where(s => _profileSteps.Contains(s.Id)))
        {
            if (!step.Enabled)
                throw new WorkflowValidationException($"Step '{step.Id}' builds the merchant profile and cannot be disabled.");
            if (step.StopGate is not null)
                throw new WorkflowValidationException($"Step '{step.Id}' scopes the run rather than deciding it and cannot carry a stop-gate.");
            foreach (var dep in DeclaredDependenciesOf(step).Where(d => !_profileSteps.Contains(d)))
                throw new WorkflowValidationException($"Step '{step.Id}' builds the merchant profile and cannot depend on evidence step '{dep}' – the profile is decided before any evidence is gathered.");
        }
    }

    private void ValidateTransitions(WorkflowDefinition def)
    {
        var pairs = new HashSet<(string, string)>();
        foreach (var t in TransitionsOf(def))
        {
            if (!_agents.ContainsKey(t.From)) throw new WorkflowValidationException($"Transition starts at unknown agent '{t.From}'.");
            if (!_agents.ContainsKey(t.To)) throw new WorkflowValidationException($"Transition '{t.From}' → '{t.To}' points at an unknown agent.");
            if (t.From == t.To) throw new WorkflowValidationException($"Agent '{t.From}' cannot transition to itself.");
            if (!pairs.Add((t.From, t.To))) throw new WorkflowValidationException($"Transition '{t.From}' → '{t.To}' is defined more than once – one transition per pair, pick one condition.");
        }
    }

    private string Mermaid(WorkflowDefinition def, IReadOnlyList<WorkflowAgentPlan> agents, HashSet<string> active)
    {
        var sb = new StringBuilder("flowchart LR\n  intake([Intake])\n");
        var transitions = TransitionsOf(def);
        foreach (var agent in agents)
        {
            var title = agent.Enabled ? $"{agent.Name} · stage {agent.Stage}" : $"{agent.Name} (off)";
            sb.Append($"  subgraph {agent.Id}[\"{title}\"]\n");
            var ids = def.Steps.Select(s => s.Id).Where(id => AgentsOf(def).First(a => a.Id == agent.Id).Steps.Contains(id));
            foreach (var id in ids)
                sb.Append(active.Contains(id) ? $"    {id}[\"{_catalog[id].Name}\"]\n" : $"    {id}[\"{_catalog[id].Name} (off)\"]:::off\n");
            sb.Append("  end\n");
            if (agent.Enabled && agent.WaitsFor.Count == 0) sb.Append($"  intake --> {agent.Id}\n");
            foreach (var w in agent.WaitsFor)
            {
                var t = transitions.FirstOrDefault(x => x.From == w && x.To == agent.Id);
                sb.Append(t is null || t.When == TransitionCondition.Always ? $"  {w} --> {agent.Id}\n" : $"  {w} -- {StopGates.Describe(t.When)} --> {agent.Id}\n");
            }
        }
        foreach (var step in def.Steps.Where(s => active.Contains(s.Id)))
            foreach (var dep in DeclaredDependenciesOf(step))
                sb.Append(active.Contains(dep) ? $"  {dep} --> {step.Id}\n" : $"  {dep} -.-> {step.Id}\n");
        sb.Append("  classDef off fill:#eee,stroke:#bbb,color:#888,stroke-dasharray: 4 4\n");
        return sb.ToString();
    }
}

file static class StringExtensions
{
    public static string? NullIfEmpty(this string s) => s.Length == 0 ? null : s;
}
