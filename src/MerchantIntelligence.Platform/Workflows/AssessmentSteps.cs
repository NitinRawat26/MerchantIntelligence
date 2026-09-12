using MerchantIntelligence.CreditDecision;
using MerchantIntelligence.Kyb;
using MerchantIntelligence.Kyb.Compliance;
using MerchantIntelligence.Kyb.Prohibited;
using MerchantIntelligence.Kyb.Registry;
using MerchantIntelligence.Kyb.Sanctions;
using MerchantIntelligence.MccValidation.Taxonomy;
using MerchantIntelligence.MccValidation.Validation;
using MerchantIntelligence.Platform.Assessment;
using MerchantIntelligence.Platform.Cases;
using MerchantIntelligence.Platform.Integrations;
using MerchantIntelligence.Platform.ModelOps;
using MerchantIntelligence.Platform.Rules;
using MerchantIntelligence.Platform.Scoring;
using MerchantIntelligence.Underwriting.Explainability;
using MerchantIntelligence.Underwriting.Financials;
using MerchantIntelligence.Underwriting.Plausibility;
using MerchantIntelligence.Underwriting.Pricing;

namespace MerchantIntelligence.Platform.Workflows;

public sealed class VerificationStep(BusinessVerificationService verification) : IAssessmentStep
{
    public WorkflowStepDescriptor Descriptor { get; } = new("verification", "Business identity verification",
        "Matches the legal entity against public registries (GLEIF, SEC EDGAR, Companies House…) and verifies the address.",
        [], [], false, []);

    public async Task ExecuteAsync(AssessmentContext ctx) =>
        ctx.Verification = await ctx.RunAsync(Descriptor, () => verification.VerifyAsync(ctx.Intake.Business, ctx.CancellationToken),
            v => $"{v.Status} ({v.ConfidencePercent:F0}% confidence){(v.BestMatch is null ? "" : $" · best match {v.BestMatch.Record.LegalName} via {v.BestMatch.Record.Source}")}");
}

public sealed class ScreeningStep(SanctionsScreeningService screening) : IAssessmentStep
{
    public WorkflowStepDescriptor Descriptor { get; } = new("screening", "Sanctions / PEP / adverse-media screening",
        "Screens the business, trading name and every beneficial owner against the loaded sanctions and PEP lists plus adverse media.",
        [], [], false,
        [
            new("includeTradingName", "boolean", "true", "Also screen the trading name when it differs from the legal name."),
            new("includeOwners", "boolean", "true", "Screen each declared beneficial owner / principal.")
        ]);

    public async Task ExecuteAsync(AssessmentContext ctx)
    {
        var intake = ctx.Intake;
        var subjects = new List<ScreeningSubject> { new(intake.Business.LegalName, null, intake.Business.Country, false, "Business") };
        if (ctx.Param(Descriptor.Id, "includeTradingName", true) && !string.IsNullOrWhiteSpace(intake.Business.TradingName) && intake.Business.TradingName != intake.Business.LegalName)
            subjects.Add(new ScreeningSubject(intake.Business.TradingName, null, intake.Business.Country, false, "Trading name"));
        if (ctx.Param(Descriptor.Id, "includeOwners", true))
            subjects.AddRange(intake.Owners.Select(o => new ScreeningSubject(o.FullName, o.DateOfBirth, o.Nationality, true, o.Role ?? "Beneficial owner")));
        ctx.Screening = await ctx.RunAsync(Descriptor, () => screening.ScreenAsync(subjects, ctx.CancellationToken),
            s => $"{s.Subjects.Count} subject(s) screened · {s.Subjects.Count(x => x.PotentialMatch)} potential match(es) · overall {s.OverallRisk}");
    }
}

public sealed class WebsiteStep(WebsiteComplianceScanner website) : IAssessmentStep
{
    public WorkflowStepDescriptor Descriptor { get; } = new("website", "Website compliance scan",
        "Crawls the merchant website for card-brand disclosures (refund policy, contact, pricing, privacy…) and domain age.",
        [], [], false, []);

    public async Task ExecuteAsync(AssessmentContext ctx)
    {
        if (ctx.SiteUrl is null) { await ctx.SkipAsync(Descriptor, "No website URL supplied."); return; }
        ctx.Website = await ctx.RunAsync(Descriptor,
            () => website.ScanAsync(ctx.SiteUrl, ctx.Intake.BusinessDescription, ctx.Intake.MerchantCategoryCode, ctx.Intake.Business.LegalName, ctx.CancellationToken),
            w => w.Reachable ? $"Grade {w.Grade} ({w.Score}/100) · {w.Checks.Count(c => c.Status == CheckStatus.Fail)} failed check(s) · {w.PagesAnalyzed.Count} page(s)" : "Website unreachable");
    }
}

public sealed class ProhibitedStep(ProhibitedBusinessDetector prohibited) : IAssessmentStep
{
    public WorkflowStepDescriptor Descriptor { get; } = new("prohibited", "Prohibited & restricted business check",
        "Classifies the business description (and website text when available) against the prohibited / restricted / high-risk category list.",
        ["website"], ["website"], false, []);

    public async Task ExecuteAsync(AssessmentContext ctx) =>
        ctx.Prohibited = await ctx.RunAsync(Descriptor,
            () => Task.FromResult(AssessmentComposer.CombineProhibited(prohibited.Analyze(null, ctx.Intake.BusinessDescription, ctx.Intake.MerchantCategoryCode), ctx.Website?.ProhibitedBusiness)),
            p => p.Matches.Count == 0 ? $"{p.Verdict} · no restricted category detected" : $"{p.Verdict} · {string.Join(", ", p.Matches.Take(3).Select(m => m.Category.Name))}");
}

public sealed class MccStep(MccValidationService mcc) : IAssessmentStep
{
    public WorkflowStepDescriptor Descriptor { get; } = new("mcc", "MCC validation",
        "Compares the declared MCC with what the website actually sells.",
        [], [], false, []);

    public async Task ExecuteAsync(AssessmentContext ctx)
    {
        if (ctx.SiteUrl is null) { await ctx.SkipAsync(Descriptor, "MCC validation needs a website to gather evidence from."); return; }
        ctx.Mcc = await ctx.RunAsync(Descriptor, () => mcc.ValidateAsync(ctx.Intake.MerchantCategoryCode, ctx.SiteUrl, ctx.CancellationToken),
            m => $"{m.Verdict} · declared {m.DeclaredMcc} {m.DeclaredDescription} · {m.AccuracyPercent:F0}% agreement");
    }
}

public sealed class MatchStep(IMatchProvider match) : IAssessmentStep
{
    public WorkflowStepDescriptor Descriptor { get; } = new("match", "MATCH / terminated-merchant inquiry",
        "Queries the configured MATCH / TMF provider for the business and its principals.",
        [], [], false, []);

    public async Task ExecuteAsync(AssessmentContext ctx)
    {
        var b = ctx.Intake.Business;
        ctx.Match = await ctx.RunAsync(Descriptor,
            () => match.InquireAsync(new MatchInquiry(b.LegalName, b.TradingName, b.TaxId, b.Country, b.AddressLine, b.City, b.Region, b.PostalCode,
                ctx.Intake.Owners.Select(AssessmentComposer.ToPrincipal).ToList()), ctx.CancellationToken),
            m => m.Availability == MatchAvailability.Available
                ? (m.Found == true ? $"FOUND · {m.Hits.Count} hit(s) via {m.Provider}" : $"No record via {m.Provider}")
                : $"{m.Availability} · {m.Message ?? "unknown, not clear"}");
    }
}

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

public sealed class CreditStep(ModelOpsService modelOps, DecisionExplainer explainer) : IAssessmentStep
{
    public WorkflowStepDescriptor Descriptor { get; } = new("credit", "Credit decision & explainability",
        "Scores the application with the champion model and explains the prediction feature by feature.",
        ["match"], ["match"], false, []);

    public async Task ExecuteAsync(AssessmentContext ctx)
    {
        var app = ctx.Application;
        ctx.CreditExplanation = await ctx.RunAsync(Descriptor, () =>
        {
            (ctx.Credit, ctx.DecisionLogId) = modelOps.PredictAndLog(app);
            return Task.FromResult(explainer.Explain(app));
        }, e => $"{e.Decision} ({e.Confidence:P0}) · top driver {e.Contributions.OrderByDescending(c => Math.Abs(c.Contribution)).First().Feature}");
    }
}

public sealed class TermsStep(ReservePricingRecommender pricing) : IAssessmentStep
{
    public WorkflowStepDescriptor Descriptor { get; } = new("terms", "Reserve & pricing recommendation",
        "Recommends reserve, settlement delay and pricing from the KYB risk roll-up, website, plausibility and delivery profile.",
        ["verification", "screening", "website", "plausibility", "credit"], ["verification", "screening", "website", "plausibility", "credit"], false, []);

    public async Task ExecuteAsync(AssessmentContext ctx)
    {
        var i = ctx.Intake;
        ctx.KybRisk = AssessmentComposer.KybRisk(ctx.Verification, ctx.Screening, ctx.Website);
        var app = ctx.Application;
        ctx.Terms = await ctx.RunAsync(Descriptor,
            () => Task.FromResult(pricing.Recommend(new PricingInput(app, i.DeliveryDays, i.CardNotPresentShare, ctx.KybRisk == RiskTier.High,
                ctx.Website?.Score, ctx.Plausibility?.PlausibilityScore, i.OffersSubscriptions, i.OffersFreeTrials))),
            t => $"Band {t.RiskBand} · {t.Reserve.Type} reserve {t.Reserve.RollingPercent:F1}% / {t.Reserve.RollingDays}d · settlement T+{t.Pricing.SettlementDelayDays}");
    }
}

public sealed class ScoreStep(UnifiedRiskScorer scorer, RulesEngine rules, RuleSetRepository ruleSets) : IAssessmentStep
{
    public WorkflowStepDescriptor Descriptor { get; } = new("score", "Unified risk score & policy rules",
        "Blends every upstream signal into the 0–1000 score, then evaluates the active policy rule set to reach Approve / Refer / Decline.",
        ["verification", "screening", "website", "prohibited", "mcc", "match", "bank", "financials", "plausibility", "credit", "terms"],
        ["verification", "screening", "website", "prohibited", "mcc", "match", "bank", "financials", "plausibility", "credit", "terms"], true, []);

    public async Task ExecuteAsync(AssessmentContext ctx)
    {
        var v = ctx.Verification;
        var s = ctx.Screening;
        ctx.KybRisk ??= AssessmentComposer.KybRisk(v, s, ctx.Website);
        ctx.Signals = AssessmentComposer.CollectSignals(v, s, ctx.Website, ctx.Prohibited, ctx.Mcc, ctx.Bank, ctx.Financials, ctx.Plausibility);
        var registriesReachable = v is not null && AssessmentComposer.RegistriesReachable(v);
        var listsLoaded = s is not null && AssessmentComposer.ListsLoaded(s);
        ctx.ScoreInput = new UnifiedRiskInput(ctx.Application, ctx.Credit, ctx.KybRisk,
            !registriesReachable ? null : v!.Status is VerificationStatus.Verified or VerificationStatus.PartialMatch,
            v?.EntityAgeMonths,
            !listsLoaded ? null : s!.Flags.Any(f => f.Code == "SANCTIONS_MATCH"),
            !listsLoaded ? null : s!.Flags.Any(f => f.Code == "PEP_MATCH"),
            !listsLoaded || !AssessmentComposer.MediaChecked(s!) ? null : s!.Flags.Any(f => f.Code == "ADVERSE_MEDIA"),
            ctx.Prohibited?.Verdict, ctx.Website?.Score, ctx.Plausibility?.PlausibilityScore, ctx.Terms?.RiskBand,
            ctx.Match?.Availability == MatchAvailability.Available ? ctx.Match.Found : null, ctx.Signals);
        ctx.Rules = await ctx.RunAsync(Descriptor, () =>
        {
            ctx.Score = scorer.Score(ctx.ScoreInput);
            var facts = RulesEngine.BuildFacts(ctx.Score, ctx.ScoreInput, new Dictionary<string, object?>
            {
                ["country"] = ctx.Intake.Business.Country,
                ["mccVerdict"] = ctx.Mcc?.Verdict.ToString(),
                ["nsfCount"] = ctx.Bank?.NsfOrOverdraftCount,
                ["ownersDeclared"] = ctx.Intake.Owners.Count
            });
            return Task.FromResult(rules.Evaluate(ruleSets.Active, facts));
        }, r => $"Score {ctx.Score!.Score}/1000 ({ctx.Score.Tier}) · rules → {r.Outcome} via {r.DecidingRule} · coverage {ctx.Score.CoveragePercent:F0}%");
    }
}

public sealed class CaseStep(CaseService cases) : IAssessmentStep
{
    public WorkflowStepDescriptor Descriptor { get; } = new("case", "Case creation & audit",
        "Opens a review case carrying the decision snapshot so analysts can work it in the queue.",
        ["score"], ["score"], false,
        [new("priority", "Low | Normal | High | Critical", "(from score)", "Force a case priority instead of deriving it from the risk tier.")]);

    public async Task ExecuteAsync(AssessmentContext ctx)
    {
        if (!ctx.Intake.CreateCase) { await ctx.SkipAsync(Descriptor, "Case creation disabled by caller."); return; }
        var decision = AssessmentComposer.BuildDecision(ctx.Score, ctx.Rules, ctx.Steps, ctx.ForcedRefer);
        var priority = ctx.Param<CasePriority?>(Descriptor.Id, "priority", null);
        ctx.Case = await ctx.RunAsync(Descriptor, () => Task.FromResult(cases.Create(ctx.Intake.Business.LegalName, ctx.Intake.Actor, ctx.Intake.ExternalRef ?? ctx.AssessmentId,
                priority, ctx.Score?.Score, ctx.Score?.Tier, ctx.Rules?.Outcome,
                new { assessmentId = ctx.AssessmentId, decision, coverageGaps = ctx.Score?.CoverageGaps, hardStops = ctx.Score?.HardStops, decisionLogId = ctx.DecisionLogId, workflow = ctx.Workflow.Version })),
            c => $"{c.Id} · {c.Status} · priority {c.Priority}");
    }
}
