using MerchantIntelligence.Platform.Profiling;
using MerchantIntelligence.Platform.Workflows;

namespace MerchantIntelligence.Platform.Agents.Profile;

public sealed class SegmentStep(MerchantProfiler profiler) : IAssessmentStep
{
    public WorkflowStepDescriptor Descriptor { get; } = new("segment", "Size segment & plan",
        "Places the merchant in a Micro / Small / Mid / Enterprise segment from volume, headcount, locations and entity type, then decides which registries to ask and which downstream steps do not apply.",
        ["entity"], ["entity"], true, [], Profiling: true);

    public async Task ExecuteAsync(AssessmentContext ctx)
    {
        var entity = ctx.Entity ?? profiler.ClassifyEntity(ctx.Intake);
        ctx.Profile = await ctx.RunAsync(Descriptor,
            () => Task.FromResult(profiler.Profile(ctx.Intake, entity, ctx.SiteUrl is not null, ctx.HasBankInput, ctx.HasFinancialInput)),
            p => $"{p.Segment} · {MerchantProfiler.Describe(p.EntityType)} · {p.LocationCount} location(s) · registries {p.RegistryScope}"
                 + (p.NotApplicable.Count > 0 ? $" · not applicable: {string.Join(", ", p.NotApplicable.Select(n => n.StepId))}" : ""));
    }
}
