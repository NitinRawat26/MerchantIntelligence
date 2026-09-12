using MerchantIntelligence.CreditDecision;
using MerchantIntelligence.Kyb;
using MerchantIntelligence.Kyb.Compliance;
using MerchantIntelligence.Kyb.Prohibited;
using MerchantIntelligence.Kyb.Registry;
using MerchantIntelligence.Kyb.Sanctions;
using MerchantIntelligence.MccValidation.Taxonomy;
using MerchantIntelligence.MccValidation.Validation;
using MerchantIntelligence.Platform.Cases;
using MerchantIntelligence.Platform.Integrations;
using MerchantIntelligence.Platform.Rules;
using MerchantIntelligence.Platform.Scoring;
using MerchantIntelligence.Underwriting.Explainability;
using MerchantIntelligence.Underwriting.Financials;
using MerchantIntelligence.Underwriting.Plausibility;
using MerchantIntelligence.Underwriting.Pricing;

namespace MerchantIntelligence.Platform.Assessment;

/// <summary>Everything an analyst collects at intake; every check in the suite draws from this one record.</summary>
public sealed record AssessmentIntake(
    BusinessIdentity Business,
    IReadOnlyList<BeneficialOwner> Owners,
    string? BusinessDescription,
    int MerchantCategoryCode,
    decimal AnnualVolume,
    decimal AverageTicket,
    decimal HighestTicket,
    bool ExistingRelationship,
    int? DeliveryDays = null,
    double CardNotPresentShare = 1.0,
    bool OffersSubscriptions = false,
    bool OffersFreeTrials = false,
    int? EmployeeCount = null,
    decimal? YearsInBusiness = null,
    decimal? PriorYearRevenue = null,
    int? WebsiteProductCount = null,
    bool? HasPhysicalLocation = null,
    string? BankStatementCsv = null,
    string? FinancialStatementText = null,
    string? ExternalRef = null,
    string Actor = "analyst",
    bool CreateCase = true,
    int? LocationCount = null);

public sealed record UploadedDocument(string FileName, byte[] Content);

public enum StepStatus { Pending, Running, Succeeded, Failed, Skipped }

public sealed record AssessmentStep(
    string Id,
    string Name,
    StepStatus Status,
    string Summary,
    long DurationMs,
    string? Error = null);

/// <summary>One line of the explainability narrative, anchored to the check that produced it.</summary>
public sealed record ExplanationItem(string Section, string Code, string Message, RiskTier Severity, string Source);

public sealed record CheckOutcome(string Check, string Result, string Detail, RiskTier Severity, bool Covered);

public sealed record AssessmentExplainability(
    string Headline,
    IReadOnlyList<string> Narrative,
    IReadOnlyList<CheckOutcome> CheckOutcomes,
    IReadOnlyList<ExplanationItem> Findings,
    IReadOnlyList<ScoreComponent> ScoreComponents,
    IReadOnlyList<UnifiedReasonCode> ReasonCodes,
    IReadOnlyList<FeatureContribution> CreditContributions,
    IReadOnlyList<RuleHit> MatchedRules,
    string? DecidingRule,
    IReadOnlyList<string> CoverageGaps,
    IReadOnlyList<string> HardStops,
    IReadOnlyList<string> AnalystNextSteps);

public sealed record AssessmentDecision(
    string Outcome,                  // Approve | Refer | Decline
    int Score,
    string Tier,
    double CoveragePercent,
    string RuleSetVersion,
    string Summary);

public sealed record AssessmentResult(
    string Id,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    AssessmentIntakeSummary Intake,
    IReadOnlyList<AssessmentStep> Steps,
    AssessmentDecision Decision,
    AssessmentExplainability Explainability,
    BusinessVerificationResult? Verification,
    ScreeningReport? Screening,
    WebsiteComplianceResult? WebsiteCompliance,
    ProhibitedBusinessResult? ProhibitedBusiness,
    MccValidationResult? MccValidation,
    MatchResult? Match,
    CashFlowAnalysis? BankStatement,
    FinancialStatementAnalysis? FinancialStatement,
    VolumePlausibilityResult? VolumePlausibility,
    DecisionResult? CreditDecision,
    DecisionExplanation? CreditExplanation,
    TermsRecommendation? Terms,
    UnifiedRiskScore? UnifiedScore,
    RulesEvaluation? Rules,
    MerchantCase? Case,
    long? DecisionLogId,
    AssessmentWorkflowInfo? Workflow = null);

/// <summary>Which workflow definition produced a result, for replay and audit.</summary>
public sealed record AssessmentWorkflowInfo(string Name, string Version, IReadOnlyList<string> EnabledSteps);

public sealed record AssessmentStepDescriptor(string Id, string Name, bool Enabled = true);

/// <summary>Intake echoed back without the bulky statement payloads.</summary>
public sealed record AssessmentIntakeSummary(
    BusinessIdentity Business,
    IReadOnlyList<BeneficialOwner> Owners,
    string? BusinessDescription,
    int MerchantCategoryCode,
    decimal AnnualVolume,
    decimal AverageTicket,
    decimal HighestTicket,
    bool ExistingRelationship,
    int? DeliveryDays,
    double CardNotPresentShare,
    bool OffersSubscriptions,
    bool OffersFreeTrials,
    int? EmployeeCount,
    decimal? YearsInBusiness,
    decimal? PriorYearRevenue,
    int? WebsiteProductCount,
    bool? HasPhysicalLocation,
    string? BankStatementSource,
    string? FinancialStatementSource,
    string? ExternalRef,
    string Actor,
    int? LocationCount = null);

public sealed record AssessmentListItem(
    string Id,
    string MerchantName,
    string Outcome,
    int Score,
    string Tier,
    double CoveragePercent,
    string? CaseId,
    DateTimeOffset CompletedAt);
