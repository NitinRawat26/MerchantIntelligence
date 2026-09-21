
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
using MerchantIntelligence.Platform.Owners;
using MerchantIntelligence.Platform.Profiling;
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

    private static string ReputationText(PlaceMatch pm)
    {
        if (pm.Record.Reputation is not { } rep) return string.Empty;
        var parts = new List<string>();
        if (rep.Rating is { } r) parts.Add($"rated {r:0.#}/{rep.RatingScale:0}{(rep.RatingCount is { } c ? $" ({c} ratings)" : "")}");
        if (rep.Popularity is { } p) parts.Add($"popularity {p:P0}");
        if (rep.ListedSince is { } s) parts.Add($"listed since {s:yyyy-MM-dd}");
        return parts.Count == 0 ? string.Empty : $" {pm.Record.Source}: {string.Join(", ", parts)}.";
    }

    /// <summary>Adverse media counts as checked only if every subject's lookup succeeded (or none was attempted).</summary>
    internal static bool MediaChecked(ScreeningReport s) => s.Subjects.All(x => x.AdverseMedia is null || x.AdverseMedia.Succeeded);

    internal static RiskTier? KybRisk(BusinessVerificationResult? v, ScreeningReport? s, WebsiteComplianceResult? w, OwnerAssessment? owners = null)
    {
        if (v is not null && !RegistriesReachable(v)) v = null;
        if (s is not null && !ListsLoaded(s)) s = null;
        if (owners is { Covered: false }) owners = null;
        if (v is null && s is null && w is null && owners is null) return null;
        var tiers = new List<RiskTier>();
        if (v is not null) tiers.AddRange(v.Flags.Select(f => f.Severity));
        if (owners is not null) tiers.AddRange(owners.Flags.Select(f => f.Severity));
        if (s is not null) tiers.Add(s.OverallRisk);
        if (w is not null) tiers.AddRange(w.Checks.Where(c => c.Status == CheckStatus.Fail).Select(c => c.Severity));
        return tiers.Count == 0 ? RiskTier.Low : tiers.Max();
    }

    internal static List<RiskSignal> CollectSignals(BusinessVerificationResult? v, ScreeningReport? s, WebsiteComplianceResult? w, ProhibitedBusinessResult? p,
        MccValidationResult? m, CashFlowAnalysis? b, FinancialStatementAnalysis? f, VolumePlausibilityResult? pl, OwnerAssessment? owners = null, Financial.BankEvidenceAssessment? bankEvidence = null)
    {
        var list = new List<RiskSignal>();
        if (v is not null) list.AddRange(v.Flags.Select(x => new RiskSignal("verification", x.Code, x.Message, x.Severity)));
        if (owners is not null) list.AddRange(owners.Flags.Select(x => new RiskSignal("owners", x.Code, x.Message, x.Severity)));
        if (s is not null) list.AddRange(s.Flags.Select(x => new RiskSignal("screening", x.Code, x.Message, x.Severity)));
        if (w is not null) list.AddRange(w.Checks.Where(c => c.Status == CheckStatus.Fail).Select(c => new RiskSignal("website", $"WEB_{c.Code}", c.Detail, c.Severity)));
        if (p is not null) list.AddRange(p.Flags.Select(x => new RiskSignal("prohibited", x.Code, x.Message, x.Severity)));
        if (m is not null) list.AddRange(m.RiskFlags.Select(x => new RiskSignal("mcc", x.Code, x.Message, x.Severity)));
        if (b is not null) list.AddRange(b.Flags.Select(x => new RiskSignal("bank", x.Code, x.Message, x.Severity)));
        if (bankEvidence is not null) list.AddRange(bankEvidence.Flags.Select(x => new RiskSignal("bank", x.Code, x.Message, x.Severity)));
        if (f is not null) list.AddRange(f.Flags.Select(x => new RiskSignal("financials", x.Code, x.Message, x.Severity)));
        if (pl is not null) list.AddRange(pl.Flags.Select(x => new RiskSignal("plausibility", x.Code, x.Message, x.Severity)));
        return list.GroupBy(x => (x.Source, x.Code)).Select(g => g.First()).ToList();
    }

    internal static AssessmentDecision BuildDecision(UnifiedRiskScore? score, RulesEvaluation? rules, IReadOnlyList<AssessmentStep> steps, bool forcedRefer = false, bool forcedDecline = false)
    {
        if (score is null || rules is null)
            return new AssessmentDecision(forcedDecline ? "Decline" : "Refer", 0, "Unknown", 0, "n/a", forcedDecline
                ? "Decline. A workflow stop-gate forced the outcome before the unified score ran."
                : steps.Any(s => s.Id == "score" && s.Status == StepStatus.Skipped)
                    ? "The unified score step is disabled in the active workflow; refer to an analyst for manual review."
                    : "The unified score or rules engine failed; refer to a senior analyst for manual review.");

        var failed = steps.Count(s => s.Status == StepStatus.Failed);
        if (forcedDecline && rules.Outcome != RuleOutcome.Decline)
            return new AssessmentDecision(RuleOutcome.Decline.ToString(), score.Score, score.Tier, score.CoveragePercent, rules.RuleSetVersion,
                $"Decline. Score {score.Score}/1000 ({score.Tier}) and rule {rules.DecidingRule} would give {rules.Outcome}, but a workflow stop-gate forced Decline.");
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
        RulesEvaluation? rules, IReadOnlyList<RiskSignal> signals, LocalPresenceResult? lp = null, MerchantProfile? profile = null, OwnerAssessment? owners = null, Financial.BankEvidenceAssessment? bankEvidence = null)
    {
        var outcomes = new List<CheckOutcome>();
        var narrative = new List<string>();
        var next = new List<string>();
        var adverseMedia = new List<AdverseMediaEvidence>();

        // Profile – scopes the run; it is reported so the reader knows which questions were asked and why.
        if (profile is not null)
        {
            var entity = MerchantProfiler.Describe(profile.EntityType) + (profile.EntityTypeInferred ? " (inferred)" : "");
            var locations = profile.LocationCount > 1 ? $", {profile.LocationCount} locations" : "";
            var skipped = profile.NotApplicable.Count == 0 ? "all checks applicable"
                : $"not applicable: {string.Join(", ", profile.NotApplicable.Select(n => n.StepId))}";
            var worst = profile.Findings.Count == 0 ? RiskTier.Low : profile.Findings.Max(pf => pf.Severity);
            outcomes.Add(new("Merchant profile", $"{profile.Segment} · {entity}", $"Registry scope {profile.RegistryScope}{locations}; {skipped}. Weights: {(profile.IsSmb ? "SMB" : "standard")} table.", worst, true));
            narrative.Add($"Profile: {profile.Segment} {entity}{locations}. {string.Join(" ", profile.Reasons.Take(3))}");
            foreach (var pf in profile.Findings) narrative.Add($"Profile finding {pf.Code}: {pf.Message}");
        }

        // Identity
        if (v is null) { outcomes.Add(new("Business identity", "Not run", "Registry verification failed or was unavailable.", RiskTier.Medium, false)); next.Add("Re-run entity verification or obtain a certificate of incorporation manually."); }
        else if (!RegistriesReachable(v))
        {
            var detail = $"No public registry responded ({string.Join(", ", v.Sources.Select(x => $"{x.Source}: {x.Error ?? "failed"}"))}); the entity could not be checked.";
            outcomes.Add(new("Business identity", "Unavailable", detail, RiskTier.Medium, false));
            narrative.Add($"Identity: registry verification could not be completed because every public source failed. {detail}");
            next.Add("Re-run entity verification once public registries are reachable, or obtain registration documents manually.");
        }
        else if (v.Status == VerificationStatus.NotApplicable)
        {
            var detail = "No company register holds this legal form (sole proprietorship / public body); registries were not consulted. Identity rests on owner KYC, local presence and bank-statement evidence.";
            outcomes.Add(new("Business identity", "Not applicable", detail, RiskTier.Low, false));
            narrative.Add($"Identity: registry verification is not applicable to this legal form. {detail}");
            if (v.LocalPresence?.Status != LocalPresenceStatus.Confirmed) next.Add("Confirm the owner's identity and the trading location directly (owner ID, DBA / assumed-name filing, lease or utility bill).");
        }
        else if (v.Status == VerificationStatus.Inconclusive)
        {
            var gap = v.Flags.FirstOrDefault(f => f.Code == "LOCAL_REGISTRY_UNAVAILABLE")?.Message
                ?? $"The registers expected to hold this entity did not answer ({string.Join(", ", v.Sources.Select(x => x.Source + (x.Succeeded ? "" : " – failed")))}).";
            outcomes.Add(new("Business identity", "Inconclusive", gap, RiskTier.Medium, false));
            narrative.Add($"Identity: registry verification is inconclusive – {gap}");
            next.Add("Verify via the state / national company register or request the certificate of formation.");
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
            if (v.Status is VerificationStatus.NotFound) next.Add(v.Scope == RegistryQueryScope.All
                ? "Request registration documents; public registry coverage (GLEIF / SEC EDGAR) is limited for small private companies."
                : "The entity is absent from the company registers that should list it; request the certificate of formation and confirm the registered name.");
            else if (v.BestMatch is null && v.LocalPresence?.Status == LocalPresenceStatus.Confirmed) next.Add("Identity rests on local-presence evidence only; request a certificate of formation or state registration to confirm the legal entity.");
        }

        // Local presence (places sources)
        lp ??= v?.LocalPresence;
        if (lp is null) outcomes.Add(new("Local presence", "Not run", "Places lookup was not run.", RiskTier.Low, false));
        else if (lp.Status == LocalPresenceStatus.NotChecked) outcomes.Add(new("Local presence", "Not checked", lp.Note ?? "No address supplied.", RiskTier.Low, false));
        else if (lp.Status == LocalPresenceStatus.Inconclusive)
        {
            var detail = lp.Note ?? $"Every places source failed ({string.Join(", ", lp.Sources.Select(x => $"{x.Source}: {x.Error ?? "failed"}"))}).";
            outcomes.Add(new("Local presence", "Unavailable", detail, RiskTier.Low, false));
            narrative.Add($"Local presence: could not be checked. {detail}");
        }
        else
        {
            var searched = string.Join(", ", lp.Sources.Where(x => x.Succeeded).Select(x => x.Source));
            var detail = lp.BestMatch is { } pm
                ? $"'{pm.Record.Name}' via {pm.Record.Source}{(pm.DistanceMeters is { } dm ? $", {dm:F0} m from the declared address" : "")}{(pm.Record.Category is null ? "" : $" ({pm.Record.Category})")}; name {pm.NameScore:P0}, overall {pm.OverallScore:P0}.{ReputationText(pm)} Searched {searched}."
                : $"No business matching the declared name near the address in {searched}.{(lp.Note is null ? "" : " " + lp.Note)}";
            outcomes.Add(new("Local presence", $"{lp.Status} ({lp.ConfidencePercent:F0}%)", detail, lp.Status == LocalPresenceStatus.NotFound ? RiskTier.Medium : RiskTier.Low, true));
            narrative.Add($"Local presence: {(lp.Status == LocalPresenceStatus.Confirmed ? "a business with this name trades at the declared address" : lp.Status == LocalPresenceStatus.PartialMatch ? "a similarly named business trades near the declared address" : "no business with this name was found near the declared address")} – trading evidence, not legal registration. {detail}");
            if (lp.Status == LocalPresenceStatus.NotFound && lp.Sources.Count(x => x.Succeeded) == 1) next.Add("Local presence was searched in OpenStreetMap only; configure a Foursquare or Google Places key, or request a utility bill / lease for the trading address.");
        }

        // Address type (residential / commercial / mixed / mail-drop) against the declared MCC
        if (lp?.AddressType is { } at)
        {
            var worst = at.Flags.Count == 0 ? RiskTier.Low : at.Flags.Max(f => f.Severity);
            var label = at.Type == AddressType.Unknown ? "Unknown" : $"{at.Type} ({at.Confidence:P0})";
            var detail = string.Join(" ", at.Evidence) + (at.Flags.Count == 0 ? "" : " " + string.Join(" ", at.Flags.Select(f => f.Message)));
            outcomes.Add(new("Address type", label, detail, worst, at.Covered && at.Type != AddressType.Unknown));
            narrative.Add($"Address type: {label}. {detail}");
            if (at.Type == AddressType.Cmra) next.Add("Declared address is a mail-drop / virtual office; obtain the physical trading address and a lease or utility bill for it.");
            else if (at.Flags.Any(f => f.Code == "ADDRESS_RESIDENTIAL_STOREFRONT_MCC")) next.Add("Storefront MCC at a residential address: confirm where customers are served (site visit, photos, lease) or re-code the MCC.");
        }

        // Digital footprint (contact e-mail domain via RDAP) – independent of the website scan
        if (lp?.Footprint is { } fp)
        {
            var worst = fp.Flags.Count == 0 ? RiskTier.Low : fp.Flags.Max(f => f.Severity);
            var result = fp.EmailIsFreeMail ? "Free-mail"
                : fp.EmailDomainAgeMonths is { } months ? $"Domain {months / 12} yr {months % 12} mo"
                : "Tenure unknown";
            var covered = fp.EmailIsFreeMail || fp.EmailDomainAgeMonths is not null;
            var detail = string.Join(" ", fp.Flags.Select(f => f.Message));
            outcomes.Add(new("Digital footprint", result, detail, worst, covered));
            narrative.Add($"Digital footprint: contact e-mail domain {fp.EmailDomain} – {detail}");
            if (fp.Flags.Any(f => f.Code == "EMAIL_DOMAIN_NEW")) next.Add("Contact e-mail domain is under six months old; corroborate tenure with a lease, utility bill or bank-account opening date.");
        }

        // Owner identity depth
        if (owners is null) outcomes.Add(new("Owner identity", "Not run", "Owner identity checks were not run.", RiskTier.Low, false));
        else if (!owners.Covered)
        {
            var sev = profile?.IsSmb == true ? RiskTier.Medium : RiskTier.Low;
            outcomes.Add(new("Owner identity", "No principal declared", "Owner identity, age and cross-application checks could not be performed.", sev, false));
            next.Add("Declare at least one beneficial owner or controlling person with date of birth, nationality and address.");
        }
        else
        {
            var worst = owners.Flags.Count == 0 ? RiskTier.Low : owners.Flags.Max(f => f.Severity);
            var people = string.Join("; ", owners.Owners.Select(o => $"{o.FullName}{(o.Role is null ? "" : $" ({o.Role})")}{(o.Age is { } a ? $", {a}" : "")}{(o.PriorApplications.Count > 0 ? $", {o.PriorApplications.Count} prior application(s)" : "")}"));
            var detail = $"{people}. Identity attributes {owners.CompletenessPercent:P0} complete{(owners.HomeBased ? "; trades from a principal's home address" : "")}." +
                         (owners.Flags.Count > 0 ? " " + string.Join(" ", owners.Flags.Select(f => f.Message)) : "");
            outcomes.Add(new("Owner identity", owners.Flags.Count == 0 ? "Complete" : $"{owners.Flags.Count} finding(s)", detail, worst, true));
            narrative.Add($"Owner identity: {owners.Owners.Count} principal(s), {owners.CompletenessPercent:P0} of identity attributes supplied.{(owners.Flags.Count > 0 ? " " + string.Join(" ", owners.Flags.Select(f => $"{f.Code}: {f.Message}")) : "")}");
            if (owners.Flags.Any(f => f.Code is "OWNER_DUPLICATE_APPLICATION" or "OWNER_APPLICATION_VELOCITY")) next.Add("Review the earlier applications this principal appeared on before deciding; confirm they are the same person and whether those merchants were approved.");
            if (owners.Flags.Any(f => f.Code == "OWNER_IDENTITY_INCOMPLETE")) next.Add("Collect the missing owner identity attributes (government ID with date of birth, proof of address).");
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
            var mention = s.Flags.Any(x => x.Code == "ADVERSE_MEDIA_MENTION");
            if (mediaNote.Count > 0)
                detail += mediaChecked
                    ? " " + MediaSummary(s)
                    : $" Adverse media NOT checked ({mediaNote.First(x => !x!.Succeeded)!.Error}) – media result is unknown, not clear.";
            var result = sanctions ? "SANCTIONS MATCH" : pep ? "PEP match" : media ? "Adverse media" : hits.Count > 0 ? "Possible match"
                : mention ? "Clear · media mentions" : mediaChecked ? "Clear" : "Lists clear · media unavailable";
            var severity = !mediaChecked && s.OverallRisk == RiskTier.Low ? RiskTier.Medium : s.OverallRisk;
            outcomes.Add(new("Sanctions / PEP / media", result, detail, severity, mediaChecked || hits.Count > 0));
            narrative.Add($"Screening: {(sanctions ? "a confirmed sanctions match was found – this is a hard stop." : hits.Count > 0 ? "possible matches need analyst disposition." : media ? "no sanctions or PEP list hits, but adverse media ties a screened name to risk terms – see the adverse-media evidence." : mediaChecked ? "no sanctions, PEP or adverse-media findings." : "no sanctions or PEP list hits, but adverse media could not be checked so the media dimension remains a coverage gap.")} {detail}");
            foreach (var line in MediaNarrative(s)) narrative.Add(line);
            adverseMedia.AddRange(MediaEvidence(s));
            if (!mediaChecked) next.Add("Re-run adverse-media screening (every news/records source failed or was rate-limited) before final approval.");
            if (media) next.Add("Review each adverse-media article: confirm the named party is this applicant/owner (not a namesake), then record the disposition.");
            if (mention && !media) next.Add("Adverse-media mentions are indirect (risk term not in the same sentence as the name); spot-check the cited articles.");
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

        // Bank statement as small-merchant evidence (requiredness, account holder, deposits vs declared, payouts)
        if (bankEvidence is { } be)
        {
            var worst = be.Flags.Count == 0 ? RiskTier.Low : be.Flags.Max(x => x.Severity);
            var detail = string.Join(" ", be.Flags.Select(x => x.Message));
            if (!be.Supplied)
            {
                if (be.Required)
                {
                    outcomes.Add(new("Bank evidence", "Required · missing", detail, worst, false));
                    narrative.Add($"Bank evidence: {detail}");
                    next.Add("Obtain the merchant's last three months of business bank statements; for a Micro / Small merchant they are the primary identity, volume and liquidity evidence.");
                }
            }
            else
            {
                var label = string.Join(" · ", new[]
                {
                    be.HolderNameScore is { } h ? $"holder {h:P0}" : "holder n/a",
                    be.InflowsToDeclaredRatio is { } r ? $"deposits {r:P0} of declared" : null,
                    be.Processors.Count > 0 ? $"{be.Processors.Count} processor(s)" : null
                }.Where(x => x is not null));
                outcomes.Add(new("Bank evidence", label, detail, worst, true));
                narrative.Add($"Bank evidence: {detail}");
                if (be.Flags.Any(x => x.Code == "BANK_HOLDER_MISMATCH")) next.Add("Bank account holder is neither the business nor an owner: obtain a voided cheque or bank letter for an account in the legal entity's name before boarding.");
                if (be.Flags.Any(x => x.Code == "BANK_HOLDER_UNDECLARED")) next.Add("Record the account-holder name from the statement header so the settlement account can be tied to the applicant.");
                if (be.Flags.Any(x => x.Code == "BANK_DEPOSITS_BELOW_DECLARED")) next.Add("Declared volume is not supported by deposits: ask the merchant to reconcile, or underwrite on the evidenced figure.");
            }
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
            explanation?.Contributions ?? [], rules?.MatchedRules ?? [], rules?.DecidingRule, coverageGaps, score?.HardStops ?? [], next.Distinct().ToList(), adverseMedia);
    }

    /// <summary>One-line media roll-up for the check outcome: counts, the sources that answered, and any that did not.</summary>
    internal static string MediaSummary(ScreeningReport s)
    {
        var results = s.Subjects.Select(x => x.AdverseMedia).Where(x => x is not null).Select(x => x!).ToList();
        var statuses = results.SelectMany(r => r.Providers ?? []).ToList();
        var ok = statuses.Where(p => p.Succeeded).Select(p => p.Provider).Distinct().ToList();
        var failed = statuses.Where(p => !p.Succeeded).Select(p => p.Provider).Distinct().ToList();
        var text = $"Adverse media: {results.Sum(r => r.NegativeCount)} negative, {results.Sum(r => r.MentionCount)} indirect mention(s) of {results.Sum(r => r.ArticleCount)} article(s)/record(s)";
        if (ok.Count > 0) text += $" from {string.Join(", ", ok)}";
        text += ".";
        if (failed.Count > 0) text += $" Source(s) unavailable: {string.Join(", ", failed)} – covered by the remaining sources.";
        return text;
    }

    /// <summary>Per-subject narrative lines naming the risk terms and quoting the sentence in which they co-occur with the name.</summary>
    internal static IEnumerable<string> MediaNarrative(ScreeningReport s)
    {
        foreach (var sub in s.Subjects)
        {
            var m = sub.AdverseMedia;
            if (m is null || !m.Succeeded) continue;
            var negatives = m.Articles.Where(a => a.Tone == "negative").ToList();
            if (negatives.Count == 0) continue;
            var terms = negatives.SelectMany(a => a.MatchedTerms ?? []).GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count()).Select(g => $"{g.Key} ×{g.Count()}").Take(8);
            var examples = negatives.Take(3).Select(a =>
                $"\"{(string.IsNullOrEmpty(a.Context) ? a.Title : a.Context)}\" ({a.Source}{(a.Published is { } d ? $", {d:yyyy-MM-dd}" : "")}, via {a.Provider ?? m.Provider})");
            yield return $"Adverse media – {sub.Subject.Name} ({sub.Subject.Role}): {negatives.Count} article(s) place the name in the same sentence as {string.Join(", ", terms)}. {string.Join(" ", examples)}";
        }
    }

    internal static IEnumerable<AdverseMediaEvidence> MediaEvidence(ScreeningReport s) =>
        s.Subjects.Where(x => x.AdverseMedia is { Succeeded: true })
            .SelectMany(x => x.AdverseMedia!.Articles
                .Where(a => a.Tone is "negative" or "mention")
                .Select(a => new AdverseMediaEvidence(x.Subject.Name, a.Title, a.Source, a.Provider ?? x.AdverseMedia.Provider, a.Published, a.Tone,
                    a.MatchedTerms ?? [], a.Category, a.Context, a.Url.ToString())))
            .OrderBy(e => e.Tone == "negative" ? 0 : 1).ThenByDescending(e => e.Published ?? DateTimeOffset.MinValue)
            .Take(30);

    internal static AssessmentIntakeSummary Summarise(AssessmentIntake i, UploadedDocument? bank, UploadedDocument? fin) => new(
        i.Business, i.Owners, i.BusinessDescription, i.MerchantCategoryCode, i.AnnualVolume, i.AverageTicket, i.HighestTicket, i.ExistingRelationship,
        i.DeliveryDays, i.CardNotPresentShare, i.OffersSubscriptions, i.OffersFreeTrials, i.EmployeeCount, i.YearsInBusiness, i.PriorYearRevenue,
        i.WebsiteProductCount, i.HasPhysicalLocation,
        bank?.FileName ?? (i.BankStatementCsv is null ? null : "inline CSV"),
        fin?.FileName ?? (i.FinancialStatementText is null ? null : "inline text"),
        i.ExternalRef, i.Actor, i.LocationCount, i.EntityType);
}
