using MerchantIntelligence.MccValidation.Validation;
using MerchantIntelligence.Platform.Workflows;

namespace MerchantIntelligence.Platform.Agents.PreCheck;

public sealed class MccStep(MccValidationService mcc) : IAssessmentStep
{
    public WorkflowStepDescriptor Descriptor { get; } = new("mcc", "MCC validation",
        "Compares the declared MCC with what the website actually sells.",
        [], [], false, []);

    public async Task ExecuteAsync(AssessmentContext ctx)
    {
        if (ctx.SiteUrl is null) { await ctx.SkipAsync(Descriptor, "MCC validation needs a website to gather evidence from."); return; }
        ctx.Mcc = await ctx.RunAsync(Descriptor, () => mcc.ValidateAsync(ctx.Intake.MerchantCategoryCode, ctx.SiteUrl, ctx.CancellationToken),
            m => $"{m.Verdict} · declared {m.DeclaredMcc} {m.DeclaredDescription} · {m.AccuracyPercent:F0}% agreement");
    }
}
