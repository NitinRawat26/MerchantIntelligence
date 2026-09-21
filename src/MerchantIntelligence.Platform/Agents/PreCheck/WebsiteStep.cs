using MerchantIntelligence.Kyb.Compliance;
using MerchantIntelligence.MccValidation.Taxonomy;
using MerchantIntelligence.Platform.Assessment;
using MerchantIntelligence.Platform.Profiling;
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
            async () => ApplyBrochureSiteRules(
                await website.ScanAsync(ctx.SiteUrl, ctx.Intake.BusinessDescription, ctx.Intake.MerchantCategoryCode, ctx.Intake.Business.LegalName, ctx.CancellationToken),
                ctx.Intake, ctx.Profile),
            w => w.Reachable ? $"Grade {w.Grade} ({w.Score}/100) · {w.Checks.Count(c => c.Status == CheckStatus.Fail)} failed check(s) · {w.PagesAnalyzed.Count} page(s)" : "Website unreachable");
    }

    /// <summary>Card-not-present share at or below which a physical merchant's site is treated as a brochure, not a sales channel.</summary>
    internal const double BrochureCnpShareCeiling = 0.10;

    private static readonly HashSet<string> SaleDisclosures = ["REFUND_POLICY", "DELIVERY_POLICY", "TERMS_CONDITIONS", "CURRENCY_DISCLOSURE", "CHECKOUT_PRESENT"];

    /// <summary>
    /// The Visa/Mastercard website disclosures (refund, delivery, terms of sale) govern e-commerce sales. A Micro/Small card-present
    /// merchant who declares (almost) no card-not-present volume sells little or nothing through its site, so a missing sale disclosure
    /// is recorded as Low when the site has no purchase flow, or Medium when it does have an ordering link (the analyst confirms whether
    /// those orders settle on this account) — either way it cannot on its own refer the application. Privacy, contact details, TLS,
    /// domain and prohibited-content checks keep their severity because they apply to any live site.
    /// </summary>
    internal static WebsiteComplianceResult ApplyBrochureSiteRules(WebsiteComplianceResult result, AssessmentIntake intake, MerchantProfile? profile)
    {
        if (!result.Reachable || profile is null) return result;
        var physical = intake.HasPhysicalLocation ?? profile.LocationCount > 0;
        var hasCheckout = result.Checks.Any(c => c.Code == "CHECKOUT_PRESENT" && c.Status == CheckStatus.Pass);
        if (!profile.IsSmb || !physical || intake.CardNotPresentShare > BrochureCnpShareCeiling) return result;

        var cap = hasCheckout ? RiskTier.Medium : RiskTier.Low;
        var note = hasCheckout
            ? $"Site of a {profile.Segment} card-present merchant declaring {intake.CardNotPresentShare:P0} card-not-present volume but exposing an ordering link: confirm whether online orders settle on this account; if they do, the disclosure must be published before boarding."
            : $"Brochure site of a {profile.Segment} card-present merchant ({intake.CardNotPresentShare:P0} card-not-present, no purchase flow): terms-of-sale disclosures apply only if the site starts taking orders.";

        var checks = result.Checks.Select(c =>
            SaleDisclosures.Contains(c.Code) && c.Status is CheckStatus.Fail or CheckStatus.Warn && c.Severity > cap
                ? c with { Severity = cap, Detail = $"{c.Detail} {note}" }
                : c).ToList();
        return result with { Checks = checks };
    }
}
