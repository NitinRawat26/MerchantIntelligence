using MerchantIntelligence.Kyb.Registry;
using MerchantIntelligence.Kyb.Sanctions;
using MerchantIntelligence.MccValidation.Taxonomy;
using MerchantIntelligence.Platform.Assessment;
using MerchantIntelligence.Platform.Integrations;
using MerchantIntelligence.Platform.Workflows;

namespace MerchantIntelligence.Platform.Agents.Kyb;

/// <summary>Is this business who it says it is, and is anyone behind it sanctioned, a PEP or terminated?</summary>
public sealed class KybAgent(SanctionsScreeningService screening) : IAssessmentAgent
{
    public WorkflowAgentDescriptor Descriptor { get; } = new("kyb", "KYB & screening agent",
        "Establish identity: registries, sanctions / PEP / adverse media and MATCH for the business and its people.",
        "Runs registry verification, the local-presence (places) check, screening and the MATCH inquiry. When the registry returns a legal name that differs from the application it re-screens that alias on its own initiative and folds the result into the screening report.",
        ["verification", "screening", "match", "presence", "owners", "licensing"]);

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
        {
            if (v.LocalPresence is { Status: LocalPresenceStatus.Confirmed, BestMatch: { } pm })
                findings.Add(new(AgentFindingKind.Observation, "LOCAL_PRESENCE_ONLY", $"No registry record for \"{b.LegalName}\", but {pm.Record.Source} lists \"{pm.Record.Name}\"{(pm.DistanceMeters is { } d ? $" {d:F0} m from the declared address" : "")}; identity is {v.Status} on trading evidence alone.", "Ask for a certificate of formation / state registration to confirm the legal entity."));
            else
                findings.Add(new(AgentFindingKind.Observation, "NOT_IN_REGISTRIES", $"No registry record found for \"{b.LegalName}\" ({v.Status}){(v.LocalPresence is { Status: LocalPresenceStatus.NotFound } ? " and no matching business near the declared address" : "")}.", "Ask for a certificate of incorporation."));
        }

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
