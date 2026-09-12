using MerchantIntelligence.Platform.Assessment;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;

namespace MerchantIntelligence.Platform.Workflows;

/// <summary>Progress event raised by a step executor; surfaced through the workflow's event stream.</summary>
public sealed class StepProgressEvent(AssessmentStep step) : WorkflowEvent(step)
{
    public AssessmentStep Step { get; } = step;
}

/// <summary>Marker message passed along the graph; all real state lives in the <see cref="AssessmentContext"/>.</summary>
public sealed record RunToken(string AssessmentId);

/// <summary>
/// Executes an assessment by compiling the active <see cref="WorkflowDefinition"/> into a Microsoft Agent Framework
/// workflow graph: one executor per enabled step, fan-out per stage, a barrier executor between stages.
/// </summary>
public sealed class WorkflowRunner
{
    private readonly IReadOnlyDictionary<string, IAssessmentStep> _steps;
    private readonly WorkflowPlanner _planner;
    private readonly ILogger<WorkflowRunner> _logger;

    public WorkflowRunner(IEnumerable<IAssessmentStep> steps, WorkflowPlanner planner, ILogger<WorkflowRunner> logger)
    {
        _steps = steps.ToDictionary(s => s.Descriptor.Id);
        _planner = planner;
        _logger = logger;
    }

    /// <summary>Compiles the definition into an Agent Framework workflow bound to the given run context.</summary>
    public Workflow Compile(WorkflowDefinition def, AssessmentContext ctx, WorkflowPlan? plan = null)
    {
        plan ??= _planner.Plan(def);
        var start = new FunctionExecutor<RunToken, RunToken>("intake", (t, _, _) => t);
        var builder = new WorkflowBuilder(start);
        ExecutorBinding previous = start;

        foreach (var stage in plan.Stages)
        {
            var executors = stage.Steps.Select(id => (ExecutorBinding)new StepExecutor(_steps[id], ctx)).ToList();
            var gate = new StageGate($"stage-{stage.Index}", executors.Count);
            builder.AddFanOutEdge(previous, executors);
            foreach (var e in executors) builder.AddEdge(e, gate);
            previous = gate;
        }

        var finish = new FunctionExecutor<RunToken, RunToken>("finish", async (t, wctx, ct) => { await wctx.YieldOutputAsync(t, ct); return t; });
        builder.AddEdge(previous, finish).WithOutputFrom(finish);
        return builder.WithName(def.Name).WithDescription(def.Description ?? string.Empty).Build();
    }

    public async Task RunAsync(WorkflowDefinition def, AssessmentContext ctx)
    {
        var plan = _planner.Plan(def);
        foreach (var id in plan.Disabled)
            await ctx.SkipAsync(_steps[id].Descriptor, $"Disabled in workflow '{def.Name}' (v{def.Version}).");

        var workflow = Compile(def, ctx, plan);
        _logger.LogInformation("Assessment {Id}: running workflow '{Workflow}' v{Version} in {Stages} stage(s)", ctx.AssessmentId, def.Name, def.Version, plan.Stages.Count);

        await using var run = await InProcessExecution.Default.RunStreamingAsync(workflow, new RunToken(ctx.AssessmentId), cancellationToken: ctx.CancellationToken);
        await foreach (var ev in run.WatchStreamAsync(ctx.CancellationToken))
        {
            switch (ev)
            {
                case StepProgressEvent p:
                    await ctx.ReportAsync(p.Step);
                    break;
                case WorkflowErrorEvent err:
                    throw Unwrap(err.Exception ?? new InvalidOperationException("Workflow failed without an exception."));
                case ExecutorFailedEvent failed:
                    throw Unwrap(failed.Data ?? new InvalidOperationException($"Executor {failed.ExecutorId} failed without an exception."));
            }
        }
        ctx.CancellationToken.ThrowIfCancellationRequested();
    }

    private static Exception Unwrap(Exception ex)
    {
        while (ex is System.Reflection.TargetInvocationException { InnerException: { } inner }) ex = inner;
        return ex is StepAbortedException or OperationCanceledException ? ex : new InvalidOperationException($"Workflow execution failed: {ex.Message}", ex);
    }

    private sealed class StepExecutor(IAssessmentStep step, AssessmentContext ctx) : Executor<RunToken, RunToken>(step.Descriptor.Id)
    {
        public override async ValueTask<RunToken> HandleAsync(RunToken message, IWorkflowContext context, CancellationToken cancellationToken)
        {
            var def = ctx.Workflow;
            if (def.HaltOnHardStop && !step.Descriptor.Required && step.Descriptor.Id != "case" && ctx.HardStop is { } hardStop)
            {
                await ctx.SkipAsync(step.Descriptor, $"Skipped: hard stop {hardStop} already established and the workflow halts on hard stops.");
                return message;
            }
            using var _ = ctx.Capture(s => context.AddEventAsync(new StepProgressEvent(s), cancellationToken).AsTask());
            await step.ExecuteAsync(ctx);
            return message;
        }
    }

    /// <summary>Forwards a single token once every executor of the preceding stage has reported in.</summary>
    private sealed class StageGate(string id, int expected) : Executor<RunToken>(id)
    {
        private int _arrived;

        public override async ValueTask HandleAsync(RunToken message, IWorkflowContext context, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _arrived) == expected)
                await context.SendMessageAsync(message, cancellationToken: cancellationToken);
        }

        protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder) => base.ConfigureProtocol(protocolBuilder).SendsMessage<RunToken>();
    }
}
