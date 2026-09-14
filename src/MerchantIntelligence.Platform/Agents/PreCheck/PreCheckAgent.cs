using MerchantIntelligence.Kyb.Prohibited;
using MerchantIntelligence.MccValidation.Validation;
using MerchantIntelligence.Platform.Assessment;
using MerchantIntelligence.Platform.Workflows;

namespace MerchantIntelligence.Platform.Agents.PreCheck;

/// <summary>Is the application complete and internally consistent? Never blocks – says what missing evidence costs.</summary>
public sealed class PreCheckAgent : IAssessmentAgent
{
    public WorkflowAgentDescriptor Descriptor { get; } = new("precheck", "Pre-check agent",
        "Validate the application: is it complete, does the website match the story, is the category allowed?",
        "Runs the website, prohibited-business and MCC checks, then tells the analyst which evidence is missing and how that degrades the decision. It never stops the run.",
        ["website", "prohibited", "mcc"]);

    public Task<AgentReview> ReviewAsync(AssessmentContext ctx, IReadOnlyList<string> ownedSteps)
    {
        var findings = new List<AgentFinding>();
        var i = ctx.Intake;

        if (ctx.SiteUrl is null)
            findings.Add(new(AgentFindingKind.Advisory, "NO_WEBSITE", "No website URL supplied.",
                "Website compliance (10% of score) and MCC validation are coverage gaps; the score is reweighted over the remaining checks and coverage drops."));
        else if (ctx.Website is { Reachable: false })
            findings.Add(new(AgentFindingKind.Advisory, "WEBSITE_UNREACHABLE", $"{ctx.SiteUrl.Host} could not be reached.",
                "Website compliance scored as failing; the MCC check has no evidence to compare against."));

        if (!ctx.HasBankInput)
            findings.Add(new(AgentFindingKind.Advisory, "NO_BANK_STATEMENT", "No bank statement supplied.",
                "Declared volume is benchmarked only against industry norms; statement-vs-declared checks cannot run and plausibility is less certain."));
        if (!ctx.HasFinancialInput && i.PriorYearRevenue is null)
            findings.Add(new(AgentFindingKind.Advisory, "NO_FINANCIALS", "No P&L / balance sheet and no prior-year revenue supplied.",
                "Revenue-vs-volume and margin checks cannot run; the credit view rests on declared figures alone."));
        if (i.Owners.Count == 0)
            findings.Add(new(AgentFindingKind.Advisory, "NO_OWNERS", "No beneficial owners or principals declared.",
                "Sanctions / PEP screening covers the business name only; MATCH is queried without principals."));
        if (string.IsNullOrWhiteSpace(i.BusinessDescription) || i.BusinessDescription.Trim().Length < 20)
            findings.Add(new(AgentFindingKind.Advisory, "THIN_DESCRIPTION", "Business description is missing or very short.",
                "The prohibited / restricted classifier has little text to work from; a mis-categorised business may not be caught."));
        if (i.EmployeeCount is null || i.YearsInBusiness is null)
            findings.Add(new(AgentFindingKind.Advisory, "THIN_PROFILE", "Headcount and/or years in business not supplied.",
                "Headcount and tenure plausibility checks are skipped; volume plausibility relies on ticket size alone."));

        if (ctx.Mcc is { Verdict: MccVerdict.Inconsistent } m)
            findings.Add(new(AgentFindingKind.Observation, "MCC_CONTRADICTED", $"Website evidence contradicts declared MCC {m.DeclaredMcc} ({m.DeclaredDescription}); {m.AccuracyPercent:F0}% agreement.",
                m.SuggestedMccs.Count > 0 ? $"Consider MCC {m.SuggestedMccs[0].Mcc} instead." : null));
        if (ctx.Prohibited is { } p && p.Verdict != BusinessPolicy.Acceptable)
            findings.Add(new(AgentFindingKind.Observation, "CATEGORY_" + p.Verdict.ToString().ToUpperInvariant(),
                $"Business classified as {p.Verdict}: {string.Join(", ", p.Matches.Take(3).Select(x => x.Category.Name))}.",
                p.Verdict == BusinessPolicy.Prohibited ? "Hard stop – the policy rules will decline regardless of score." : "Enhanced review required by policy."));

        var advisories = findings.Count(f => f.Kind == AgentFindingKind.Advisory);
        var summary = advisories == 0
            ? "Application complete: every evidence source supplied."
            : $"Application accepted with {advisories} missing evidence item(s); decision quality is reduced but the run continues.";
        return Task.FromResult(new AgentReview(summary, findings));
    }
}
