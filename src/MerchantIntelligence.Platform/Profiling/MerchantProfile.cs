using MerchantIntelligence.MccValidation.Taxonomy;

namespace MerchantIntelligence.Platform.Profiling;

/// <summary>Legal form of the applicant as declared at intake. Drives which registries can know the entity and how large it can plausibly be.</summary>
public enum EntityType
{
    Unknown,
    SoleProprietorship,
    SingleMemberLlc,
    MultiMemberLlc,
    Partnership,
    SCorporation,
    CCorporation,
    PublicCorporation,
    NonProfit,
    Government,
    Trust,
    Other
}

/// <summary>Size band of the merchant. Decides which evidence is asked for, never how risky the merchant is.</summary>
public enum MerchantSegment
{
    Micro,
    Small,
    Mid,
    Enterprise
}

/// <summary>Which family of registries can be expected to hold a record for the entity.</summary>
public enum RegistryScope
{
    /// <summary>State / national company registers (Secretary of State, OpenCorporates, Companies House).</summary>
    Local,
    /// <summary>Global identifiers and securities filings on top of local registers (GLEIF LEI, SEC EDGAR).</summary>
    Global,
    /// <summary>Tax-exempt registers (IRS TEOS, charity commissions).</summary>
    TaxExempt,
    /// <summary>No company register applies (sole proprietors trading under their own name, public bodies).</summary>
    None
}

/// <summary>A step the profile marks as not applicable, with the reason recorded in the audit trail.</summary>
public sealed record StepApplicability(string StepId, string Reason);

/// <summary>An inconsistency between the declared legal form and the rest of the application.</summary>
public sealed record ProfileFinding(string Code, string Message, RiskTier Severity);

/// <summary>
/// The merchant profile produced by the Profile agent before any evidence is gathered: legal form, size segment and the
/// consequences for the rest of the run (registry scope, steps that do not apply). Advisory to scope only – it never
/// lowers a score or clears a finding.
/// </summary>
public sealed record MerchantProfile(
    EntityType EntityType,
    bool EntityTypeInferred,
    MerchantSegment Segment,
    RegistryScope RegistryScope,
    int LocationCount,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<StepApplicability> NotApplicable,
    IReadOnlyList<ProfileFinding> Findings)
{
    public bool IsSmb => Segment is MerchantSegment.Micro or MerchantSegment.Small;

    public string? NotApplicableReason(string stepId) => NotApplicable.FirstOrDefault(n => n.StepId == stepId)?.Reason;
}
