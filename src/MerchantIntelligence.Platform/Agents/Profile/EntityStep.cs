using MerchantIntelligence.Platform.Profiling;
using MerchantIntelligence.Platform.Workflows;

namespace MerchantIntelligence.Platform.Agents.Profile;

public sealed class EntityStep(MerchantProfiler profiler) : IAssessmentStep
{
    public WorkflowStepDescriptor Descriptor { get; } = new("entity", "Entity type",
        "Normalises the declared legal form (sole proprietorship, LLC, corporation, non-profit…), infers it from the application when missing, and records inconsistencies between the form and the declared owners, headcount and volume.",
        [], [], true, [], Profiling: true);

    public async Task ExecuteAsync(AssessmentContext ctx) =>
        ctx.Entity = await ctx.RunAsync(Descriptor,
            () => Task.FromResult(profiler.ClassifyEntity(ctx.Intake)),
            e => $"{MerchantProfiler.Describe(e.EntityType)}{(e.Inferred ? " (inferred)" : "")} · registry scope {e.RegistryScope}{(e.Findings.Count > 0 ? $" · {e.Findings.Count} consistency finding(s)" : "")}");
}
