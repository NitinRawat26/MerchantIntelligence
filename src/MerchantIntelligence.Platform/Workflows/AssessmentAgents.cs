using MerchantIntelligence.CreditDecision;
using MerchantIntelligence.Kyb.Prohibited;
using MerchantIntelligence.Kyb.Registry;
using MerchantIntelligence.Kyb.Sanctions;
using MerchantIntelligence.MccValidation.Taxonomy;
using MerchantIntelligence.MccValidation.Validation;
using MerchantIntelligence.Platform.Assessment;
using MerchantIntelligence.Platform.Integrations;
using MerchantIntelligence.Platform.Rules;

namespace MerchantIntelligence.Platform.Workflows;

/// <summary>What an agent concluded once its tools have run.</summary>
public sealed record AgentReview(string Summary, IReadOnlyList<AgentFinding> Findings);

/// <summary>
/// A rule-based agent: owns a group of steps (its tools), runs them through the workflow engine and then
/// reviews their combined output – raising advisories, taking extra deterministic actions and summarising
/// for the analyst. Agents never change scores, hard stops or the policy outcome.
/// </summary>
public interface IAssessmentAgent
{
    WorkflowAgentDescriptor Descriptor { get; }
    Task<AgentReview> ReviewAsync(AssessmentContext ctx, IReadOnlyList<string> ownedSteps);
}

internal static class AgentText
{
    public static string Money(decimal v) => "$" + v.ToString("N0");
    public static string Norm(string s) => new string(s.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
}

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

/// <summary>Is this business who it says it is, and is anyone behind it sanctioned, a PEP or terminated?</summary>
public sealed class KybAgent(SanctionsScreeningService screening) : IAssessmentAgent
{
    public WorkflowAgentDescriptor Descriptor { get; } = new("kyb", "KYB & screening agent",
        "Establish identity: registries, sanctions / PEP / adverse media and MATCH for the business and its people.",
        "Runs registry verification, screening and the MATCH inquiry. When the registry returns a legal name that differs from the application it re-screens that alias on its own initiative and folds the result into the screening report.",
        ["verification", "screening", "match"]);

    public async Task<AgentReview> ReviewAsync(AssessmentContext ctx, IReadOnlyList<string> ownedSteps)
    {
        var findings = new List<AgentFinding>();
        var v = ctx.Verification;
        var b = ctx.Intake.Business;

        if (v?.BestMatch is { } best)
        {
            var registryName = best.Record.LegalName;
            var known = new[] { b.LegalName, b.TradingName ?? string.Empty }.Select(AgentText.Norm).ToHashSet();
            if (!string.IsNullOrWhiteSpace(registryName) && !known.Contains(AgentText.Norm(registryName)))
            {
                if (ctx.Screening is { } report && ownedSteps.Contains("screening"))
                {
                    var alias = await screening.ScreenAsync([new ScreeningSubject(registryName, null, b.Country, false, $"Registry alias ({best.Record.Source})")], ctx.CancellationToken);
                    var hits = alias.Subjects.Count(s => s.PotentialMatch);
                    ctx.Screening = report with
                    {
                        Subjects = report.Subjects.Concat(alias.Subjects).ToList(),
                        Flags = report.Flags.Concat(alias.Flags).ToList(),
                        OverallRisk = (RiskTier)Math.Max((int)report.OverallRisk, (int)alias.OverallRisk)
                    };
                    findings.Add(new(AgentFindingKind.Action, "ALIAS_RESCREENED", $"Registry ({best.Record.Source}) knows this entity as \"{registryName}\"; re-screened the alias: {hits} potential match(es).",
                        hits > 0 ? "Alias hits are merged into the screening report and feed the score." : "No additional exposure found."));
                }
                else
                    findings.Add(new(AgentFindingKind.Observation, "ALIAS_UNSCREENED", $"Registry ({best.Record.Source}) knows this entity as \"{registryName}\", which was not screened because the screening step did not run."));
            }
            if (best.Record.Status is { } status && !status.Contains("active", StringComparison.OrdinalIgnoreCase))
                findings.Add(new(AgentFindingKind.Observation, "REGISTRY_STATUS", $"Registry status is \"{status}\".", "Confirm the entity is trading before onboarding."));
        }
        else if (v is not null && AssessmentComposer.RegistriesReachable(v))
            findings.Add(new(AgentFindingKind.Observation, "NOT_IN_REGISTRIES", $"No registry record found for \"{b.LegalName}\" ({v.Status}).", "Ask for a certificate of incorporation."));

        if (ctx.Screening is { } s)
        {
            if (s.Flags.Any(f => f.Code == "SANCTIONS_MATCH"))
                findings.Add(new(AgentFindingKind.Observation, "SANCTIONS_MATCH", "Potential sanctions match.", "Hard stop – policy declines regardless of score."));
            if (s.Flags.Any(f => f.Code == "PEP_MATCH"))
                findings.Add(new(AgentFindingKind.Observation, "PEP_EDD", "Politically exposed person identified.", "Enhanced due diligence is required; screening component scores lower."));
        }
        if (ctx.Match is { Availability: MatchAvailability.Available, Found: true } m)
            findings.Add(new(AgentFindingKind.Observation, "MATCH_LISTED", $"MATCH / TMF listing found ({m.Hits.Count} hit(s)).", "Hard stop – policy declines regardless of score."));
        else if (ctx.Match is { } mm && mm.Availability != MatchAvailability.Available)
            findings.Add(new(AgentFindingKind.Advisory, "MATCH_UNAVAILABLE", $"MATCH provider {mm.Availability}.", "Terminated-merchant history is unknown; the self-declared flag is used instead."));

        var identity = v is null ? "identity not verified" : $"identity {v.Status} ({v.ConfidencePercent:F0}%)";
        var exposure = ctx.Screening is null ? "screening not run" : $"screening {ctx.Screening.OverallRisk}";
        return new AgentReview($"{identity} · {exposure} · {findings.Count(f => f.Kind == AgentFindingKind.Action)} action(s) taken", findings);
    }
}

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
        if (ctx.Plausibility is { } p && ctx.Credit is { } c && p.PlausibilityScore < 40 && c.Decision == Decision.Approved)
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

/// <summary>Applies the deterministic score and policy, then writes the memo. It explains – it does not decide.</summary>
public sealed class DecisionAgent : IAssessmentAgent
{
    public WorkflowAgentDescriptor Descriptor { get; } = new("decision", "Decision & case agent",
        "Apply pricing, the unified score and policy rules; open the case and brief the analyst.",
        "Runs terms, unified score + rules and case creation. The outcome comes from the rules engine alone; this agent only gathers the other agents' findings into a brief for the analyst.",
        ["terms", "score", "case"]);

    public Task<AgentReview> ReviewAsync(AssessmentContext ctx, IReadOnlyList<string> ownedSteps)
    {
        var findings = new List<AgentFinding>();
        var decision = AssessmentComposer.BuildDecision(ctx.Score, ctx.Rules, ctx.Steps, ctx.ForcedRefer);

        if (ctx.Score is null)
            findings.Add(new(AgentFindingKind.Advisory, "NO_SCORE", "Unified score did not run; the outcome defaults to Refer.", "Enable the score step or investigate its failure."));
        foreach (var hs in ctx.Score?.HardStops ?? [])
            findings.Add(new(AgentFindingKind.Observation, "HARD_STOP", $"Hard stop: {hs}.", "Outcome is Decline by policy regardless of score."));
        if (ctx.ForcedRefer)
            findings.Add(new(AgentFindingKind.Observation, "FORCED_REFER", "A check failed under the Refer policy; Approve is not available for this run."));
        var gaps = ctx.Score?.CoverageGaps ?? [];
        if (gaps.Count > 0)
            findings.Add(new(AgentFindingKind.Advisory, "COVERAGE_GAPS", $"{gaps.Count} check(s) did not contribute: {string.Join(", ", gaps)}.", $"Coverage {decision.CoveragePercent:F0}% – score reweighted over the checks that ran."));

        var upstream = ctx.Agents.Where(a => a.Id != Descriptor.Id).SelectMany(a => a.Findings).ToList();
        var advisories = upstream.Count(f => f.Kind == AgentFindingKind.Advisory);
        var actions = upstream.Count(f => f.Kind == AgentFindingKind.Action);
        if (upstream.Count > 0)
            findings.Add(new(AgentFindingKind.Observation, "BRIEF", $"Upstream agents raised {upstream.Count} finding(s): {advisories} missing-evidence advisor{(advisories == 1 ? "y" : "ies")}, {actions} autonomous action(s), {upstream.Count - advisories - actions} observation(s)."));

        var terms = ctx.Terms is null ? "no terms" : $"band {ctx.Terms.RiskBand}";
        var rule = ctx.Rules is null ? "rules not evaluated" : $"via {ctx.Rules.DecidingRule}";
        return Task.FromResult(new AgentReview($"{decision.Outcome} · score {decision.Score}/1000 ({decision.Tier}) {rule} · {terms} · coverage {decision.CoveragePercent:F0}%", findings));
    }
}
