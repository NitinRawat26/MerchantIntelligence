using MerchantIntelligence.Kyb.Registry;
using MerchantIntelligence.Platform.Profiling;
using MerchantIntelligence.Platform.Workflows;

namespace MerchantIntelligence.Platform.Agents.Kyb;

public sealed class VerificationStep(BusinessVerificationService verification) : IAssessmentStep
{
    public WorkflowStepDescriptor Descriptor { get; } = new("verification", "Business identity verification",
        "Matches the legal entity against the public registries the profile says should hold it (state / national company registers for private companies, GLEIF and SEC EDGAR for public or large ones, none for sole proprietorships) and verifies the address.",
        [], [], false, []);

    public async Task ExecuteAsync(AssessmentContext ctx)
    {
        var scope = ScopeOf(ctx.Profile?.RegistryScope);
        ctx.Verification = await ctx.RunAsync(Descriptor, () => verification.VerifyAsync(ctx.Intake.Business, scope, ctx.CancellationToken),
            v => $"{v.Status} ({v.ConfidencePercent:F0}% confidence) · registers: {Describe(scope)}{(v.BestMatch is null ? "" : $" · best match {v.BestMatch.Record.LegalName} via {v.BestMatch.Record.Source}")}");
    }

    internal static RegistryQueryScope ScopeOf(RegistryScope? scope) => scope switch
    {
        RegistryScope.Local => RegistryQueryScope.Local,
        RegistryScope.TaxExempt => RegistryQueryScope.TaxExempt,
        RegistryScope.None => RegistryQueryScope.None,
        _ => RegistryQueryScope.All
    };

    private static string Describe(RegistryQueryScope scope) => scope switch
    {
        RegistryQueryScope.Local => "company registers authoritative, LEI / SEC absence expected",
        RegistryQueryScope.TaxExempt => "tax-exempt registers authoritative, company registers fallback",
        RegistryQueryScope.None => "not consulted (no register holds this legal form)",
        _ => "all"
    };
}
