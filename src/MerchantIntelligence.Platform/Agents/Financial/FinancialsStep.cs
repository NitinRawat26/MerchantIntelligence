using MerchantIntelligence.Platform.Workflows;
using MerchantIntelligence.Underwriting.Financials;

namespace MerchantIntelligence.Platform.Agents.Financial;

public sealed class FinancialsStep : IAssessmentStep
{
    public WorkflowStepDescriptor Descriptor { get; } = new("financials", "P&L / balance sheet analysis",
        "Extracts revenue, net income and ratios from the uploaded financial statement.",
        [], [], false, []);

    public async Task ExecuteAsync(AssessmentContext ctx)
    {
        if (!ctx.HasFinancialInput) { await ctx.SkipAsync(Descriptor, "No P&L / balance sheet supplied."); return; }
        ctx.Financials = await ctx.RunAsync(Descriptor, () =>
        {
            var doc = ctx.FinancialStatementDocument;
            return Task.FromResult(doc is not null
                ? ProfitAndLossAnalyzer.Analyze(new MemoryStream(doc.Content), doc.FileName, ctx.Intake.AnnualVolume)
                : ProfitAndLossAnalyzer.AnalyzeText(ctx.Intake.FinancialStatementText!, ctx.Intake.AnnualVolume));
        }, f => $"Revenue {(f.Statement.Revenue is { } r ? "$" + r.ToString("N0") : "n/a")} · net income {(f.Statement.NetIncome is { } n ? "$" + n.ToString("N0") : "n/a")} · {f.Flags.Count} flag(s)");
    }
}
