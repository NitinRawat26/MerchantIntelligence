using MerchantIntelligence.MccValidation.Taxonomy;
using MerchantIntelligence.Platform.Assessment;
using MerchantIntelligence.Platform.Workflows;
using MerchantIntelligence.Underwriting.Pricing;

namespace MerchantIntelligence.Platform.Agents.Decision;

public sealed class TermsStep(ReservePricingRecommender pricing) : IAssessmentStep
{
    public WorkflowStepDescriptor Descriptor { get; } = new("terms", "Reserve & pricing recommendation",
        "Recommends reserve, settlement delay and pricing from the KYB risk roll-up, website, plausibility and delivery profile.",
        ["verification", "presence", "screening", "website", "plausibility", "credit"], ["verification", "presence", "screening", "website", "plausibility", "credit"], false, []);

    public async Task ExecuteAsync(AssessmentContext ctx)
    {
        var i = ctx.Intake;
        ctx.KybRisk = AssessmentComposer.KybRisk(ctx.Verification, ctx.Screening, ctx.Website, ctx.Owners);
        var app = ctx.Application;
        ctx.Terms = await ctx.RunAsync(Descriptor,
            () => Task.FromResult(pricing.Recommend(new PricingInput(app, i.DeliveryDays, i.CardNotPresentShare, ctx.KybRisk == RiskTier.High,
                ctx.Website?.Score, ctx.Plausibility?.PlausibilityScore, i.OffersSubscriptions, i.OffersFreeTrials))),
            t => $"Band {t.RiskBand} · {t.Reserve.Type} reserve {t.Reserve.RollingPercent:F1}% / {t.Reserve.RollingDays}d · settlement T+{t.Pricing.SettlementDelayDays}");
    }
}
