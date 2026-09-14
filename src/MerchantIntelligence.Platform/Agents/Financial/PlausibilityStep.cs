using MerchantIntelligence.Platform.Workflows;
using MerchantIntelligence.Underwriting.Plausibility;

namespace MerchantIntelligence.Platform.Agents.Financial;

public sealed class PlausibilityStep(VolumePlausibilityAnalyzer plausibility) : IAssessmentStep
{
    public WorkflowStepDescriptor Descriptor { get; } = new("plausibility", "Declared volume plausibility",
        "Benchmarks declared volume, tickets, headcount and locations against the MCC; uses statement and P&L evidence when present.",
        ["bank", "financials"], ["bank", "financials"], false, []);

    public async Task ExecuteAsync(AssessmentContext ctx)
    {
        var i = ctx.Intake;
        ctx.Plausibility = await ctx.RunAsync(Descriptor,
            () => Task.FromResult(plausibility.Analyze(new VolumeDeclaration(i.AnnualVolume, i.AverageTicket, i.HighestTicket, i.MerchantCategoryCode,
                i.EmployeeCount, i.YearsInBusiness, i.PriorYearRevenue ?? ctx.Financials?.Statement.Revenue,
                ctx.Bank?.AverageMonthlyCardDeposits, i.WebsiteProductCount, i.HasPhysicalLocation, i.LocationCount))),
            p => $"{p.PlausibilityScore}/100 · {p.Verdict}");
    }
}
