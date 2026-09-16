using MerchantIntelligence.MccValidation.Taxonomy;
using MerchantIntelligence.Platform.Assessment;
using MerchantIntelligence.Platform.Scoring;

namespace MerchantIntelligence.Platform.Workflows;

/// <summary>A stop-gate that fired during a run: which step, why, and what it imposed.</summary>
public sealed record StopGateHit(string StepId, string Agent, string Reason, StopGateScope Scope, ForcedOutcome ForceOutcome);

/// <summary>Deterministic evaluation of <see cref="StopGateConfig"/> rules against a step's recorded result.</summary>
public static class StopGates
{
    private static readonly Dictionary<string, string> HardStopOf = new()
    {
        ["screening"] = "SANCTIONS_MATCH",
        ["prohibited"] = "PROHIBITED_BUSINESS",
        ["match"] = "MATCH_LISTED"
    };

    private static readonly HashSet<string> FlagSources = ["verification", "screening", "website", "prohibited", "mcc", "bank", "financials", "plausibility"];

    /// <summary>Steps whose result can establish a hard stop.</summary>
    public static bool CanHardStop(string stepId) => HardStopOf.ContainsKey(stepId);

    /// <summary>Steps whose result carries coded flags a gate can match on.</summary>
    public static bool RaisesFlags(string stepId) => FlagSources.Contains(stepId);

    public static string Describe(TransitionCondition when) => when switch
    {
        TransitionCondition.Success => "on success",
        TransitionCondition.Fail => "on fail",
        _ => "always"
    };

    public static string Describe(StopGateConfig gate) => gate.When switch
    {
        StopGateTrigger.HardStop => "a hard stop is established",
        StopGateTrigger.Failed => "the step fails",
        StopGateTrigger.HighSeverityFlag => "a High or VeryHigh flag is raised",
        StopGateTrigger.Flag => $"flag '{gate.Code}' is raised",
        _ => gate.When.ToString()
    };

    /// <summary>Returns the reason the gate fired for <paramref name="stepId"/>, or null when it did not.</summary>
    public static string? Evaluate(StopGateConfig gate, string stepId, AssessmentContext ctx)
    {
        switch (gate.When)
        {
            case StopGateTrigger.HardStop:
                return HardStopOf.TryGetValue(stepId, out var code) && ctx.HardStop == code ? $"hard stop {code}" : null;
            case StopGateTrigger.Failed:
                return ctx.Steps.Any(s => s.Id == stepId && s.Status == StepStatus.Failed) ? "step failed" : null;
            case StopGateTrigger.HighSeverityFlag:
            {
                var hit = SignalsOf(stepId, ctx).FirstOrDefault(s => s.Severity >= RiskTier.High);
                return hit is null ? null : $"{hit.Severity} flag {hit.Code}";
            }
            case StopGateTrigger.Flag:
            {
                var hit = SignalsOf(stepId, ctx).FirstOrDefault(s => string.Equals(s.Code, gate.Code, StringComparison.OrdinalIgnoreCase));
                return hit is null ? null : $"flag {hit.Code}";
            }
            default:
                return null;
        }
    }

    private static IEnumerable<RiskSignal> SignalsOf(string stepId, AssessmentContext ctx) =>
        AssessmentComposer.CollectSignals(ctx.Verification, ctx.Screening, ctx.Website, ctx.Prohibited, ctx.Mcc, ctx.Bank, ctx.Financials, ctx.Plausibility)
            .Where(s => s.Source == stepId);
}
