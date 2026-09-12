using System.Text.Json;

namespace MerchantIntelligence.Platform.Workflows;

/// <summary>What happens to the assessment when a step throws.</summary>
public enum StepFailurePolicy
{
    /// <summary>Treat the check as not run (coverage gap) and continue – the historical behaviour.</summary>
    Skip,
    /// <summary>Continue, but force the final outcome to Refer so an analyst sees the gap.</summary>
    Refer,
    /// <summary>Stop the assessment; the run fails.</summary>
    Abort
}

/// <summary>One step of an assessment workflow as configured by the operator.</summary>
public sealed class WorkflowStepConfig
{
    public string Id { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public StepFailurePolicy OnFail { get; set; } = StepFailurePolicy.Skip;
    /// <summary>Overrides the step's default dependencies when set. Null keeps the catalogue defaults.</summary>
    public List<string>? DependsOn { get; set; }
    public Dictionary<string, JsonElement>? Params { get; set; }
}

/// <summary>
/// The ordered, data-driven definition of an assessment pipeline. Steps run in list order; steps whose
/// dependencies are already satisfied are grouped into the same stage and run concurrently.
/// </summary>
public sealed class WorkflowDefinition
{
    public string Name { get; set; } = "Default";
    public string Version { get; set; } = "default";
    public string? Description { get; set; }
    /// <summary>Once a hard stop (sanctions / MATCH / prohibited) is established, skip the remaining evidence steps.</summary>
    public bool HaltOnHardStop { get; set; }
    public List<WorkflowStepConfig> Steps { get; set; } = new();
    /// <summary>Which agent owns each step. Null keeps the catalogue grouping.</summary>
    public List<WorkflowAgentConfig>? Agents { get; set; }

    public WorkflowStepConfig? Step(string id) => Steps.FirstOrDefault(s => s.Id == id);
}

/// <summary>An agent as configured by the operator: which steps (tools) it owns and whether it takes part in the run.</summary>
public sealed class WorkflowAgentConfig
{
    public string Id { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public List<string> Steps { get; set; } = new();
}

/// <summary>Static description of an agent: its mandate and the steps it owns by default.</summary>
public sealed record WorkflowAgentDescriptor(
    string Id,
    string Name,
    string Mandate,
    string Description,
    IReadOnlyList<string> DefaultSteps);

/// <summary>Where an agent sits in the run: which stage it executes in and which agents it waits for.</summary>
public sealed record WorkflowAgentPlan(string Id, string Name, bool Enabled, int Stage, IReadOnlyList<string> Steps, IReadOnlyList<string> WaitsFor);

public sealed record WorkflowVersion(int Version, string Name, string Author, string? Comment, DateTimeOffset CreatedAt, bool Active, int EnabledSteps, int TotalSteps);

public sealed record WorkflowParamDescriptor(string Name, string Type, string Default, string Description);

/// <summary>Static description of a step as known to the engine; the catalogue the editor renders.</summary>
public sealed record WorkflowStepDescriptor(
    string Id,
    string Name,
    string Description,
    IReadOnlyList<string> DependsOn,
    IReadOnlyList<string> Consumes,
    bool Required,
    IReadOnlyList<WorkflowParamDescriptor> Params);

/// <summary>The resolved execution plan for a definition: stages, warnings and a Mermaid diagram of the graph.</summary>
public sealed record WorkflowPlan(
    IReadOnlyList<WorkflowStage> Stages,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Disabled,
    string Mermaid,
    IReadOnlyList<WorkflowAgentPlan> Agents);

public sealed record WorkflowStage(int Index, IReadOnlyList<string> Steps);

public sealed class WorkflowValidationException(string message) : Exception(message);
