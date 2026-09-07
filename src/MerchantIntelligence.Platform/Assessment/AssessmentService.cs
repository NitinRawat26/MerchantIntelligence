using System.Diagnostics;
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

/// <summary>
/// Runs every check in the suite against a single intake, in a fixed order, tolerating individual
/// failures (a failed check becomes a coverage gap rather than aborting the assessment), then folds the
/// results into the unified score, the policy rules and a human-readable explanation. Results are
/// persisted so the report can be re-opened and exported later.
/// </summary>
public sealed class AssessmentService
{
    private readonly BusinessVerificationService _verification;
    private readonly SanctionsScreeningService _screening;
    private readonly WebsiteComplianceScanner _website;
    private readonly ProhibitedBusinessDetector _prohibited;
    private readonly MccValidationService _mcc;
    private readonly IMatchProvider _match;
    private readonly VolumePlausibilityAnalyzer _plausibility;
    private readonly DecisionExplainer _explainer;
    private readonly ReservePricingRecommender _pricing;
    private readonly ModelOpsService _modelOps;
    private readonly UnifiedRiskScorer _scorer;
    private readonly RulesEngine _rules;
    private readonly RuleSetRepository _ruleSets;
    private readonly CaseService _cases;
    private readonly AuditTrail _audit;
    private readonly PlatformDatabase _db;
    private readonly ILogger<AssessmentService> _logger;

    public AssessmentService(BusinessVerificationService verification, SanctionsScreeningService screening, WebsiteComplianceScanner website,
        ProhibitedBusinessDetector prohibited, MccValidationService mcc, IMatchProvider match, VolumePlausibilityAnalyzer plausibility,
        DecisionExplainer explainer, ReservePricingRecommender pricing, ModelOpsService modelOps, UnifiedRiskScorer scorer, RulesEngine rules,
        RuleSetRepository ruleSets, CaseService cases, AuditTrail audit, PlatformDatabase db, ILogger<AssessmentService> logger)
    {
        _verification = verification;
        _screening = screening;
        _website = website;
        _prohibited = prohibited;
        _mcc = mcc;
        _match = match;
        _plausibility = plausibility;
        _explainer = explainer;
        _pricing = pricing;
        _modelOps = modelOps;
        _scorer = scorer;
        _rules = rules;
        _ruleSets = ruleSets;
        _cases = cases;
        _audit = audit;
        _db = db;
        _logger = logger;
        EnsureSchema();
    }

    public static readonly IReadOnlyList<(string Id, string Name)> StepCatalog =
    [
        ("verification", "Business identity verification"),
        ("screening", "Sanctions / PEP / adverse-media screening"),
        ("website", "Website compliance scan"),
        ("prohibited", "Prohibited & restricted business check"),
        ("mcc", "MCC validation"),
        ("match", "MATCH / terminated-merchant inquiry"),
        ("bank", "Bank statement cash-flow analysis"),
        ("financials", "P&L / balance sheet analysis"),
        ("plausibility", "Declared volume plausibility"),
        ("credit", "Credit decision & explainability"),
        ("terms", "Reserve & pricing recommendation"),
        ("score", "Unified risk score & policy rules"),
        ("case", "Case creation & audit")
    ];

    public async Task<AssessmentResult> RunAsync(AssessmentIntake intake, UploadedDocument? bankStatement, UploadedDocument? financialStatement,
        Func<AssessmentStep, Task>? progress, CancellationToken ct = default)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var id = $"ASMT-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}";
        var steps = new List<AssessmentStep>();
        var report = progress ?? (_ => Task.CompletedTask);

        var siteUrl = ParseUrl(intake.Business.WebsiteUrl);
        var hasBankInput = bankStatement is not null || !string.IsNullOrWhiteSpace(intake.BankStatementCsv);
        var hasFinancialInput = financialStatement is not null || !string.IsNullOrWhiteSpace(intake.FinancialStatementText);

        // 1. Business verification
        var verification = await Step("verification", steps, report,
            () => _verification.VerifyAsync(intake.Business, ct),
            v => $"{v.Status} ({v.ConfidencePercent:F0}% confidence){(v.BestMatch is null ? "" : $" · best match {v.BestMatch.Record.LegalName} via {v.BestMatch.Record.Source}")}");

        // 2. Screening
        var subjects = new List<ScreeningSubject> { new(intake.Business.LegalName, null, intake.Business.Country, false, "Business") };
        if (!string.IsNullOrWhiteSpace(intake.Business.TradingName) && intake.Business.TradingName != intake.Business.LegalName)
            subjects.Add(new ScreeningSubject(intake.Business.TradingName, null, intake.Business.Country, false, "Trading name"));
        subjects.AddRange(intake.Owners.Select(o => new ScreeningSubject(o.FullName, o.DateOfBirth, o.Nationality, true, o.Role ?? "Beneficial owner")));
        var screening = await Step("screening", steps, report,
            () => _screening.ScreenAsync(subjects, ct),
            s => $"{s.Subjects.Count} subject(s) screened · {s.Subjects.Count(x => x.PotentialMatch)} potential match(es) · overall {s.OverallRisk}");

        // 3. Website compliance
        WebsiteComplianceResult? website = null;
        if (siteUrl is null)
            await Skip("website", steps, report, "No website URL supplied.");
        else
            website = await Step("website", steps, report,
                () => _website.ScanAsync(siteUrl, intake.BusinessDescription, intake.MerchantCategoryCode, intake.Business.LegalName, ct),
                w => w.Reachable ? $"Grade {w.Grade} ({w.Score}/100) · {w.Checks.Count(c => c.Status == CheckStatus.Fail)} failed check(s) · {w.PagesAnalyzed.Count} page(s)" : "Website unreachable");

        // 4. Prohibited business (description + website text; worst verdict wins)
        var prohibited = await Step("prohibited", steps, report,
            () => Task.FromResult(CombineProhibited(_prohibited.Analyze(null, intake.BusinessDescription, intake.MerchantCategoryCode), website?.ProhibitedBusiness)),
            p => p.Matches.Count == 0 ? $"{p.Verdict} · no restricted category detected" : $"{p.Verdict} · {string.Join(", ", p.Matches.Take(3).Select(m => m.Category.Name))}");

        // 5. MCC validation
        MccValidationResult? mcc = null;
        if (siteUrl is null)
            await Skip("mcc", steps, report, "MCC validation needs a website to gather evidence from.");
        else
            mcc = await Step("mcc", steps, report,
                () => _mcc.ValidateAsync(intake.MerchantCategoryCode, siteUrl, ct),
                m => $"{m.Verdict} · declared {m.DeclaredMcc} {m.DeclaredDescription} · {m.AccuracyPercent:F0}% agreement");

        // 6. MATCH
        var match = await Step("match", steps, report,
            () => _match.InquireAsync(new MatchInquiry(intake.Business.LegalName, intake.Business.TradingName, intake.Business.TaxId, intake.Business.Country,
                intake.Business.AddressLine, intake.Business.City, intake.Business.Region, intake.Business.PostalCode,
                intake.Owners.Select(ToPrincipal).ToList()), ct),
            m => m.Availability == MatchAvailability.Available
                ? (m.Found == true ? $"FOUND · {m.Hits.Count} hit(s) via {m.Provider}" : $"No record via {m.Provider}")
                : $"{m.Availability} · {m.Message ?? "unknown, not clear"}");

        // 7. Bank statement
        CashFlowAnalysis? bank = null;
        if (!hasBankInput)
            await Skip("bank", steps, report, "No bank statement supplied.");
        else
            bank = await Step("bank", steps, report, () =>
            {
                var parsed = bankStatement is not null
                    ? BankStatementParser.Parse(new MemoryStream(bankStatement.Content), bankStatement.FileName)
                    : BankStatementParser.ParseCsv(intake.BankStatementCsv!);
                return Task.FromResult(CashFlowAnalyzer.Analyze(parsed));
            }, b => $"{b.MonthsCovered} month(s) · implied annual card volume ${b.ImpliedAnnualCardVolume:N0} · {b.NsfOrOverdraftCount} NSF/overdraft · {b.Flags.Count} flag(s)");

        // 8. Financial statement
        FinancialStatementAnalysis? financials = null;
        if (!hasFinancialInput)
            await Skip("financials", steps, report, "No P&L / balance sheet supplied.");
        else
            financials = await Step("financials", steps, report, () => Task.FromResult(financialStatement is not null
                    ? ProfitAndLossAnalyzer.Analyze(new MemoryStream(financialStatement.Content), financialStatement.FileName, intake.AnnualVolume)
                    : ProfitAndLossAnalyzer.AnalyzeText(intake.FinancialStatementText!, intake.AnnualVolume)),
                f => $"Revenue {(f.Statement.Revenue is { } r ? "$" + r.ToString("N0") : "n/a")} · net income {(f.Statement.NetIncome is { } n ? "$" + n.ToString("N0") : "n/a")} · {f.Flags.Count} flag(s)");

        // 9. Volume plausibility
        var plausibility = await Step("plausibility", steps, report,
            () => Task.FromResult(_plausibility.Analyze(new VolumeDeclaration(intake.AnnualVolume, intake.AverageTicket, intake.HighestTicket, intake.MerchantCategoryCode,
                intake.EmployeeCount, intake.YearsInBusiness, intake.PriorYearRevenue ?? financials?.Statement.Revenue,
                bank is null ? null : bank.AverageMonthlyCardDeposits, intake.WebsiteProductCount, intake.HasPhysicalLocation))),
            p => $"{p.PlausibilityScore}/100 · {p.Verdict}");

        // 10. Credit decision + explainability
        var app = new MerchantApplication
        {
            MerchantCategoryCode = intake.MerchantCategoryCode, AnnualVolume = (float)intake.AnnualVolume, AverageTicket = (float)intake.AverageTicket,
            HighestTicket = (float)intake.HighestTicket, MatchFound = match?.Found == true, ExistingRelationship = intake.ExistingRelationship
        };
        DecisionResult? credit = null;
        long? logId = null;
        var explanation = await Step("credit", steps, report, () =>
        {
            (credit, logId) = _modelOps.PredictAndLog(app);
            return Task.FromResult(_explainer.Explain(app));
        }, e => $"{e.Decision} ({e.Confidence:P0}) · top driver {e.Contributions.OrderByDescending(c => Math.Abs(c.Contribution)).First().Feature}");

        // 11. Terms
        var kybRisk = KybRisk(verification, screening, website);
        var terms = await Step("terms", steps, report,
            () => Task.FromResult(_pricing.Recommend(new PricingInput(app, intake.DeliveryDays, intake.CardNotPresentShare, kybRisk == RiskTier.High,
                website?.Score, plausibility?.PlausibilityScore, intake.OffersSubscriptions, intake.OffersFreeTrials))),
            t => $"Band {t.RiskBand} · {t.Reserve.Type} reserve {t.Reserve.RollingPercent:F1}% / {t.Reserve.RollingDays}d · settlement T+{t.Pricing.SettlementDelayDays}");

        // 12. Unified score + rules
        var signals = CollectSignals(verification, screening, website, prohibited, mcc, bank, financials, plausibility);
        var registriesReachable = verification is not null && RegistriesReachable(verification);
        var listsLoaded = screening is not null && ListsLoaded(screening);
        var input = new UnifiedRiskInput(app, credit, kybRisk,
            !registriesReachable ? null : verification!.Status is VerificationStatus.Verified or VerificationStatus.PartialMatch,
            verification?.EntityAgeMonths,
            !listsLoaded ? null : screening!.Flags.Any(f => f.Code == "SANCTIONS_MATCH"),
            !listsLoaded ? null : screening!.Flags.Any(f => f.Code == "PEP_MATCH"),
            !listsLoaded ? null : screening!.Flags.Any(f => f.Code == "ADVERSE_MEDIA"),
            prohibited?.Verdict, website?.Score, plausibility?.PlausibilityScore, terms?.RiskBand,
            match?.Availability == MatchAvailability.Available ? match.Found : null, signals);
        UnifiedRiskScore? score = null;
        var rules = await Step("score", steps, report, () =>
        {
            score = _scorer.Score(input);
            var facts = RulesEngine.BuildFacts(score, input, new Dictionary<string, object?>
            {
                ["country"] = intake.Business.Country,
                ["mccVerdict"] = mcc?.Verdict.ToString(),
                ["nsfCount"] = bank?.NsfOrOverdraftCount,
                ["ownersDeclared"] = intake.Owners.Count
            });
            return Task.FromResult(_rules.Evaluate(_ruleSets.Active, facts));
        }, r => $"Score {score!.Score}/1000 ({score.Tier}) · rules → {r.Outcome} via {r.DecidingRule} · coverage {score.CoveragePercent:F0}%");

        var decision = BuildDecision(score, rules, steps);
        var explainability = BuildExplainability(intake, decision, verification, screening, website, prohibited, mcc, match, bank, financials,
            plausibility, credit, explanation, terms, score, rules, signals);

        // 13. Case
        MerchantCase? merchantCase = null;
        if (!intake.CreateCase)
            await Skip("case", steps, report, "Case creation disabled by caller.");
        else
            merchantCase = await Step("case", steps, report, () => Task.FromResult(_cases.Create(intake.Business.LegalName, intake.Actor, intake.ExternalRef ?? id,
                    null, score?.Score, score?.Tier, rules?.Outcome, new { assessmentId = id, decision, coverageGaps = score?.CoverageGaps, hardStops = score?.HardStops, decisionLogId = logId })),
                c => $"{c.Id} · {c.Status} · priority {c.Priority}");

        var result = new AssessmentResult(id, startedAt, DateTimeOffset.UtcNow, Summarise(intake, bankStatement, financialStatement), steps, decision, explainability,
            verification, screening, website, prohibited, mcc, match, bank, financials, plausibility, credit, explanation, terms, score, rules, merchantCase, logId);
        Persist(result);
        _audit.Record(merchantCase?.Id, intake.Actor, "assessment.completed", new { assessmentId = id, decision.Outcome, decision.Score, decision.CoveragePercent });
        return result;
    }

    // ---- persistence ----

    public AssessmentResult? Get(string id)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT json FROM assessments WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        var json = cmd.ExecuteScalar() as string;
        return json is null ? null : JsonSerializer.Deserialize<AssessmentResult>(json, RulesEngine.JsonOptions);
    }

    public IReadOnlyList<AssessmentListItem> List(int limit = 50)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, merchant_name, outcome, score, tier, coverage, case_id, completed_at FROM assessments ORDER BY completed_at DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<AssessmentListItem>();
        while (r.Read())
            list.Add(new AssessmentListItem(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetString(4), r.GetDouble(5),
                r.IsDBNull(6) ? null : r.GetString(6), DateTimeOffset.Parse(r.GetString(7))));
        return list;
    }

    private void Persist(AssessmentResult result)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO assessments (id, merchant_name, outcome, score, tier, coverage, case_id, completed_at, json)
            VALUES ($id, $name, $outcome, $score, $tier, $coverage, $case, $completed, $json)
            """;
        cmd.Parameters.AddWithValue("$id", result.Id);
        cmd.Parameters.AddWithValue("$name", result.Intake.Business.LegalName);
        cmd.Parameters.AddWithValue("$outcome", result.Decision.Outcome);
        cmd.Parameters.AddWithValue("$score", result.Decision.Score);
        cmd.Parameters.AddWithValue("$tier", result.Decision.Tier);
        cmd.Parameters.AddWithValue("$coverage", result.Decision.CoveragePercent);
        cmd.Parameters.AddWithValue("$case", (object?)result.Case?.Id ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$completed", result.CompletedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(result, RulesEngine.JsonOptions));
        cmd.ExecuteNonQuery();
    }

    private void EnsureSchema()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS assessments (
                id TEXT PRIMARY KEY,
                merchant_name TEXT NOT NULL,
                outcome TEXT NOT NULL,
                score INTEGER NOT NULL,
                tier TEXT NOT NULL,
                coverage REAL NOT NULL,
                case_id TEXT,
                completed_at TEXT NOT NULL,
                json TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    // ---- step plumbing ----

    private async Task<T?> Step<T>(string id, List<AssessmentStep> steps, Func<AssessmentStep, Task> report, Func<Task<T>> run, Func<T, string> summarise) where T : class
    {
        var name = StepCatalog.First(s => s.Id == id).Name;
        await report(new AssessmentStep(id, name, StepStatus.Running, "Running…", 0));
        var sw = Stopwatch.StartNew();
        try
        {
            var value = await run();
            var step = new AssessmentStep(id, name, StepStatus.Succeeded, summarise(value), sw.ElapsedMilliseconds);
            steps.Add(step);
            await report(step);
            return value;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Assessment step {Step} failed", id);
            var step = new AssessmentStep(id, name, StepStatus.Failed, "Check failed; treated as not run (coverage gap).", sw.ElapsedMilliseconds, ex.Message);
            steps.Add(step);
            await report(step);
            return null;
        }
    }

    private static async Task Skip(string id, List<AssessmentStep> steps, Func<AssessmentStep, Task> report, string reason)
    {
        var step = new AssessmentStep(id, StepCatalog.First(s => s.Id == id).Name, StepStatus.Skipped, reason, 0);
        steps.Add(step);
        await report(step);
    }

    // ---- derivations ----

    private static Uri? ParseUrl(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        raw = raw.Trim();
        if (!raw.Contains("://", StringComparison.Ordinal)) raw = "https://" + raw;
        return Uri.TryCreate(raw, UriKind.Absolute, out var url) && url.Host.Contains('.') ? url : null;
    }

    private static MatchPrincipal ToPrincipal(BeneficialOwner o)
    {
        var parts = o.FullName.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        return new MatchPrincipal(parts[0], parts.Length > 1 ? parts[1] : string.Empty, o.DateOfBirth, null);
    }

    private static ProhibitedBusinessResult CombineProhibited(ProhibitedBusinessResult fromDescription, ProhibitedBusinessResult? fromWebsite)
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
    private static bool RegistriesReachable(BusinessVerificationResult v) => v.Sources.Count == 0 || v.Sources.Any(x => x.Succeeded);

    /// <summary>Screening against zero loaded lists is not a clear result; treat it as not run.</summary>
    private static bool ListsLoaded(ScreeningReport s) => s.Lists.Any(l => l.Error is null && l.EntityCount > 0);

    private static RiskTier? KybRisk(BusinessVerificationResult? v, ScreeningReport? s, WebsiteComplianceResult? w)
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

    private static List<RiskSignal> CollectSignals(BusinessVerificationResult? v, ScreeningReport? s, WebsiteComplianceResult? w, ProhibitedBusinessResult? p,
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

    private static AssessmentDecision BuildDecision(UnifiedRiskScore? score, RulesEvaluation? rules, IReadOnlyList<AssessmentStep> steps)
    {
        if (score is null || rules is null)
            return new AssessmentDecision("Refer", 0, "Unknown", 0, "n/a", "The unified score or rules engine failed; refer to a senior analyst for manual review.");

        var failed = steps.Count(s => s.Status == StepStatus.Failed);
        var summary = rules.Outcome switch
        {
            RuleOutcome.Decline => $"Decline. Policy rule {rules.DecidingRule} fired on a score of {score.Score}/1000 ({score.Tier}).",
            RuleOutcome.Approve => $"Approve. Score {score.Score}/1000 ({score.Tier}) with {score.CoveragePercent:F0}% check coverage and no blocking findings; rule {rules.DecidingRule} applies.",
            _ => $"Refer for manual review. Score {score.Score}/1000 ({score.Tier}); rule {rules.DecidingRule} requires an analyst decision."
        };
        if (failed > 0) summary += $" {failed} check(s) could not be completed and are counted as coverage gaps.";
        return new AssessmentDecision(rules.Outcome.ToString(), score.Score, score.Tier, score.CoveragePercent, rules.RuleSetVersion, summary);
    }

    private static AssessmentExplainability BuildExplainability(AssessmentIntake intake, AssessmentDecision decision, BusinessVerificationResult? v, ScreeningReport? s,
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
            if (mediaNote.Count > 0)
                detail += mediaNote.All(x => x!.Succeeded)
                    ? $" Adverse media: {mediaNote.Sum(x => x!.NegativeCount)} negative of {mediaNote.Sum(x => x!.ArticleCount)} article(s)."
                    : $" Adverse media lookup incomplete ({mediaNote.First(x => !x!.Succeeded)!.Error}).";
            outcomes.Add(new("Sanctions / PEP / media", sanctions ? "SANCTIONS MATCH" : pep ? "PEP match" : media ? "Adverse media" : hits.Count > 0 ? "Possible match" : "Clear", detail, s.OverallRisk, true));
            narrative.Add($"Screening: {(sanctions ? "a confirmed sanctions match was found – this is a hard stop." : hits.Count > 0 ? "possible matches need analyst disposition." : "no sanctions, PEP or adverse-media findings.")} {detail}");
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

        var headline = $"{decision.Outcome.ToUpperInvariant()} — {intake.Business.LegalName}: score {decision.Score}/1000 ({decision.Tier}), {decision.CoveragePercent:F0}% of checks covered, " +
                       $"{findings.Count(x => x.Severity == RiskTier.High)} high / {findings.Count(x => x.Severity == RiskTier.Medium)} medium finding(s).";

        var coverageGaps = outcomes.Where(o => !o.Covered).Select(o => $"{o.Check}: {o.Result} – {o.Detail}")
            .Concat(score?.CoverageGaps.Select(g => $"Score component not covered: {g}") ?? []).ToList();
        return new AssessmentExplainability(headline, narrative, outcomes, findings, score?.Components ?? [], score?.ReasonCodes ?? [],
            explanation?.Contributions ?? [], rules?.MatchedRules ?? [], rules?.DecidingRule, coverageGaps, score?.HardStops ?? [], next.Distinct().ToList());
    }

    private static AssessmentIntakeSummary Summarise(AssessmentIntake i, UploadedDocument? bank, UploadedDocument? fin) => new(
        i.Business, i.Owners, i.BusinessDescription, i.MerchantCategoryCode, i.AnnualVolume, i.AverageTicket, i.HighestTicket, i.ExistingRelationship,
        i.DeliveryDays, i.CardNotPresentShare, i.OffersSubscriptions, i.OffersFreeTrials, i.EmployeeCount, i.YearsInBusiness, i.PriorYearRevenue,
        i.WebsiteProductCount, i.HasPhysicalLocation,
        bank?.FileName ?? (i.BankStatementCsv is null ? null : "inline CSV"),
        fin?.FileName ?? (i.FinancialStatementText is null ? null : "inline text"),
        i.ExternalRef, i.Actor);
}
