using MerchantIntelligence.MccValidation.Taxonomy;
using MerchantIntelligence.Platform.Assessment;
using MerchantIntelligence.Platform.Profiling;
using MerchantIntelligence.Platform.Workflows;

namespace MerchantIntelligence.Platform.Agents.Profile;

/// <summary>
/// Who is applying and what should we ask them? Runs alone before every other agent, classifies the legal form and
/// size segment, and scopes the rest of the run. It decides which questions are asked – never how risky the answer is.
/// </summary>
public sealed class ProfileAgent : IAssessmentAgent
{
    public WorkflowAgentDescriptor Descriptor { get; } = new("profile", "Profile agent",
        "Classify the applicant – legal form, size segment, locations – and decide which checks apply before any evidence is gathered.",
        "Runs the entity and segment steps from intake alone (no lookups), then publishes the merchant profile every other agent reads: registry scope for verification, steps that are not applicable to this kind of merchant, and the segment the scorer weights by. It always runs first and cannot be disabled.",
        ["entity", "segment"], AgentKind.Profiling);

    public Task<AgentReview> ReviewAsync(AssessmentContext ctx, IReadOnlyList<string> ownedSteps)
    {
        var findings = new List<AgentFinding>();
        var p = ctx.Profile;
        if (p is null)
            return Task.FromResult(new AgentReview("Profile could not be built; downstream steps run with the generic (enterprise) plan.", findings));

        foreach (var f in p.Findings)
            findings.Add(new(f.Severity >= RiskTier.Medium ? AgentFindingKind.Observation : AgentFindingKind.Advisory, f.Code, f.Message,
                f.Code.StartsWith("ENTITY_", StringComparison.Ordinal) ? "Declared legal form is inconsistent with the application; confirm with the merchant before boarding." : null));
        foreach (var n in p.NotApplicable)
            findings.Add(new(AgentFindingKind.Advisory, "NOT_APPLICABLE_" + n.StepId.ToUpperInvariant(), $"'{n.StepId}' skipped for this profile.", n.Reason));
        if (p.RegistryScope is RegistryScope.Local or RegistryScope.None)
            findings.Add(new(AgentFindingKind.Advisory, "REGISTRY_SCOPE_LOCAL", "LEI and SEC EDGAR lookups are not expected to know this merchant and are excluded from verification.",
                "Registry evidence comes from state / national company registers where configured; local presence and bank evidence carry identity."));
        if (p.LocationCount > 1)
            findings.Add(new(AgentFindingKind.Advisory, "MULTI_LOCATION", $"{p.LocationCount} locations declared.",
                "Presence and volume plausibility are assessed per location; boarding requires one MID per location."));

        var summary = $"{p.Segment} {MerchantProfiler.Describe(p.EntityType)}{(p.EntityTypeInferred ? " (inferred)" : "")} with {p.LocationCount} location(s); "
                      + (p.NotApplicable.Count == 0 ? "every check applies." : $"{p.NotApplicable.Count} check(s) not applicable to this profile.");
        return Task.FromResult(new AgentReview(summary, findings));
    }
}
