using MerchantIntelligence.Platform.Assessment;
using MerchantIntelligence.Platform.Integrations;
using MerchantIntelligence.Platform.Workflows;

namespace MerchantIntelligence.Platform.Agents.Kyb;

public sealed class MatchStep(IMatchProvider match) : IAssessmentStep
{
    public WorkflowStepDescriptor Descriptor { get; } = new("match", "MATCH / terminated-merchant inquiry",
        "Queries the configured MATCH / TMF provider for the business and its principals.",
        [], [], false, []);

    public async Task ExecuteAsync(AssessmentContext ctx)
    {
        var b = ctx.Intake.Business;
        ctx.Match = await ctx.RunAsync(Descriptor,
            () => match.InquireAsync(new MatchInquiry(b.LegalName, b.TradingName, b.TaxId, b.Country, b.AddressLine, b.City, b.Region, b.PostalCode,
                ctx.Intake.Owners.Select(AssessmentComposer.ToPrincipal).ToList()), ctx.CancellationToken),
            m => m.Availability == MatchAvailability.Available
                ? (m.Found == true ? $"FOUND · {m.Hits.Count} hit(s) via {m.Provider}" : $"No record via {m.Provider}")
                : $"{m.Availability} · {m.Message ?? "unknown, not clear"}");
    }
}
