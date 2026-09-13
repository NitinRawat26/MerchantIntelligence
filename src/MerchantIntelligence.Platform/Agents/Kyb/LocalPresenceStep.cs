using MerchantIntelligence.Kyb.Registry;
using MerchantIntelligence.Platform.Workflows;

namespace MerchantIntelligence.Platform.Agents.Kyb;

public sealed class LocalPresenceStep(LocalPresenceService presence) : IAssessmentStep
{
    public WorkflowStepDescriptor Descriptor { get; } = new("presence", "Local business presence",
        "Looks for a business with the declared name at or near the declared address in OpenStreetMap (always) and Foursquare / Google Places (when keys are configured). Trading evidence for small merchants that no legal registry knows; it complements, never replaces, registry verification.",
        ["verification"], ["verification"], false, []);

    public async Task ExecuteAsync(AssessmentContext ctx)
    {
        var b = ctx.Intake.Business;
        if (string.IsNullOrWhiteSpace(b.AddressLine) && string.IsNullOrWhiteSpace(b.City) && string.IsNullOrWhiteSpace(b.PostalCode))
        { await ctx.SkipAsync(Descriptor, "No address or locality supplied to search around."); return; }
        if (!presence.IsEnabled) { await ctx.SkipAsync(Descriptor, "Local presence disabled (Kyb:LocalPresenceEnabled=false)."); return; }

        var verification = ctx.Verification ?? new BusinessVerificationResult(b, VerificationStatus.Inconclusive, 0, null, null, null, [], []);
        ctx.LocalPresence = await ctx.RunAsync(Descriptor, () => presence.CheckAsync(verification, ctx.CancellationToken),
            lp => lp.BestMatch is { } pm
                ? $"{lp.Status} ({lp.ConfidencePercent:F0}%) · '{pm.Record.Name}' via {pm.Record.Source}{(pm.DistanceMeters is { } d ? $" · {d:F0} m away" : "")}"
                : $"{lp.Status} · {lp.Note ?? $"no matching business in {string.Join(", ", lp.Sources.Where(s => s.Succeeded).Select(s => s.Source))}"}");
        if (ctx.LocalPresence is { } result && ctx.Verification is { } v)
            ctx.Verification = BusinessVerificationService.WithLocalPresence(v, result);
    }
}
