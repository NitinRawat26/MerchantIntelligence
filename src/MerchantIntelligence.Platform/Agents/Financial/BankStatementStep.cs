using MerchantIntelligence.Platform.Workflows;
using MerchantIntelligence.Underwriting.Financials;

namespace MerchantIntelligence.Platform.Agents.Financial;

public sealed class BankStatementStep : IAssessmentStep
{
    public WorkflowStepDescriptor Descriptor { get; } = new("bank", "Bank statement cash-flow analysis",
        "Parses the uploaded bank statement into monthly inflows, card deposits, NSF events and processor names.",
        [], [], false, []);

    public async Task ExecuteAsync(AssessmentContext ctx)
    {
        if (!ctx.HasBankInput) { await ctx.SkipAsync(Descriptor, "No bank statement supplied."); return; }
        ctx.Bank = await ctx.RunAsync(Descriptor, () =>
        {
            var doc = ctx.BankStatementDocument;
            var parsed = doc is not null ? BankStatementParser.Parse(new MemoryStream(doc.Content), doc.FileName) : BankStatementParser.ParseCsv(ctx.Intake.BankStatementCsv!);
            return Task.FromResult(CashFlowAnalyzer.Analyze(parsed));
        }, b => $"{b.MonthsCovered} month(s) · implied annual card volume ${b.ImpliedAnnualCardVolume:N0} · {b.NsfOrOverdraftCount} NSF/overdraft · {b.Flags.Count} flag(s)");
    }
}
