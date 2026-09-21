using MerchantIntelligence.Platform.Owners;
using MerchantIntelligence.Platform.Workflows;

namespace MerchantIntelligence.Platform.Agents.Kyb;

public sealed class OwnerIdentityStep(PrincipalRegistry principals) : IAssessmentStep
{
    public WorkflowStepDescriptor Descriptor { get; } = new("owners", "Owner identity depth",
        "Checks the declared principals themselves: identity completeness (date of birth, nationality, address, role), age against years in business, whether the owner lives at the business address, and whether the same person has appeared behind other applications (duplicate / velocity). For a Micro or Small merchant the owner is the primary identity evidence, so gaps weigh more.",
        [], [], false, []);

    public async Task ExecuteAsync(AssessmentContext ctx)
    {
        var intake = ctx.Intake;
        if (intake.Owners.Count == 0)
        {
            ctx.Owners = new OwnerAssessment([], 0, false, [], false);
            await ctx.SkipAsync(Descriptor, "No principal declared; owner identity could not be checked.");
            return;
        }

        ctx.Owners = await ctx.RunAsync(Descriptor, () =>
            {
                var today = DateOnly.FromDateTime(DateTime.UtcNow);
                var result = OwnerIdentityAssessor.Assess(intake, ctx.Profile, o => principals.PriorApplications(o, intake.Business.LegalName, ctx.AssessmentId), today);
                principals.Remember(ctx.AssessmentId, intake.Business.LegalName, intake.Owners, ctx.StartedAt);
                return Task.FromResult(result);
            },
            r => $"{r.Owners.Count} principal(s) · identity {r.CompletenessPercent:P0} complete{(r.HomeBased ? " · home-based" : "")}{(r.Flags.Count > 0 ? $" · {r.Flags.Count} finding(s): {string.Join(", ", r.Flags.Select(f => f.Code))}" : " · no findings")}");
    }
}
