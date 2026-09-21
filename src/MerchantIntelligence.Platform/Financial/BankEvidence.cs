using MerchantIntelligence.Kyb;
using MerchantIntelligence.Kyb.Matching;
using MerchantIntelligence.Kyb.Registry;
using MerchantIntelligence.MccValidation.Taxonomy;
using MerchantIntelligence.Platform.Assessment;
using MerchantIntelligence.Platform.Profiling;
using MerchantIntelligence.Underwriting.Financials;

namespace MerchantIntelligence.Platform.Financial;

/// <summary>
/// The bank statement read as identity and volume evidence for a small merchant — not just cash-flow health. Answers: was
/// it supplied when the profile says it must be, is the account the merchant's own, do the deposits support the declared
/// volume, is money arriving regularly, and is the merchant already taking cards somewhere else.
/// </summary>
public sealed record BankEvidenceAssessment(
    bool Required,
    bool Supplied,
    /// <summary>Declared account-holder name similarity to the legal / trading name; null when not declared.</summary>
    double? HolderNameScore,
    /// <summary>Annualised total inflows ÷ declared annual volume.</summary>
    decimal? InflowsToDeclaredRatio,
    /// <summary>Annualised card-processor deposits ÷ declared annual volume; null when no processor deposits were seen.</summary>
    decimal? CardDepositsToDeclaredRatio,
    int MonthsWithoutInflows,
    IReadOnlyList<string> Processors,
    IReadOnlyList<KybFlag> Flags,
    /// <summary>True when a statement was analysed. A required-but-missing statement is a coverage gap, never a clean result.</summary>
    bool Covered);

public static class BankEvidenceAssessor
{
    public const decimal InflowsLowRatio = 0.5m;
    public const decimal InflowsHighRatio = 3.0m;
    public const double HolderNameMatch = 0.85;

    public static BankEvidenceAssessment Assess(AssessmentIntake intake, MerchantProfile? profile, CashFlowAnalysis? bank, string? accountHolderName)
    {
        var required = profile?.IsSmb == true;
        var flags = new List<KybFlag>();
        if (bank is null)
        {
            if (required)
                flags.Add(new("BANK_STATEMENT_REQUIRED", $"A {profile!.Segment} merchant is underwritten primarily on its bank statement and none was analysed; identity, volume and cash-flow evidence are all missing.", RiskTier.Medium));
            return new BankEvidenceAssessment(required, false, null, null, null, 0, [], flags, false);
        }

        // Account holder
        double? holderScore = null;
        if (string.IsNullOrWhiteSpace(accountHolderName))
        {
            if (required) flags.Add(new("BANK_HOLDER_UNDECLARED", "Account-holder name was not recorded from the statement; the account cannot be tied to the applicant.", RiskTier.Low));
        }
        else
        {
            var b = intake.Business;
            var score = NameMatcher.Similarity(b.LegalName, accountHolderName);
            if (!string.IsNullOrWhiteSpace(b.TradingName)) score = Math.Max(score, NameMatcher.Similarity(b.TradingName, accountHolderName));
            var ownerScore = intake.Owners.Count == 0 ? 0 : intake.Owners.Max(o => NameMatcher.Similarity(o.FullName, accountHolderName));
            holderScore = Math.Round(Math.Max(score, ownerScore), 4);
            if (score >= HolderNameMatch)
                flags.Add(new("BANK_HOLDER_MATCH", $"Account holder '{accountHolderName}' matches the business name ({score:P0}).", RiskTier.Low));
            else if (ownerScore >= HolderNameMatch)
                flags.Add(new("BANK_HOLDER_IS_OWNER", $"Account holder '{accountHolderName}' is a declared owner, not the business ({ownerScore:P0}); personal account used for business receipts — common for sole proprietors, a settlement-account concern for an LLC.", intake.EntityType is EntityType.SoleProprietorship or null ? RiskTier.Low : RiskTier.Medium));
            else
                flags.Add(new("BANK_HOLDER_MISMATCH", $"Account holder '{accountHolderName}' matches neither the business ({score:P0}) nor any declared owner ({ownerScore:P0}); settlement would go to a third party.", RiskTier.High));
        }

        // Deposits vs declared volume
        decimal? inflowRatio = null, cardRatio = null;
        if (intake.AnnualVolume > 0 && bank.MonthsCovered > 0)
        {
            inflowRatio = Math.Round(bank.AverageMonthlyInflows * 12 / intake.AnnualVolume, 2);
            if (inflowRatio < InflowsLowRatio)
                flags.Add(new("BANK_DEPOSITS_BELOW_DECLARED", $"Annualised deposits ${bank.AverageMonthlyInflows * 12:N0} are {inflowRatio:P0} of the declared ${intake.AnnualVolume:N0} annual volume; the declared figure is not supported by the account.", required ? RiskTier.Medium : RiskTier.Low));
            else if (inflowRatio > InflowsHighRatio)
                flags.Add(new("BANK_DEPOSITS_FAR_ABOVE_DECLARED", $"Annualised deposits ${bank.AverageMonthlyInflows * 12:N0} are {inflowRatio:F1}× the declared ${intake.AnnualVolume:N0}; either volume is under-declared or the account carries unrelated funds.", RiskTier.Low));
            else
                flags.Add(new("BANK_DEPOSITS_SUPPORT_DECLARED", $"Annualised deposits ${bank.AverageMonthlyInflows * 12:N0} support the declared ${intake.AnnualVolume:N0} ({inflowRatio:P0}).", RiskTier.Low));

            if (bank.ImpliedAnnualCardVolume > 0)
            {
                cardRatio = Math.Round(bank.ImpliedAnnualCardVolume / intake.AnnualVolume, 2);
                flags.Add(new("BANK_EXISTING_CARD_PAYOUTS", $"Card-processor payouts already arrive from {string.Join(", ", bank.DetectedProcessors)}: ${bank.ImpliedAnnualCardVolume:N0}/yr, {cardRatio:P0} of declared volume. Existing acceptance history is a strong plausibility anchor; confirm whether this account will be switched or added to.", RiskTier.Low));
            }
            else if (required && intake.CardNotPresentShare < 1.0)
                flags.Add(new("BANK_NO_CARD_PAYOUTS", "No card-processor payouts in the statement: the merchant has no visible acceptance history, so declared volume rests on the application alone.", RiskTier.Low));
        }

        // Regularity: months with nothing coming in are a stronger SMB signal than volatility alone
        var dry = bank.Monthly.Count(m => m.Inflows <= 0);
        if (dry > 0)
            flags.Add(new("BANK_MONTHS_WITHOUT_DEPOSITS", $"{dry} of {bank.Monthly.Count} statement month(s) had no deposits at all; an operating merchant banks every month.", dry >= 2 ? RiskTier.Medium : RiskTier.Low));

        if (required && bank.NsfOrOverdraftCount > 0)
            flags.Add(new("BANK_NSF_SMB", $"{bank.NsfOrOverdraftCount} NSF / overdraft event(s) on the merchant's primary account; for a {profile!.Segment} merchant this is the main liquidity evidence and drives reserve sizing.", bank.NsfOrOverdraftCount >= 3 ? RiskTier.High : RiskTier.Medium));

        if (required && bank.MonthsCovered < 3)
            flags.Add(new("BANK_HISTORY_SHORT_SMB", $"Only {bank.MonthsCovered} month(s) of statements; request at least three for a {profile!.Segment} merchant.", RiskTier.Low));

        return new BankEvidenceAssessment(required, true, holderScore, inflowRatio, cardRatio, dry, bank.DetectedProcessors, flags, true);
    }
}
