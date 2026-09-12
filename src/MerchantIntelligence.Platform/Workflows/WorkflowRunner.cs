using System.Diagnostics;
using MerchantIntelligence.Platform.Assessment;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;

namespace MerchantIntelligence.Platform.Workflows;

/// <summary>Progress event raised by a step; surfaced through the workflow's event stream.</summary>
public sealed class StepProgressEvent(AssessmentStep step) : WorkflowEvent(step)
{
    public AssessmentStep Step { get; } = step;
}

/// <summary>Progress event raised by an agent (Running when it starts, then its final report).</summary>
public sealed class AgentProgressEvent(AgentReport report) : WorkflowEvent(report)
{
    public AgentReport Report { get; } = report;
}

/// <summary>Marker message passed along the graph; all real state lives in the <see cref="AssessmentContext"/>.</summary>
public sealed record RunToken(string AssessmentId);

/// <summary>
/// Executes an assessment by compiling the active <see cref="WorkflowDefinition"/> into a Microsoft Agent Framework
/// workflow graph: one executor per enabled agent, agents of the same stage fanned out concurrently, a barrier
/// between stages. Each agent runs the steps it owns (concurrently where their dependencies allow) and then
/// reviews the combined result.
/// </summary>
public sealed class WorkflowRunner
{
    private readonly IReadOnlyDictionary<string, IAssessmentStep> _steps;
    private readonly IReadOnlyDictionary<string, IAssessmentAgent> _agents;
    private readonly WorkflowPlanner _planner;
    private readonly ILogger<WorkflowRunner> _logger;

    public WorkflowRunner(IEnumerable<IAssessmentStep> steps, IEnumerable<IAssessmentAgent> agents, WorkflowPlanner planner, ILogger<WorkflowRunner> logger)
    {
        _steps = steps.ToDictionary(s => s.Descriptor.Id);
        _agents = agents.ToDictionary(a => a.Descriptor.Id);
        _planner = planner;
        _logger = logger;
    }

    /// <summary>Compiles the definition into an Agent Framework workflow bound to the given run context.</summary>
    public Workflow Compile(WorkflowDefinition def, AssessmentContext ctx, WorkflowPlan? plan = null)
    {
        plan ??= _planner.Plan(def);
        var owner = _planner.OwnersOf(def);
        var start = new FunctionExecutor<RunToken, RunToken>("intake", (t, _, _) => t);
        var builder = new WorkflowBuilder(start);
        ExecutorBinding previous = start;

        foreach (var stage in plan.Agents.Where(a => a.Enabled).GroupBy(a => a.Stage).OrderBy(g => g.Key))
        {
            var executors = stage.Select(a => (ExecutorBinding)new AgentExecutor(_agents[a.Id], a.Steps.Select(id => _steps[id]).ToList(),
                def.Steps.Select(s => s.Id).Where(id => owner.GetValueOrDefault(id) == a.Id).ToList(),
                _planner.IntraStages(def, a.Steps), ctx, _logger)).ToList();
            var gate = new StageGate($"stage-{stage.Key}", executors.Count);
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
        ctx.AgentOrder = plan.Agents.Select(a => a.Id).ToList();
        var owner = _planner.OwnersOf(def);
        var disabledAgents = plan.Agents.Where(a => !a.Enabled).Select(a => a.Id).ToHashSet();
        foreach (var id in plan.Disabled)
        {
            var reason = disabledAgents.Contains(owner[id])
                ? $"Agent '{owner[id]}' disabled in workflow '{def.Name}' (v{def.Version})."
                : $"Disabled in workflow '{def.Name}' (v{def.Version}).";
            await ctx.SkipAsync(_steps[id].Descriptor, reason);
        }
        foreach (var a in plan.Agents.Where(a => !a.Enabled))
        {
            var d = _agents[a.Id].Descriptor;
            var ownedAll = def.Steps.Select(s => s.Id).Where(id => owner.GetValueOrDefault(id) == a.Id).ToList();
            var report = new AgentReport(d.Id, d.Name, d.Mandate, StepStatus.Skipped, ownedAll, $"Disabled in workflow '{def.Name}' (v{def.Version}).", [], 0);
            ctx.AddAgent(report);
            await ctx.ReportAgentAsync(report);
        }

        var workflow = Compile(def, ctx, plan);
        _logger.LogInformation("Assessment {Id}: running workflow '{Workflow}' v{Version} with {Agents} agent(s) in {Stages} stage(s)",
            ctx.AssessmentId, def.Name, def.Version, plan.Agents.Count(a => a.Enabled), plan.Agents.Where(a => a.Enabled).Select(a => a.Stage).DefaultIfEmpty(0).Max());

        await using var run = await InProcessExecution.Default.RunStreamingAsync(workflow, new RunToken(ctx.AssessmentId), cancellationToken: ctx.CancellationToken);
        await foreach (var ev in run.WatchStreamAsync(ctx.CancellationToken))
        {
            switch (ev)
            {
                case StepProgressEvent p:
                    await ctx.ReportAsync(p.Step);
                    break;
                case AgentProgressEvent a:
                    await ctx.ReportAgentAsync(a.Report);
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

    /// <summary>Runs one agent: its tools stage by stage (concurrently inside a stage), then its review.</summary>
    private sealed class AgentExecutor(IAssessmentAgent agent, IReadOnlyList<IAssessmentStep> steps, IReadOnlyList<string> ownedAll, List<List<string>> stages, AssessmentContext ctx, ILogger logger)
        : Executor<RunToken, RunToken>("agent-" + agent.Descriptor.Id)
    {
        public override async ValueTask<RunToken> HandleAsync(RunToken message, IWorkflowContext context, CancellationToken cancellationToken)
        {
            var d = agent.Descriptor;
            var owned = steps.Select(s => s.Descriptor.Id).ToList();
            await context.AddEventAsync(new AgentProgressEvent(new AgentReport(d.Id, d.Name, d.Mandate, StepStatus.Running, ownedAll, "Running…", [], 0)), cancellationToken);
            var sw = Stopwatch.StartNew();

            using (ctx.Capture(s => context.AddEventAsync(new StepProgressEvent(s), cancellationToken).AsTask()))
            {
                var byId = steps.ToDictionary(s => s.Descriptor.Id);
                foreach (var stage in stages)
                    await Task.WhenAll(stage.Select(id => RunStep(byId[id])));
            }

            AgentReport report;
            try
            {
                var review = await agent.ReviewAsync(ctx, owned);
                var status = ctx.StepsOf(owned).Any(s => s.Status == StepStatus.Failed) ? StepStatus.Failed : StepStatus.Succeeded;
                report = new AgentReport(d.Id, d.Name, d.Mandate, status, ownedAll, review.Summary, review.Findings, sw.ElapsedMilliseconds);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Agent {Agent} review failed", d.Id);
                report = new AgentReport(d.Id, d.Name, d.Mandate, StepStatus.Failed, ownedAll, $"Tools ran; review failed: {ex.Message}", [], sw.ElapsedMilliseconds);
            }
            ctx.AddAgent(report);
            await context.AddEventAsync(new AgentProgressEvent(report), cancellationToken);
            return message;
        }

        private async Task RunStep(IAssessmentStep step)
        {
            if (ctx.Workflow.HaltOnHardStop && !step.Descriptor.Required && step.Descriptor.Id != "case" && ctx.HardStop is { } hardStop)
            {
                await ctx.SkipAsync(step.Descriptor, $"Skipped: hard stop {hardStop} already established and the workflow halts on hard stops.");
                return;
            }
            await step.ExecuteAsync(ctx);
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
