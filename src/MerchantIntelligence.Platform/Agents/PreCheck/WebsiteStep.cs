using MerchantIntelligence.Kyb.Compliance;
using MerchantIntelligence.Platform.Workflows;

namespace MerchantIntelligence.Platform.Agents.PreCheck;

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
