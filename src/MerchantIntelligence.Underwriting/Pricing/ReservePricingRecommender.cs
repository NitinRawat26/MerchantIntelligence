using MerchantIntelligence.CreditDecision;
using MerchantIntelligence.MccValidation.Taxonomy;
using MerchantIntelligence.Underwriting.Benchmarks;

namespace MerchantIntelligence.Underwriting.Pricing;

public sealed record PricingInput(
    MerchantApplication Application,
    // Days between the customer paying and receiving the goods/service; null = use industry default.
    int? DeliveryDays = null,
    // Card-present share of volume (0..1); card-not-present is riskier.
    double CardNotPresentShare = 1.0,
    // Optional external signals that harden terms.
    bool? KybHighRisk = null,
    int? WebsiteComplianceScore = null,
    int? VolumePlausibilityScore = null,
    bool OffersSubscriptions = false,
    bool OffersFreeTrials = false);

public sealed record ReserveRecommendation(
    string Type,                       // None | Rolling | Capped | Upfront
    double RollingPercent,             // % of each settlement withheld
    int RollingDays,                   // days each withheld amount is held
    decimal CapAmount,                 // upper bound on total reserve (0 = uncapped)
    decimal UpfrontAmount,
    decimal EstimatedSteadyStateBalance);

public sealed record PricingRecommendation(
    double InterchangePlusMarkupBps,
    decimal PerTransactionFee,
    decimal MonthlyFee,
    decimal ChargebackFee,
    int SettlementDelayDays,
    decimal MonthlyVolumeCap,
    decimal SingleTransactionCap);

public sealed record PricingFactor(string Code, string Description, string Effect);

public sealed record TermsRecommendation(
    string RiskBand,                   // A (lowest) .. E (highest)
    double RiskScore,                  // 0..1 composite
    ReserveRecommendation Reserve,
    PricingRecommendation Pricing,
    decimal EstimatedExposure,         // acquirer credit exposure over the delivery + chargeback window
    IReadOnlyList<PricingFactor> Factors,
    string BenchmarkSource);

/// <summary>
/// Turns the credit model output plus deal attributes into concrete commercial terms: rolling /
/// capped reserve, settlement delay, interchange-plus pricing and volume caps. Exposure is modelled as
/// (daily volume x (delivery days + 120-day chargeback window x expected chargeback rate)).
/// </summary>
public sealed class ReservePricingRecommender
{
    private const int ChargebackWindowDays = 120;

    private readonly IDecisionPredictor _predictor;
    private readonly IndustryBenchmarks _benchmarks;
    private readonly MccCatalog _catalog;

    public ReservePricingRecommender(IDecisionPredictor predictor, IndustryBenchmarks benchmarks, MccCatalog catalog)
    {
        _predictor = predictor;
        _benchmarks = benchmarks;
        _catalog = catalog;
    }

    public TermsRecommendation Recommend(PricingInput input)
    {
        var app = input.Application;
        var mcc = (int)app.MerchantCategoryCode;
        var bm = _benchmarks.Resolve(mcc);
        var factors = new List<PricingFactor>();

        var prediction = _predictor.Predict(app);
        var pDecline = prediction.Probabilities.GetValueOrDefault(Decision.Declined);
        var pCancel = prediction.Probabilities.GetValueOrDefault(Decision.Cancelled);
        var risk = pDecline + 0.5 * pCancel;
        factors.Add(new PricingFactor("MODEL_RISK", $"Credit model: {prediction.Decision} ({prediction.Confidence:P0}); P(decline)={pDecline:P0}, P(cancel)={pCancel:P0}.", $"+{risk:F2}"));

        var tier = _catalog.Find(mcc)?.RiskTier ?? RiskTier.Medium;
        var tierAdj = tier switch { RiskTier.High => 0.15, RiskTier.Medium => 0.05, _ => 0.0 };
        if (tierAdj > 0) factors.Add(new PricingFactor("MCC_RISK_TIER", $"MCC {mcc} ({_catalog.Describe(mcc)}) is {tier} risk.", $"+{tierAdj:F2}"));
        risk += tierAdj;

        var deliveryDays = input.DeliveryDays ?? bm.DeliveryDays;
        if (deliveryDays >= 30)
        {
            var adj = Math.Min(0.2, deliveryDays / 300.0);
            factors.Add(new PricingFactor("FUTURE_DELIVERY", $"{deliveryDays}-day average fulfilment window creates pre-delivery exposure.", $"+{adj:F2}"));
            risk += adj;
        }

        if (input.CardNotPresentShare > 0.5)
        {
            var adj = 0.05 * input.CardNotPresentShare;
            factors.Add(new PricingFactor("CARD_NOT_PRESENT", $"{input.CardNotPresentShare:P0} of volume is card-not-present.", $"+{adj:F2}"));
            risk += adj;
        }

        if (input.OffersFreeTrials || input.OffersSubscriptions)
        {
            var adj = input.OffersFreeTrials ? 0.1 : 0.05;
            factors.Add(new PricingFactor("RECURRING_BILLING", input.OffersFreeTrials ? "Free-trial-to-subscription model has elevated 'unrecognised charge' disputes." : "Subscription billing raises cancellation disputes.", $"+{adj:F2}"));
            risk += adj;
        }

        if (input.KybHighRisk == true)
        {
            factors.Add(new PricingFactor("KYB_HIGH_RISK", "KYB report returned high risk (sanctions, prohibited content or registry mismatch).", "+0.20"));
            risk += 0.2;
        }
        if (input.WebsiteComplianceScore is int wc && wc < 60)
        {
            var adj = (60 - wc) / 400.0;
            factors.Add(new PricingFactor("WEBSITE_NON_COMPLIANT", $"Website compliance score {wc}/100 (missing policies increase disputes).", $"+{adj:F2}"));
            risk += adj;
        }
        if (input.VolumePlausibilityScore is int vp && vp < 60)
        {
            var adj = (60 - vp) / 400.0;
            factors.Add(new PricingFactor("VOLUME_IMPLAUSIBLE", $"Volume plausibility {vp}/100; declared volume is not supported by evidence.", $"+{adj:F2}"));
            risk += adj;
        }

        if (app.ExistingRelationship)
        {
            factors.Add(new PricingFactor("EXISTING_RELATIONSHIP", "Existing relationship in good standing.", "-0.10"));
            risk -= 0.1;
        }

        risk = Math.Clamp(risk, 0, 1);
        var band = risk switch { < 0.15 => "A", < 0.3 => "B", < 0.5 => "C", < 0.7 => "D", _ => "E" };

        // Exposure: unfulfilled sales at any time + expected chargebacks over the window.
        var dailyVolume = (decimal)app.AnnualVolume / 365m;
        var exposure = dailyVolume * deliveryDays
                       + dailyVolume * ChargebackWindowDays * (decimal)bm.ChargebackRate * RiskMultiplier(band)
                       + (decimal)app.HighestTicket;
        exposure = Math.Round(exposure, 0);

        var reserve = BuildReserve(band, dailyVolume, exposure, app.MatchFound);
        var pricing = BuildPricing(band, app, tier);

        return new TermsRecommendation(band, Math.Round(risk, 3), reserve, pricing, exposure, factors, bm.Source);
    }

    private static decimal RiskMultiplier(string band) => band switch { "A" => 1m, "B" => 1.5m, "C" => 2.5m, "D" => 4m, _ => 6m };

    private static ReserveRecommendation BuildReserve(string band, decimal dailyVolume, decimal exposure, bool matchFound)
    {
        var (pct, days) = band switch
        {
            "A" => (0.0, 0),
            "B" => (5.0, 90),
            "C" => (10.0, 180),
            "D" => (15.0, 180),
            _ => (20.0, 180)
        };
        if (matchFound) { pct = Math.Max(pct, 20); days = Math.Max(days, 180); }

        var steady = Math.Round(dailyVolume * (decimal)(pct / 100.0) * days, 0);
        // Cap the rolling reserve at the modelled exposure so low-risk merchants aren't over-collateralised.
        var cap = pct > 0 ? Math.Max(exposure, steady * 0.5m) : 0m;
        var type = pct == 0 ? "None" : cap < steady ? "Capped" : "Rolling";
        var upfront = band == "E" || matchFound ? Math.Round(Math.Min(exposure * 0.25m, dailyVolume * 30), 0) : 0m;
        if (upfront > 0) type = pct > 0 ? "Upfront+" + type : "Upfront";

        return new ReserveRecommendation(type, pct, days, Math.Round(cap, 0), upfront, Math.Min(steady, cap > 0 ? cap : steady));
    }

    private static PricingRecommendation BuildPricing(string band, MerchantApplication app, RiskTier tier)
    {
        var markupBps = band switch { "A" => 20, "B" => 35, "C" => 60, "D" => 95, _ => 150 };
        if (tier == RiskTier.High) markupBps += 25;
        // Very small merchants don't cover fixed servicing cost on bps alone.
        if (app.AnnualVolume < 100_000) markupBps += 15;
        else if (app.AnnualVolume > 10_000_000) markupBps = Math.Max(10, markupBps - 10);

        var perTx = app.AverageTicket < 15 ? 0.05m : band is "A" or "B" ? 0.10m : 0.20m;
        var monthly = app.AnnualVolume < 250_000 ? 15m : 0m;
        var chargebackFee = band is "A" or "B" ? 15m : band == "C" ? 20m : 25m;
        var settlementDelay = band switch { "A" => 1, "B" => 2, "C" => 2, "D" => 3, _ => 5 };

        var monthlyCap = Math.Round((decimal)app.AnnualVolume / 12m * (band switch { "A" => 2.0m, "B" => 1.5m, "C" => 1.25m, "D" => 1.1m, _ => 1.0m }), 0);
        var singleTxCap = Math.Round((decimal)app.HighestTicket * (band is "A" or "B" ? 1.5m : 1.1m), 0);
        return new PricingRecommendation(markupBps, perTx, monthly, chargebackFee, settlementDelay, monthlyCap, singleTxCap);
    }
}
