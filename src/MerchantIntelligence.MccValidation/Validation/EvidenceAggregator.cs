using MerchantIntelligence.MccValidation.Taxonomy;

namespace MerchantIntelligence.MccValidation.Validation;

/// <summary>
/// Combines independent provider votes into a verdict and an accuracy score for the declared MCC.
/// </summary>
public sealed class EvidenceAggregator
{
    private readonly MccCatalog _catalog;

    public EvidenceAggregator(MccCatalog catalog) => _catalog = catalog;

    public MccValidationResult Aggregate(
        int declaredMcc,
        Uri websiteUrl,
        IReadOnlyList<(IMccEvidenceProvider Provider, ProviderEvidence Evidence)> results,
        IReadOnlyList<Uri> pagesAnalyzed)
    {
        var declared = _catalog.Find(declaredMcc);
        var declaredDescription = declared?.Description ?? $"MCC {declaredMcc} (not in catalog)";
        var declaredTier = declared?.RiskTier ?? RiskTier.Medium;

        var informative = results.Where(r => r.Evidence.Succeeded && r.Evidence.Candidates.Count > 0).ToList();
        var totalWeight = informative.Sum(r => r.Provider.Weight);

        var combined = new Dictionary<int, double>();
        foreach (var (provider, evidence) in informative)
        {
            var max = evidence.Candidates.Max(c => c.Score);
            foreach (var c in evidence.Candidates)
            {
                // Normalise each provider to [0,1] relative to its own top candidate so no single
                // provider's scale dominates, then weight.
                combined[c.Mcc] = combined.GetValueOrDefault(c.Mcc) + provider.Weight * (c.Score / max);
            }
        }

        var suggested = combined
            .Select(kv => new MccCandidate(kv.Key, _catalog.Describe(kv.Key), totalWeight > 0 ? kv.Value / totalWeight : 0))
            .OrderByDescending(c => c.Score)
            .Take(5)
            .ToList();

        var declaredSupport = suggested.FirstOrDefault(c => c.Mcc == declaredMcc)?.Score ?? 0;
        var sameCategorySupport = declared is null ? 0 : suggested
            .Where(c => _catalog.Find(c.Mcc)?.Category == declared.Category)
            .Sum(c => c.Score);

        var accuracy = Math.Clamp(0.7 * declaredSupport + 0.3 * sameCategorySupport, 0, 1);

        MccVerdict verdict;
        if (informative.Count == 0) verdict = MccVerdict.Insufficient;
        else if (accuracy >= 0.55) verdict = MccVerdict.Consistent;
        else if (accuracy >= 0.25 || sameCategorySupport >= 0.5) verdict = MccVerdict.Questionable;
        else verdict = MccVerdict.Inconsistent;

        var flags = BuildRiskFlags(declared, declaredMcc, verdict, suggested, results);

        return new MccValidationResult(
            declaredMcc, declaredDescription, declaredTier, websiteUrl, verdict,
            Math.Round(accuracy * 100, 1), suggested, flags,
            results.Select(r => r.Evidence).ToList(), pagesAnalyzed);
    }

    private List<RiskFlag> BuildRiskFlags(
        MccEntry? declared, int declaredMcc, MccVerdict verdict,
        IReadOnlyList<MccCandidate> suggested,
        IReadOnlyList<(IMccEvidenceProvider Provider, ProviderEvidence Evidence)> results)
    {
        var flags = new List<RiskFlag>();

        if (declared is null)
            flags.Add(new RiskFlag("UNKNOWN_MCC", $"MCC {declaredMcc} is not in the catalog.", RiskTier.Medium));
        else if (declared.RiskTier == RiskTier.High)
            flags.Add(new RiskFlag("HIGH_RISK_MCC", $"Declared MCC {declaredMcc} ({declared.Description}) is a high-risk category.", RiskTier.High));

        var top = suggested.FirstOrDefault();
        if (top is not null && top.Mcc != declaredMcc)
        {
            var topEntry = _catalog.Find(top.Mcc);
            if (topEntry?.RiskTier == RiskTier.High && declared?.RiskTier != RiskTier.High)
                flags.Add(new RiskFlag("HIDDEN_HIGH_RISK",
                    $"Evidence suggests high-risk MCC {top.Mcc} ({top.Description}) while a lower-risk MCC was declared.", RiskTier.High));
            else if (verdict == MccVerdict.Inconsistent)
                flags.Add(new RiskFlag("MCC_MISMATCH",
                    $"Evidence points to MCC {top.Mcc} ({top.Description}) rather than {declaredMcc}.", RiskTier.Medium));
        }

        var failed = results.Where(r => !r.Evidence.Succeeded).Select(r => r.Provider.Name).ToList();
        if (failed.Count > 0)
            flags.Add(new RiskFlag("PROVIDER_UNAVAILABLE", $"Evidence unavailable from: {string.Join(", ", failed)}.", RiskTier.Low));

        if (verdict == MccVerdict.Insufficient)
            flags.Add(new RiskFlag("INSUFFICIENT_EVIDENCE", "The website could not be analysed; manual review required.", RiskTier.Medium));

        return flags;
    }
}
