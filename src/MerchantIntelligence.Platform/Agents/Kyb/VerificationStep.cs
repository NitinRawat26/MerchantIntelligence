using MerchantIntelligence.Kyb.Registry;
using MerchantIntelligence.Platform.Workflows;

namespace MerchantIntelligence.Platform.Agents.Kyb;

public sealed class VerificationStep(BusinessVerificationService verification) : IAssessmentStep
{
    public WorkflowStepDescriptor Descriptor { get; } = new("verification", "Business identity verification",
        "Matches the legal entity against public registries (GLEIF, SEC EDGAR, Companies House…) and verifies the address.",
        [], [], false, []);

    public async Task ExecuteAsync(AssessmentContext ctx) =>
        ctx.Verification = await ctx.RunAsync(Descriptor, () => verification.VerifyAsync(ctx.Intake.Business, ctx.CancellationToken),
            v => $"{v.Status} ({v.ConfidencePercent:F0}% confidence){(v.BestMatch is null ? "" : $" · best match {v.BestMatch.Record.LegalName} via {v.BestMatch.Record.Source}")}");
}
