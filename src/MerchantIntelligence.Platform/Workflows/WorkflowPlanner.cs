using System.Text;

namespace MerchantIntelligence.Platform.Workflows;

/// <summary>
/// Validates a <see cref="WorkflowDefinition"/> against the step catalogue and resolves it into ordered stages.
/// A step is placed in the first stage after all of its enabled dependencies and never before the step listed
/// above it, so list order is honoured while independent neighbours run concurrently.
/// </summary>
public sealed class WorkflowPlanner
{
    private readonly Dictionary<string, WorkflowStepDescriptor> _catalog;

    public WorkflowPlanner(IEnumerable<IAssessmentStep> steps)
    {
        _catalog = steps.ToDictionary(s => s.Descriptor.Id, s => s.Descriptor);
    }

    public IReadOnlyList<WorkflowStepDescriptor> Catalog => _catalog.Values.ToList();

    public WorkflowStepDescriptor Describe(string id) => _catalog[id];

    /// <summary>Effective dependencies of a configured step (override or catalogue default), enabled or not.</summary>
    public IReadOnlyList<string> DependenciesOf(WorkflowStepConfig step) => step.DependsOn ?? _catalog[step.Id].DependsOn;

    public WorkflowPlan Plan(WorkflowDefinition def)
    {
        Validate(def);
        var warnings = new List<string>();
        var enabled = def.Steps.Where(s => s.Enabled).ToList();
        var enabledIds = enabled.Select(s => s.Id).ToHashSet();
        var disabled = def.Steps.Where(s => !s.Enabled).Select(s => s.Id).ToList();

        foreach (var step in enabled)
        {
            var missing = DependenciesOf(step).Where(d => !enabledIds.Contains(d)).ToList();
            if (missing.Count > 0)
                warnings.Add($"'{step.Id}' will run without input from {string.Join(", ", missing.Select(m => $"'{m}'"))} (disabled) – results are degraded.");
        }
        foreach (var required in _catalog.Values.Where(c => c.Required && !enabledIds.Contains(c.Id)))
            warnings.Add($"'{required.Id}' is disabled; every assessment will end in Refer with no score.");
        if (def.HaltOnHardStop)
            warnings.Add("Halt on hard stop is on: once sanctions, MATCH or a prohibited category is confirmed, remaining evidence steps are skipped.");

        var stageOf = new Dictionary<string, int>();
        var stages = new List<List<string>>();
        var previous = 0;
        foreach (var step in enabled)
        {
            var depStage = DependenciesOf(step).Where(enabledIds.Contains).Select(d => stageOf[d] + 1).DefaultIfEmpty(0).Max();
            var stage = Math.Max(depStage, previous);
            stageOf[step.Id] = stage;
            while (stages.Count <= stage) stages.Add(new List<string>());
            stages[stage].Add(step.Id);
            previous = stage;
        }

        var planned = stages.Select((s, i) => new WorkflowStage(i + 1, s)).ToList();
        return new WorkflowPlan(planned, warnings, disabled, Mermaid(def, planned));
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

        // Enabled dependencies must appear earlier in the list (this also rules out cycles).
        var position = def.Steps.Select((s, i) => (s.Id, i)).ToDictionary(x => x.Id, x => x.i);
        var enabled = def.Steps.Where(s => s.Enabled).Select(s => s.Id).ToHashSet();
        foreach (var step in def.Steps.Where(s => s.Enabled))
            foreach (var dep in DependenciesOf(step).Where(enabled.Contains))
                if (position[dep] > position[step.Id])
                    throw new WorkflowValidationException($"Step '{step.Id}' depends on '{dep}', which is ordered after it. Move '{dep}' above '{step.Id}' or disable the dependency.");
    }

    private string Mermaid(WorkflowDefinition def, IReadOnlyList<WorkflowStage> stages)
    {
        var sb = new StringBuilder("flowchart LR\n  intake([Intake])\n");
        var enabled = def.Steps.Where(s => s.Enabled).Select(s => s.Id).ToHashSet();
        foreach (var stage in stages)
        {
            sb.Append($"  subgraph stage{stage.Index}[Stage {stage.Index}]\n");
            foreach (var id in stage.Steps) sb.Append($"    {id}[\"{_catalog[id].Name}\"]\n");
            sb.Append("  end\n");
        }
        foreach (var id in def.Steps.Where(s => !s.Enabled).Select(s => s.Id))
            sb.Append($"  {id}[\"{_catalog[id].Name} (off)\"]:::off\n");
        foreach (var id in stages.FirstOrDefault()?.Steps ?? []) sb.Append($"  intake --> {id}\n");
        foreach (var step in def.Steps.Where(s => s.Enabled))
            foreach (var dep in DependenciesOf(step))
                sb.Append(enabled.Contains(dep) ? $"  {dep} --> {step.Id}\n" : $"  {dep} -.-> {step.Id}\n");
        sb.Append("  classDef off fill:#eee,stroke:#bbb,color:#888,stroke-dasharray: 4 4\n");
        return sb.ToString();
    }
}

file static class StringExtensions
{
    public static string? NullIfEmpty(this string s) => s.Length == 0 ? null : s;
}
