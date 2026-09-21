using MerchantIntelligence.Kyb.Registry;
using MerchantIntelligence.Platform.Assessment;
using MerchantIntelligence.Platform.Integrations;
using MerchantIntelligence.Platform.Rules;
using MerchantIntelligence.Platform.Scoring;
using MerchantIntelligence.Platform.Workflows;

namespace MerchantIntelligence.Platform.Agents.Decision;

public sealed class ScoreStep(UnifiedRiskScorer scorer, RulesEngine rules, RuleSetRepository ruleSets) : IAssessmentStep
{
    public WorkflowStepDescriptor Descriptor { get; } = new("score", "Unified risk score & policy rules",
        "Blends every upstream signal into the 0–1000 score, then evaluates the active policy rule set to reach Approve / Refer / Decline.",
        ["verification", "presence", "screening", "website", "prohibited", "mcc", "match", "bank", "financials", "plausibility", "credit", "terms"],
        ["verification", "presence", "screening", "website", "prohibited", "mcc", "match", "bank", "financials", "plausibility", "credit", "terms"], true, []);

    public async Task ExecuteAsync(AssessmentContext ctx)
    {
        var v = ctx.Verification;
        var s = ctx.Screening;
        ctx.KybRisk ??= AssessmentComposer.KybRisk(v, s, ctx.Website);
        ctx.Signals = AssessmentComposer.CollectSignals(v, s, ctx.Website, ctx.Prohibited, ctx.Mcc, ctx.Bank, ctx.Financials, ctx.Plausibility);
        var registriesReachable = v is not null && AssessmentComposer.RegistriesReachable(v);
        var listsLoaded = s is not null && AssessmentComposer.ListsLoaded(s);
        ctx.ScoreInput = new UnifiedRiskInput(ctx.Application, ctx.Credit, ctx.KybRisk,
            !registriesReachable ? null : v!.Status switch { VerificationStatus.Verified or VerificationStatus.PartialMatch => true, VerificationStatus.NotFound => false, _ => null },
            v?.EntityAgeMonths,
            !listsLoaded ? null : s!.Flags.Any(f => f.Code == "SANCTIONS_MATCH"),
            !listsLoaded ? null : s!.Flags.Any(f => f.Code == "PEP_MATCH"),
            !listsLoaded || !AssessmentComposer.MediaChecked(s!) ? null : s!.Flags.Any(f => f.Code == "ADVERSE_MEDIA"),
            ctx.Prohibited?.Verdict, ctx.Website?.Score, ctx.Plausibility?.PlausibilityScore, ctx.Terms?.RiskBand,
            ctx.Match?.Availability == MatchAvailability.Available ? ctx.Match.Found : null, ctx.Signals,
            ctx.Profile?.Segment,
            ctx.Profile?.NotApplicable.SelectMany(n => ScoreWeights.ComponentsOfStep(n.StepId)).ToHashSet());
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
