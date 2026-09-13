using MerchantIntelligence.Platform.ModelOps;
using MerchantIntelligence.Platform.Workflows;
using MerchantIntelligence.Underwriting.Explainability;

namespace MerchantIntelligence.Platform.Agents.Financial;

public sealed class CreditStep(ModelOpsService modelOps, DecisionExplainer explainer) : IAssessmentStep
{
    public WorkflowStepDescriptor Descriptor { get; } = new("credit", "Credit decision & explainability",
        "Scores the application with the champion model and explains the prediction feature by feature.",
        ["match"], ["match"], false, []);

    public async Task ExecuteAsync(AssessmentContext ctx)
    {
        var app = ctx.Application;
        ctx.CreditExplanation = await ctx.RunAsync(Descriptor, () =>
        {
            (ctx.Credit, ctx.DecisionLogId) = modelOps.PredictAndLog(app);
            return Task.FromResult(explainer.Explain(app));
        }, e => $"{e.Decision} ({e.Confidence:P0}) · top driver {e.Contributions.OrderByDescending(c => Math.Abs(c.Contribution)).First().Feature}");
    }
}
