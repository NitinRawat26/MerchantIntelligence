using MerchantIntelligence.Platform.Assessment;
using MerchantIntelligence.Platform.Workflows;

namespace MerchantIntelligence.Platform.Agents.Decision;

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
        var decision = AssessmentComposer.BuildDecision(ctx.Score, ctx.Rules, ctx.Steps, ctx.ForcedRefer, ctx.ForcedDecline);

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
