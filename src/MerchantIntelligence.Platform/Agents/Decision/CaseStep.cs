using MerchantIntelligence.Platform.Assessment;
using MerchantIntelligence.Platform.Cases;
using MerchantIntelligence.Platform.Workflows;

namespace MerchantIntelligence.Platform.Agents.Decision;

public sealed class CaseStep(CaseService cases) : IAssessmentStep
{
    public WorkflowStepDescriptor Descriptor { get; } = new("case", "Case creation & audit",
        "Opens a review case carrying the decision snapshot so analysts can work it in the queue.",
        ["score"], ["score"], false,
        [new("priority", "Low | Normal | High | Critical", "(from score)", "Force a case priority instead of deriving it from the risk tier.")]);

    public async Task ExecuteAsync(AssessmentContext ctx)
    {
        if (!ctx.Intake.CreateCase) { await ctx.SkipAsync(Descriptor, "Case creation disabled by caller."); return; }
        var decision = AssessmentComposer.BuildDecision(ctx.Score, ctx.Rules, ctx.Steps, ctx.ForcedRefer, ctx.ForcedDecline);
        var priority = ctx.Param<CasePriority?>(Descriptor.Id, "priority", null);
        ctx.Case = await ctx.RunAsync(Descriptor, () => Task.FromResult(cases.Create(ctx.Intake.Business.LegalName, ctx.Intake.Actor, ctx.Intake.ExternalRef ?? ctx.AssessmentId,
                priority, ctx.Score?.Score, ctx.Score?.Tier, ctx.Rules?.Outcome,
                new { assessmentId = ctx.AssessmentId, decision, coverageGaps = ctx.Score?.CoverageGaps, hardStops = ctx.Score?.HardStops, decisionLogId = ctx.DecisionLogId, workflow = ctx.Workflow.Version })),
            c => $"{c.Id} · {c.Status} · priority {c.Priority}");
    }
}
