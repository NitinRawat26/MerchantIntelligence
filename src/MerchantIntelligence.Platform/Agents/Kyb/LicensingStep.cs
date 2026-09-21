using MerchantIntelligence.Platform.Licensing;
using MerchantIntelligence.Platform.Workflows;

namespace MerchantIntelligence.Platform.Agents.Kyb;

public sealed class LicensingStep : IAssessmentStep
{
    public WorkflowStepDescriptor Descriptor { get; } = new("licensing", "Licences & permits",
        "For merchant categories that cannot lawfully trade without a permit (food service, alcohol, pharmacy, healthcare, money services, gaming, ...) checks the licences the analyst attested from the merchant's documents: present, complete (number, issuer), in date. Attested evidence, not a registry lookup; an active company registration never satisfies a licence requirement.",
        [], [], false, []);

    public async Task ExecuteAsync(AssessmentContext ctx)
    {
        var intake = ctx.Intake;
        if (!LicensingAssessor.IsRegulated(intake.MerchantCategoryCode) && (intake.Licenses is null || intake.Licenses.Count == 0))
        {
            ctx.Licensing = new LicensingAssessment([], [], [], true);
            await ctx.SkipAsync(Descriptor, $"MCC {intake.MerchantCategoryCode} carries no licence requirement and none was attested.");
            return;
        }

        ctx.Licensing = await ctx.RunAsync(Descriptor,
            () => Task.FromResult(LicensingAssessor.Assess(intake, ctx.Profile, DateOnly.FromDateTime(DateTime.UtcNow))),
            r => $"{r.Requirements.Count} required · {r.Requirements.Count(q => q.Attested is not null)} attested" +
                 (r.Unrequested.Count > 0 ? $" · {r.Unrequested.Count} additional" : "") +
                 (r.Flags.Count > 0 ? $" · {r.Flags.Count} finding(s): {string.Join(", ", r.Flags.Select(f => f.Code))}" : " · no findings"));
    }
}
