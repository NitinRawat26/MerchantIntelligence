using MerchantIntelligence.Platform.Workflows;
using MerchantIntelligence.Platform.Financial;
using MerchantIntelligence.Underwriting.Financials;

namespace MerchantIntelligence.Platform.Agents.Financial;

public sealed class BankStatementStep : IAssessmentStep
{
    public WorkflowStepDescriptor Descriptor { get; } = new("bank", "Bank statement cash-flow analysis",
        "Parses the uploaded bank statement into monthly inflows, card deposits, NSF events and processor names, then reads it as small-merchant evidence: required for Micro / Small, account holder vs applicant, deposits vs declared volume, months without deposits, existing card payouts.",
        [], [], false, []);

    public async Task ExecuteAsync(AssessmentContext ctx)
    {
        if (!ctx.HasBankInput)
        {
            ctx.BankEvidence = BankEvidenceAssessor.Assess(ctx.Intake, ctx.Profile, null, ctx.Intake.BankAccountHolderName);
            await ctx.SkipAsync(Descriptor, ctx.BankEvidence.Required
                ? $"No bank statement supplied — required primary evidence for a {ctx.Profile!.Segment} merchant; recorded as a coverage gap."
                : "No bank statement supplied.");
            return;
        }
        ctx.Bank = await ctx.RunAsync(Descriptor, () =>
        {
            var doc = ctx.BankStatementDocument;
            var parsed = doc is not null ? BankStatementParser.Parse(new MemoryStream(doc.Content), doc.FileName) : BankStatementParser.ParseCsv(ctx.Intake.BankStatementCsv!);
            var analysis = CashFlowAnalyzer.Analyze(parsed);
            ctx.BankEvidence = BankEvidenceAssessor.Assess(ctx.Intake, ctx.Profile, analysis, ctx.Intake.BankAccountHolderName);
            return Task.FromResult(analysis);
        }, b => $"{b.MonthsCovered} month(s) · implied annual card volume ${b.ImpliedAnnualCardVolume:N0} · {b.NsfOrOverdraftCount} NSF/overdraft · {b.Flags.Count} flag(s)"
               + (ctx.BankEvidence is { } e ? $" · evidence: {(e.Required ? "required" : "optional")}{(e.HolderNameScore is { } h ? $", holder {h:P0}" : "")}{(e.InflowsToDeclaredRatio is { } r ? $", deposits {r:P0} of declared" : "")}, {e.Flags.Count} finding(s)" : ""));
    }
}
