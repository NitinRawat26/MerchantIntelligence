using MerchantIntelligence.Platform.Assessment;
using MerchantIntelligence.Platform.Workflows;

namespace MerchantIntelligence.Platform.Agents.Financial;

/// <summary>Can the merchant sustain the declared volume, and what does the credit model say?</summary>
public sealed class FinancialAgent : IAssessmentAgent
{
    public WorkflowAgentDescriptor Descriptor { get; } = new("financial", "Financial & credit agent",
        "Reconcile declared volume with statements and financials, then score credit risk.",
        "Runs statement and P&L analysis, volume plausibility and the credit model; reconciles the three views of revenue and flags where they disagree.",
        ["bank", "financials", "plausibility", "credit"]);

    public Task<AgentReview> ReviewAsync(AssessmentContext ctx, IReadOnlyList<string> ownedSteps)
    {
        var findings = new List<AgentFinding>();
        var declared = ctx.Intake.AnnualVolume;

        if (ctx.Bank is { } bank && declared > 0)
        {
            var ratio = bank.ImpliedAnnualCardVolume / declared;
            if (ratio < 0.7m || ratio > 1.5m)
                findings.Add(new(AgentFindingKind.Observation, "STATEMENT_VS_DECLARED",
                    $"Statements imply {AgentText.Money(bank.ImpliedAnnualCardVolume)} annual card volume vs {AgentText.Money(declared)} declared ({ratio:F2}×).",
                    ratio < 0.7m ? "Declared volume is not supported by history – possible under-declaration or volume split across acquirers." : "Declared volume exceeds demonstrated processing – check growth story."));
            if (bank.NsfOrOverdraftCount > 0)
                findings.Add(new(AgentFindingKind.Observation, "NSF_EVENTS", $"{bank.NsfOrOverdraftCount} NSF / overdraft event(s) over {bank.MonthsCovered} month(s).", "Liquidity stress; consider a higher reserve."));
            if (bank.DetectedProcessors.Count > 1)
                findings.Add(new(AgentFindingKind.Observation, "MULTIPLE_PROCESSORS", $"Deposits from {bank.DetectedProcessors.Count} processors: {string.Join(", ", bank.DetectedProcessors)}.", "Volume may be split across acquirers."));
        }
        if (ctx.Financials?.Statement.Revenue is { } revenue && declared > 0)
        {
            var ratio = declared / revenue;
            if (ratio > 1.2m)
                findings.Add(new(AgentFindingKind.Observation, "VOLUME_EXCEEDS_REVENUE", $"Declared card volume {AgentText.Money(declared)} is {ratio:F2}× reported revenue {AgentText.Money(revenue)}.", "Card volume above total revenue is implausible unless revenue is stale."));
            if (ctx.Financials.Statement.NetIncome is { } net && net < 0)
                findings.Add(new(AgentFindingKind.Observation, "LOSS_MAKING", $"Net loss of {AgentText.Money(-net)} reported.", "Credit exposure is higher; reserve and settlement delay should reflect it."));
        }
        if (ctx.Plausibility is { } p && ctx.Credit is { } c && p.PlausibilityScore < 40 && c.Decision == CreditDecision.Decision.Approved)
            findings.Add(new(AgentFindingKind.Observation, "MODEL_VS_PLAUSIBILITY", $"Credit model approves ({c.Confidence:P0}) while volume plausibility is {p.Verdict} ({p.PlausibilityScore}/100).", "The model does not see plausibility; weigh the analyst's judgement."));

        var evidence = new List<string>();
        if (ctx.Bank is not null) evidence.Add($"{ctx.Bank.MonthsCovered}m statements");
        if (ctx.Financials is not null) evidence.Add("P&L");
        var plaus = ctx.Plausibility is null ? "plausibility not run" : $"plausibility {ctx.Plausibility.Verdict} ({ctx.Plausibility.PlausibilityScore}/100)";
        var credit = ctx.Credit is null ? "no credit decision" : $"model {ctx.Credit.Decision} ({ctx.Credit.Confidence:P0})";
        var summary = $"{(evidence.Count == 0 ? "no financial evidence" : string.Join(" + ", evidence))} · {plaus} · {credit}";
        return Task.FromResult(new AgentReview(summary, findings));
    }
}
