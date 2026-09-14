using MerchantIntelligence.Kyb.Prohibited;
using MerchantIntelligence.Platform.Assessment;
using MerchantIntelligence.Platform.Workflows;

namespace MerchantIntelligence.Platform.Agents.PreCheck;

public sealed class ProhibitedStep(ProhibitedBusinessDetector prohibited) : IAssessmentStep
{
    public WorkflowStepDescriptor Descriptor { get; } = new("prohibited", "Prohibited & restricted business check",
        "Classifies the business description (and website text when available) against the prohibited / restricted / high-risk category list.",
        ["website"], ["website"], false, []);

    public async Task ExecuteAsync(AssessmentContext ctx) =>
        ctx.Prohibited = await ctx.RunAsync(Descriptor,
            () => Task.FromResult(AssessmentComposer.CombineProhibited(prohibited.Analyze(null, ctx.Intake.BusinessDescription, ctx.Intake.MerchantCategoryCode), ctx.Website?.ProhibitedBusiness)),
            p => p.Matches.Count == 0 ? $"{p.Verdict} · no restricted category detected" : $"{p.Verdict} · {string.Join(", ", p.Matches.Take(3).Select(m => m.Category.Name))}");
}
