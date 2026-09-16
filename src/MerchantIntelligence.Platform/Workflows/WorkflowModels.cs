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

/// <summary>What a stop-gate looks at in the result of the step it is attached to.</summary>
public enum StopGateTrigger
{
    /// <summary>The step established a hard stop (sanctions match, MATCH listing, prohibited category).</summary>
    HardStop,
    /// <summary>The step threw and was recorded as Failed.</summary>
    Failed,
    /// <summary>The step raised at least one High or VeryHigh flag.</summary>
    HighSeverityFlag,
    /// <summary>The step raised a flag with the configured <see cref="StopGateConfig.Code"/>.</summary>
    Flag
}

/// <summary>How far a fired stop-gate reaches.</summary>
public enum StopGateScope
{
    /// <summary>Skip the remaining non-required steps of the owning agent only.</summary>
    Agent,
    /// <summary>Skip the remaining non-required steps of every agent still to run.</summary>
    Workflow
}

/// <summary>Outcome a stop-gate can impose on the run once it fires. Approve can never be forced.</summary>
public enum ForcedOutcome
{
    None,
    Refer,
    Decline
}

/// <summary>
/// A rule evaluated right after its step completes. When the trigger matches, the remaining evidence steps in scope are
/// skipped, the owning agent reports Failed (so on-fail transitions take effect) and the decision can be forced to Refer or Decline.
/// </summary>
public sealed class StopGateConfig
{
    public StopGateTrigger When { get; set; } = StopGateTrigger.HardStop;
    /// <summary>Flag code to match; required when <see cref="When"/> is <see cref="StopGateTrigger.Flag"/>.</summary>
    public string? Code { get; set; }
    public StopGateScope Scope { get; set; } = StopGateScope.Agent;
    public ForcedOutcome ForceOutcome { get; set; } = ForcedOutcome.None;
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
    /// <summary>
    /// Position inside an <see cref="AgentStepOrder.Ordered"/> agent. Steps sharing a slot run together; null means "own slot,
    /// after the previous step". Ignored by <see cref="AgentStepOrder.Parallel"/> agents and always overridden by dependencies.
    /// </summary>
    public int? Slot { get; set; }
    public StopGateConfig? StopGate { get; set; }
}

/// <summary>How an agent schedules the steps it owns.</summary>
public enum AgentStepOrder
{
    /// <summary>Only data dependencies constrain the steps; everything else runs concurrently.</summary>
    Parallel,
    /// <summary>Steps run in list (or slot) order, one slot at a time; dependencies can only push a step later.</summary>
    Ordered
}

/// <summary>When an agent-to-agent transition lets the target run.</summary>
public enum TransitionCondition
{
    /// <summary>The target runs whatever the source's outcome was.</summary>
    Always,
    /// <summary>The target runs only when the source succeeded (no failed step, no fired stop-gate).</summary>
    Success,
    /// <summary>The target runs only when the source failed or a stop-gate fired inside it.</summary>
    Fail
}

/// <summary>How an agent ended; what transitions out of it are matched against.</summary>
public enum AgentOutcome
{
    /// <summary>Every owned step ran without failure and no stop-gate fired.</summary>
    Succeeded,
    /// <summary>A step failed, a stop-gate fired or the review threw.</summary>
    Failed,
    /// <summary>The agent did not run: disabled, or none of its incoming transitions held.</summary>
    Skipped
}

/// <summary>
/// Control flow between two agents: the target waits for the source to settle and runs when the condition holds. An agent
/// with several incoming transitions runs when at least one of them holds; with none satisfied it is skipped. Agents
/// without incoming transitions (or dependencies) start immediately.
/// </summary>
public sealed class WorkflowTransition
{
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
    public TransitionCondition When { get; set; } = TransitionCondition.Always;
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
    /// <summary>Agent-to-agent control flow. Null or empty means agents are ordered by step dependencies alone.</summary>
    public List<WorkflowTransition>? Transitions { get; set; }

    public WorkflowStepConfig? Step(string id) => Steps.FirstOrDefault(s => s.Id == id);
}

/// <summary>An agent as configured by the operator: which steps (tools) it owns, how it orders them and whether it takes part in the run.</summary>
public sealed class WorkflowAgentConfig
{
    public string Id { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public List<string> Steps { get; set; } = new();
    public AgentStepOrder StepOrder { get; set; } = AgentStepOrder.Parallel;
}

/// <summary>Static description of an agent: its mandate and the steps it owns by default.</summary>
public sealed record WorkflowAgentDescriptor(
    string Id,
    string Name,
    string Mandate,
    string Description,
    IReadOnlyList<string> DefaultSteps);

/// <summary>
/// Where an agent sits in the run: which stage it executes in, which agents it waits for, the incoming transitions that
/// let it run (empty = unconditional) and the order its own steps run in (each inner list runs concurrently).
/// </summary>
public sealed record WorkflowAgentPlan(string Id, string Name, bool Enabled, int Stage, IReadOnlyList<string> Steps, IReadOnlyList<string> WaitsFor,
    IReadOnlyList<WorkflowTransition> RunsWhen, IReadOnlyList<IReadOnlyList<string>> StepStages);

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
