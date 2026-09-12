
using System.Text.Json;
using MerchantIntelligence.CreditDecision;
using MerchantIntelligence.Kyb;
using MerchantIntelligence.Kyb.Compliance;
using MerchantIntelligence.Kyb.Prohibited;
using MerchantIntelligence.Kyb.Registry;
using MerchantIntelligence.Kyb.Sanctions;
using MerchantIntelligence.MccValidation.Taxonomy;
using MerchantIntelligence.MccValidation.Validation;
using MerchantIntelligence.Platform.Cases;
using MerchantIntelligence.Platform.Integrations;
using MerchantIntelligence.Platform.ModelOps;
using MerchantIntelligence.Platform.Rules;
using MerchantIntelligence.Platform.Scoring;
using MerchantIntelligence.Platform.Storage;
using MerchantIntelligence.Underwriting.Explainability;
using MerchantIntelligence.Underwriting.Financials;
using MerchantIntelligence.Underwriting.Plausibility;
using MerchantIntelligence.Underwriting.Pricing;
using Microsoft.Extensions.Logging;

namespace MerchantIntelligence.Platform.Assessment;

/// <summary>Pure derivations shared by the workflow steps: signal collection, KYB risk roll-up, decision and explainability text.</summary>
internal static class AssessmentComposer
{
    internal static MatchPrincipal ToPrincipal(BeneficialOwner o)
    {
        var parts = o.FullName.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        return new MatchPrincipal(parts[0], parts.Length > 1 ? parts[1] : string.Empty, o.DateOfBirth, null);
    }

    internal static ProhibitedBusinessResult CombineProhibited(ProhibitedBusinessResult fromDescription, ProhibitedBusinessResult? fromWebsite)
    {
        if (fromWebsite is null) return fromDescription;
        var verdict = (BusinessPolicy)Math.Max((int)fromDescription.Verdict, (int)fromWebsite.Verdict);
        var matches = fromDescription.Matches.Concat(fromWebsite.Matches)
            .GroupBy(m => m.Category.Code).Select(g => g.OrderByDescending(m => m.Score).First())
            .OrderByDescending(m => m.Score).ToList();
        var flags = fromDescription.Flags.Concat(fromWebsite.Flags).GroupBy(f => f.Code).Select(g => g.First()).ToList();
        return new ProhibitedBusinessResult(verdict, matches, flags);
    }

    /// <summary>A verification whose every registry call failed says nothing about the entity; treat it as not run.</summary>
    internal static bool RegistriesReachable(BusinessVerificationResult v) => v.Sources.Count == 0 || v.Sources.Any(x => x.Succeeded);

    /// <summary>Screening against zero loaded lists is not a clear result; treat it as not run.</summary>
    internal static bool ListsLoaded(ScreeningReport s) => s.Lists.Any(l => l.Error is null && l.EntityCount > 0);

    /// <summary>Adverse media counts as checked only if every subject's lookup succeeded (or none was attempted).</summary>
    internal static bool MediaChecked(ScreeningReport s) => s.Subjects.All(x => x.AdverseMedia is null || x.AdverseMedia.Succeeded);

    internal static RiskTier? KybRisk(BusinessVerificationResult? v, ScreeningReport? s, WebsiteComplianceResult? w)
    {
        if (v is not null && !RegistriesReachable(v)) v = null;
        if (s is not null && !ListsLoaded(s)) s = null;
        if (v is null && s is null && w is null) return null;
        var tiers = new List<RiskTier>();
        if (v is not null) tiers.AddRange(v.Flags.Select(f => f.Severity));
        if (s is not null) tiers.Add(s.OverallRisk);
        if (w is not null) tiers.AddRange(w.Checks.Where(c => c.Status == CheckStatus.Fail).Select(c => c.Severity));
        return tiers.Count == 0 ? RiskTier.Low : tiers.Max();
    }

    internal static List<RiskSignal> CollectSignals(BusinessVerificationResult? v, ScreeningReport? s, WebsiteComplianceResult? w, ProhibitedBusinessResult? p,
        MccValidationResult? m, CashFlowAnalysis? b, FinancialStatementAnalysis? f, VolumePlausibilityResult? pl)
    {
        var list = new List<RiskSignal>();
        if (v is not null) list.AddRange(v.Flags.Select(x => new RiskSignal("verification", x.Code, x.Message, x.Severity)));
        if (s is not null) list.AddRange(s.Flags.Select(x => new RiskSignal("screening", x.Code, x.Message, x.Severity)));
        if (w is not null) list.AddRange(w.Checks.Where(c => c.Status == CheckStatus.Fail).Select(c => new RiskSignal("website", $"WEB_{c.Code}", c.Detail, c.Severity)));
        if (p is not null) list.AddRange(p.Flags.Select(x => new RiskSignal("prohibited", x.Code, x.Message, x.Severity)));
        if (m is not null) list.AddRange(m.RiskFlags.Select(x => new RiskSignal("mcc", x.Code, x.Message, x.Severity)));
        if (b is not null) list.AddRange(b.Flags.Select(x => new RiskSignal("bank", x.Code, x.Message, x.Severity)));
        if (f is not null) list.AddRange(f.Flags.Select(x => new RiskSignal("financials", x.Code, x.Message, x.Severity)));
        if (pl is not null) list.AddRange(pl.Flags.Select(x => new RiskSignal("plausibility", x.Code, x.Message, x.Severity)));
        return list.GroupBy(x => (x.Source, x.Code)).Select(g => g.First()).ToList();
    }

    internal static AssessmentDecision BuildDecision(UnifiedRiskScore? score, RulesEvaluation? rules, IReadOnlyList<AssessmentStep> steps, bool forcedRefer = false)
    {
        if (score is null || rules is null)
            return new AssessmentDecision("Refer", 0, "Unknown", 0, "n/a", steps.Any(s => s.Id == "score" && s.Status == StepStatus.Skipped)
                ? "The unified score step is disabled in the active workflow; refer to an analyst for manual review."
                : "The unified score or rules engine failed; refer to a senior analyst for manual review.");

        var failed = steps.Count(s => s.Status == StepStatus.Failed);
        if (forcedRefer && rules.Outcome == RuleOutcome.Approve)
        {
            var forced = $"Refer for manual review. Score {score.Score}/1000 ({score.Tier}) and rule {rules.DecidingRule} would approve, but {failed} check(s) failed under a refer-on-failure policy.";
            return new AssessmentDecision(RuleOutcome.Refer.ToString(), score.Score, score.Tier, score.CoveragePercent, rules.RuleSetVersion, forced);
        }
        var summary = rules.Outcome switch
        {
            RuleOutcome.Decline => $"Decline. Policy rule {rules.DecidingRule} fired on a score of {score.Score}/1000 ({score.Tier}).",
            RuleOutcome.Approve => $"Approve. Score {score.Score}/1000 ({score.Tier}) with {score.CoveragePercent:F0}% score-signal coverage and no blocking findings; rule {rules.DecidingRule} applies.",
            _ => $"Refer for manual review. Score {score.Score}/1000 ({score.Tier}); rule {rules.DecidingRule} requires an analyst decision."
        };
        if (failed > 0) summary += $" {failed} check(s) could not be completed and are counted as coverage gaps.";
        return new AssessmentDecision(rules.Outcome.ToString(), score.Score, score.Tier, score.CoveragePercent, rules.RuleSetVersion, summary);
    }

    internal static AssessmentExplainability BuildExplainability(AssessmentIntake intake, AssessmentDecision decision, BusinessVerificationResult? v, ScreeningReport? s,
        WebsiteComplianceResult? w, ProhibitedBusinessResult? p, MccValidationResult? m, MatchResult? match, CashFlowAnalysis? b, FinancialStatementAnalysis? f,
        VolumePlausibilityResult? pl, DecisionResult? credit, DecisionExplanation? explanation, TermsRecommendation? terms, UnifiedRiskScore? score,
        RulesEvaluation? rules, IReadOnlyList<RiskSignal> signals)
    {
        var outcomes = new List<CheckOutcome>();
        var narrative = new List<string>();
        var next = new List<string>();

        // Identity
        if (v is null) { outcomes.Add(new("Business identity", "Not run", "Registry verification failed or was unavailable.", RiskTier.Medium, false)); next.Add("Re-run entity verification or obtain a certificate of incorporation manually."); }
        else if (!RegistriesReachable(v))
        {
            var detail = $"No public registry responded ({string.Join(", ", v.Sources.Select(x => $"{x.Source}: {x.Error ?? "failed"}"))}); the entity could not be checked.";
            outcomes.Add(new("Business identity", "Unavailable", detail, RiskTier.Medium, false));
            narrative.Add($"Identity: registry verification could not be completed because every public source failed. {detail}");
            next.Add("Re-run entity verification once public registries are reachable, or obtain registration documents manually.");
        }
        else
        {
            var sev = v.Status is VerificationStatus.Verified ? RiskTier.Low : v.Status is VerificationStatus.PartialMatch ? RiskTier.Medium : RiskTier.High;
            var detail = v.BestMatch is null
                ? $"No registry record matched '{intake.Business.LegalName}' across {v.Sources.Count} source(s) ({string.Join(", ", v.Sources.Select(x => x.Source + (x.Succeeded ? "" : " – failed")))})."
                : $"Best match '{v.BestMatch.Record.LegalName}' from {v.BestMatch.Record.Source} (name {v.BestMatch.NameScore:P0}, address {v.BestMatch.AddressScore:P0}, overall {v.BestMatch.OverallScore:P0})"
                  + (v.EntityAgeMonths is { } age ? $"; entity age {age} months" : "") + (v.Address is { } a ? $"; address {(a.Verified ? "verified" : "not verified")} via {a.Provider}" : "") + ".";
            outcomes.Add(new("Business identity", $"{v.Status} ({v.ConfidencePercent:F0}%)", detail, sev, true));
            narrative.Add($"Identity: the legal entity is {v.Status.ToString().ToLowerInvariant()} with {v.ConfidencePercent:F0}% confidence. {detail}");
            if (v.Status is VerificationStatus.NotFound or VerificationStatus.Inconclusive) next.Add("Request registration documents; public registry coverage (GLEIF / SEC EDGAR) is limited for small private companies.");
        }

        // Screening
        if (s is null) { outcomes.Add(new("Sanctions / PEP / media", "Not run", "Screening failed.", RiskTier.High, false)); next.Add("Re-run sanctions screening before any approval."); }
        else if (!ListsLoaded(s))
        {
            var detail = $"No sanctions list could be loaded ({string.Join(", ", s.Lists.Select(l => $"{l.ListName}: {l.Error ?? "empty"}"))}); subjects were not screened.";
            outcomes.Add(new("Sanctions / PEP / media", "Unavailable", detail, RiskTier.High, false));
            narrative.Add($"Screening: no sanctions, PEP or adverse-media screening was possible because every list download failed – this must not be read as clear. {detail}");
            next.Add("Re-run sanctions screening before any approval; list downloads failed.");
        }
        else
        {
            var hits = s.Subjects.Where(x => x.PotentialMatch).ToList();
            var sanctions = s.Flags.Any(x => x.Code == "SANCTIONS_MATCH");
            var pep = s.Flags.Any(x => x.Code == "PEP_MATCH");
            var media = s.Flags.Any(x => x.Code == "ADVERSE_MEDIA");
            var detail = hits.Count == 0
                ? $"All {s.Subjects.Count} subject(s) clear against {s.Lists.Count(l => l.Error is null)} loaded list(s) ({string.Join(", ", s.Lists.Where(l => l.Error is null).Select(l => l.ListName))})."
                : string.Join(" ", hits.Select(h => $"{h.Subject.Name}: {h.Hits.Count} hit(s) – {string.Join("; ", h.Hits.Take(2).Select(x => $"{x.Entity.Name} [{x.Entity.ListName}] {x.Score:P0}"))}."));
            var mediaNote = s.Subjects.Select(x => x.AdverseMedia).Where(x => x is not null).ToList();
            var mediaChecked = MediaChecked(s);
            if (mediaNote.Count > 0)
                detail += mediaChecked
                    ? $" Adverse media: {mediaNote.Sum(x => x!.NegativeCount)} negative of {mediaNote.Sum(x => x!.ArticleCount)} article(s)."
                    : $" Adverse media NOT checked ({mediaNote.First(x => !x!.Succeeded)!.Error}) – media result is unknown, not clear.";
            var result = sanctions ? "SANCTIONS MATCH" : pep ? "PEP match" : media ? "Adverse media" : hits.Count > 0 ? "Possible match"
                : mediaChecked ? "Clear" : "Lists clear · media unavailable";
            var severity = !mediaChecked && s.OverallRisk == RiskTier.Low ? RiskTier.Medium : s.OverallRisk;
            outcomes.Add(new("Sanctions / PEP / media", result, detail, severity, mediaChecked || hits.Count > 0));
            narrative.Add($"Screening: {(sanctions ? "a confirmed sanctions match was found – this is a hard stop." : hits.Count > 0 ? "possible matches need analyst disposition." : mediaChecked ? "no sanctions, PEP or adverse-media findings." : "no sanctions or PEP list hits, but adverse media could not be checked so the media dimension remains a coverage gap.")} {detail}");
            if (!mediaChecked) next.Add("Re-run adverse-media screening (GDELT lookup failed or was rate-limited) before final approval.");
            if (hits.Count > 0 && !sanctions) next.Add("Disposition each possible sanctions match (confirm or discount with date of birth / nationality evidence).");
            if (pep) next.Add("Apply enhanced due diligence: source of wealth and senior approval for the politically exposed person.");
        }

        // Website
        if (w is null) outcomes.Add(new("Website compliance", "Not run", intake.Business.WebsiteUrl is null ? "No website supplied." : "Scan failed.", RiskTier.Medium, false));
        else
        {
            var fails = w.Checks.Where(c => c.Status == CheckStatus.Fail).ToList();
            var detail = !w.Reachable ? "Website unreachable." :
                $"Grade {w.Grade} ({w.Score}/100) over {w.PagesAnalyzed.Count} page(s). " +
                (fails.Count == 0 ? "All card-brand disclosure checks passed." : $"Failed: {string.Join("; ", fails.Select(c => c.Title))}.") +
                (w.Domain?.Registered is { } reg ? $" Domain registered {reg:yyyy-MM-dd}." : "");
            outcomes.Add(new("Website compliance", w.Reachable ? $"Grade {w.Grade}" : "Unreachable", detail, fails.Count == 0 ? RiskTier.Low : fails.Max(c => c.Severity), true));
            narrative.Add($"Website: {detail}");
            if (fails.Count > 0) next.Add($"Ask the merchant to remediate website disclosures: {string.Join(", ", fails.Select(c => c.Title))}.");
        }

        // Prohibited
        if (p is null) outcomes.Add(new("Prohibited / restricted", "Not run", "Classification failed.", RiskTier.Medium, false));
        else
        {
            var detail = p.Matches.Count == 0 ? "No prohibited or restricted category detected in the description or website text."
                : string.Join(" ", p.Matches.Take(3).Select(x => $"{x.Category.Name} ({x.Category.Policy}, score {x.Score:F2}; keywords: {string.Join(", ", x.MatchedKeywords.Take(5))}{(x.DeclaredMccInCategory ? "; declared MCC belongs to this category" : "")})."));
            var sev = p.Verdict switch { BusinessPolicy.Prohibited => RiskTier.High, BusinessPolicy.Restricted => RiskTier.High, BusinessPolicy.HighRisk => RiskTier.Medium, _ => RiskTier.Low };
            outcomes.Add(new("Prohibited / restricted", p.Verdict.ToString(), detail, sev, true));
            narrative.Add($"Business type: verdict {p.Verdict}. {detail}");
            if (p.Verdict == BusinessPolicy.Restricted) next.Add("Collect licensing evidence / card-brand registration for the restricted category before boarding.");
            if (p.Verdict == BusinessPolicy.Prohibited) next.Add("Prohibited business type – decline unless the classification is proven wrong.");
        }

        // MCC
        if (m is null) outcomes.Add(new("MCC validation", "Not run", intake.Business.WebsiteUrl is null ? "Needs a website." : "Validation failed.", RiskTier.Low, false));
        else
        {
            var detail = $"Declared MCC {m.DeclaredMcc} ({m.DeclaredDescription}, {m.DeclaredRiskTier} risk) is {m.Verdict} with website evidence at {m.AccuracyPercent:F0}% agreement." +
                (m.SuggestedMccs.Count > 0 ? $" Evidence suggests: {string.Join(", ", m.SuggestedMccs.Take(3).Select(c => $"{c.Mcc} {c.Description} ({c.Score:P0})"))}." : "");
            var sev = m.Verdict switch { MccVerdict.Consistent => RiskTier.Low, MccVerdict.Questionable => RiskTier.Medium, MccVerdict.Inconsistent => RiskTier.High, _ => RiskTier.Medium };
            outcomes.Add(new("MCC validation", m.Verdict.ToString(), detail, sev, m.Verdict != MccVerdict.Insufficient));
            narrative.Add($"MCC: {detail}");
            if (m.Verdict == MccVerdict.Inconsistent) next.Add("Confirm the correct MCC with the merchant; misclassification affects interchange and brand-risk programmes.");
        }

        // MATCH
        if (match is null) outcomes.Add(new("MATCH / TMF", "Not run", "Inquiry failed.", RiskTier.Medium, false));
        else if (match.Availability != MatchAvailability.Available)
        {
            outcomes.Add(new("MATCH / TMF", match.Availability.ToString(), $"{match.Message ?? "No MATCH provider configured."} Treated as unknown, not clear.", RiskTier.Medium, false));
            narrative.Add("MATCH: no terminated-merchant provider is configured, so prior terminations are unknown (coverage gap) rather than clear.");
            next.Add("Run a MATCH / terminated-merchant inquiry through your sponsor bank before final approval.");
        }
        else
        {
            var detail = match.Found == true ? $"{match.Hits.Count} hit(s) via {match.Provider}: {string.Join("; ", match.Hits.Select(h => $"{h.ReasonCode} {h.ReasonDescription} ({h.MatchedOn})"))}." : $"No record via {match.Provider}.";
            outcomes.Add(new("MATCH / TMF", match.Found == true ? "FOUND" : "Clear", detail, match.Found == true ? RiskTier.High : RiskTier.Low, true));
            narrative.Add($"MATCH: {detail}");
        }

        // Bank statement
        if (b is null) outcomes.Add(new("Bank statement", "Not run", intake.BankStatementCsv is null ? "No statement supplied." : "Could not parse statement.", RiskTier.Low, false));
        else
        {
            var detail = $"{b.MonthsCovered} month(s) {b.PeriodStart:yyyy-MM-dd}–{b.PeriodEnd:yyyy-MM-dd}, {b.TransactionCount} transactions. Average monthly inflows ${b.AverageMonthlyInflows:N0}, card deposits ${b.AverageMonthlyCardDeposits:N0}/month (implied annual card volume ${b.ImpliedAnnualCardVolume:N0} vs declared ${intake.AnnualVolume:N0}). {b.NsfOrOverdraftCount} NSF/overdraft, {b.NegativeBalanceDays} negative-balance day(s), volatility {b.InflowVolatility:F2}." +
                (b.DetectedProcessors.Count > 0 ? $" Processors: {string.Join(", ", b.DetectedProcessors)}." : "") +
                (b.Flags.Count > 0 ? $" Flags: {string.Join("; ", b.Flags.Select(x => x.Message))}" : "");
            outcomes.Add(new("Bank statement", $"{b.MonthsCovered}m · {b.Flags.Count} flag(s)", detail, b.Flags.Count == 0 ? RiskTier.Low : b.Flags.Max(x => x.Severity), true));
            narrative.Add($"Cash flow: {detail}");
        }

        // Financials
        if (f is null) outcomes.Add(new("P&L / balance sheet", "Not run", intake.FinancialStatementText is null ? "No statement supplied." : "Could not parse statement.", RiskTier.Low, false));
        else
        {
            var ratios = string.Join(", ", f.Ratios.Where(r => r.Value is not null).Select(r => $"{r.Name} {r.Value:F2} ({r.Assessment})"));
            var detail = $"Revenue {(f.Statement.Revenue is { } r ? "$" + r.ToString("N0") : "n/a")}, net income {(f.Statement.NetIncome is { } n ? "$" + n.ToString("N0") : "n/a")}. Ratios: {ratios}." +
                (f.Flags.Count > 0 ? $" Flags: {string.Join("; ", f.Flags.Select(x => x.Message))}" : "");
            outcomes.Add(new("P&L / balance sheet", $"{f.Flags.Count} flag(s)", detail, f.Flags.Count == 0 ? RiskTier.Low : f.Flags.Max(x => x.Severity), true));
            narrative.Add($"Financials: {detail}");
        }

        // Plausibility
        if (pl is null) outcomes.Add(new("Volume plausibility", "Not run", "Analysis failed.", RiskTier.Medium, false));
        else
        {
            var detail = $"{pl.Verdict} ({pl.PlausibilityScore}/100). " + string.Join(" ", pl.Metrics.Select(x => $"{x.Name}: {x.Value} vs {x.Benchmark} – {x.Assessment}.")) +
                (pl.Flags.Count > 0 ? $" Flags: {string.Join("; ", pl.Flags.Select(x => x.Message))}" : "");
            outcomes.Add(new("Volume plausibility", $"{pl.PlausibilityScore}/100", detail, pl.PlausibilityScore >= 70 ? RiskTier.Low : pl.PlausibilityScore >= 40 ? RiskTier.Medium : RiskTier.High, true));
            narrative.Add($"Declared volume of ${intake.AnnualVolume:N0} at ${intake.AverageTicket:N0} average ticket: {detail}");
            if (pl.PlausibilityScore < 40) next.Add("Declared volume is implausible against benchmarks – obtain processing history or revise the declaration.");
        }

        // Credit
        if (credit is null || explanation is null) outcomes.Add(new("Credit model", "Not run", "Champion model unavailable.", RiskTier.Medium, false));
        else
        {
            var top = explanation.Contributions.OrderByDescending(c => Math.Abs(c.Contribution)).Take(3)
                .Select(c => $"{c.Feature}={c.Value} ({(c.Contribution >= 0 ? "+" : "")}{c.Contribution:P1}, {c.Direction})");
            var detail = $"Champion model predicts {credit.Decision} with {credit.Confidence:P0} confidence (approve probability {credit.Probabilities.GetValueOrDefault(Decision.Approved):P0}; baseline {explanation.BaselineProbability:P0}). Top drivers: {string.Join("; ", top)}. {explanation.Narrative}";
            outcomes.Add(new("Credit model", $"{credit.Decision} ({credit.Confidence:P0})", detail, credit.Decision == Decision.Approved ? RiskTier.Low : RiskTier.Medium, true));
            narrative.Add($"Credit model: {detail}");
        }

        // Terms
        if (terms is not null)
        {
            var detail = $"Risk band {terms.RiskBand} (composite {terms.RiskScore:F2}). Reserve: {terms.Reserve.Type} {terms.Reserve.RollingPercent:F1}% for {terms.Reserve.RollingDays} days" +
                (terms.Reserve.CapAmount > 0 ? $" capped at ${terms.Reserve.CapAmount:N0}" : "") + $". Pricing: IC+ {terms.Pricing.InterchangePlusMarkupBps:F0} bps + ${terms.Pricing.PerTransactionFee:N2}/txn, settlement T+{terms.Pricing.SettlementDelayDays}, monthly cap ${terms.Pricing.MonthlyVolumeCap:N0}. Estimated exposure ${terms.EstimatedExposure:N0}. Factors: {string.Join("; ", terms.Factors.Select(x => $"{x.Description} ({x.Effect})"))}.";
            outcomes.Add(new("Recommended terms", $"Band {terms.RiskBand}", detail, terms.RiskBand is "A" or "B" ? RiskTier.Low : terms.RiskBand is "C" ? RiskTier.Medium : RiskTier.High, true));
            narrative.Add($"Terms: {detail}");
        }

        // Score + rules
        if (score is not null && rules is not null)
        {
            var comps = string.Join("; ", score.Components.Select(c => c.Covered ? $"{c.Name} {c.Score:F0}/100 × {c.Weight:P0}" : $"{c.Name} not covered"));
            narrative.Add($"Unified score: {score.Score}/1000 ({score.Tier}), coverage {score.CoveragePercent:F0}%. Components: {comps}." +
                (score.HardStops.Count > 0 ? $" Hard stops: {string.Join(", ", score.HardStops)}." : "") +
                (score.CoverageGaps.Count > 0 ? $" Coverage gaps: {string.Join(", ", score.CoverageGaps)}." : ""));
            narrative.Add($"Policy: rule set v{rules.RuleSetVersion} evaluated {rules.MatchedRules.Count} matching rule(s) ({string.Join(", ", rules.MatchedRules.Select(r => $"{r.Id}→{r.Outcome}"))}); the deciding rule is {rules.DecidingRule}, giving {rules.Outcome}.");
            if (score.CoverageGaps.Count > 0) next.Add($"Close coverage gaps to strengthen the decision: {string.Join(", ", score.CoverageGaps)}.");
        }

        var findings = signals.Select(x => new ExplanationItem(x.Source, x.Code, x.Message, x.Severity, x.Source))
            .Concat(score?.ReasonCodes.Select(r => new ExplanationItem("score", r.Code, r.Description, r.Severity, r.Source)) ?? [])
            .GroupBy(x => x.Code).Select(g => g.First())
            .OrderByDescending(x => x.Severity).ToList();

        var headline = $"{decision.Outcome.ToUpperInvariant()} — {intake.Business.LegalName}: score {decision.Score}/1000 ({decision.Tier}), {outcomes.Count(o => o.Covered)} of {outcomes.Count} checks covered ({decision.CoveragePercent:F0}% score-signal coverage), " +
                       $"{findings.Count(x => x.Severity == RiskTier.High)} high / {findings.Count(x => x.Severity == RiskTier.Medium)} medium finding(s).";

        var coverageGaps = outcomes.Where(o => !o.Covered).Select(o => $"{o.Check}: {o.Result} – {o.Detail}")
            .Concat(score?.CoverageGaps.Select(g => $"Score component not covered: {g}") ?? []).ToList();
        return new AssessmentExplainability(headline, narrative, outcomes, findings, score?.Components ?? [], score?.ReasonCodes ?? [],
            explanation?.Contributions ?? [], rules?.MatchedRules ?? [], rules?.DecidingRule, coverageGaps, score?.HardStops ?? [], next.Distinct().ToList());
    }

    internal static AssessmentIntakeSummary Summarise(AssessmentIntake i, UploadedDocument? bank, UploadedDocument? fin) => new(
        i.Business, i.Owners, i.BusinessDescription, i.MerchantCategoryCode, i.AnnualVolume, i.AverageTicket, i.HighestTicket, i.ExistingRelationship,
        i.DeliveryDays, i.CardNotPresentShare, i.OffersSubscriptions, i.OffersFreeTrials, i.EmployeeCount, i.YearsInBusiness, i.PriorYearRevenue,
        i.WebsiteProductCount, i.HasPhysicalLocation,
        bank?.FileName ?? (i.BankStatementCsv is null ? null : "inline CSV"),
        fin?.FileName ?? (i.FinancialStatementText is null ? null : "inline text"),
        i.ExternalRef, i.Actor, i.LocationCount);
}
