using System.Text.RegularExpressions;
using MerchantIntelligence.Kyb.Matching;
using MerchantIntelligence.MccValidation.Taxonomy;
using Microsoft.Extensions.Logging;

namespace MerchantIntelligence.Kyb.Registry;

/// <summary>
/// Fans out to every enabled registry, scores the candidates against the declared identity
/// and derives shell-company / mismatch flags.
/// </summary>
public sealed class BusinessVerificationService
{
    private static readonly Regex VirtualOfficeHint = new(
        @"\b(p\.?\s?o\.?\s?box|pmb|suite\s*#?\s*\d{3,}|registered agent|virtual office|mail ?box|c/o)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> InactiveStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "INACTIVE", "DISSOLVED", "dissolved", "liquidation", "closed", "REVOKED", "struck off", "removed", "DORMANT", "RETIRED", "LAPSED"
    };

    private readonly IReadOnlyList<IBusinessRegistryProvider> _providers;
    private readonly IReadOnlyList<IAddressGeocoder> _geocoders;
    private readonly KybOptions _options;
    private readonly ILogger<BusinessVerificationService> _logger;

    public BusinessVerificationService(
        IEnumerable<IBusinessRegistryProvider> providers,
        IEnumerable<IAddressGeocoder> geocoders,
        KybOptions options,
        ILogger<BusinessVerificationService> logger)
    {
        _providers = providers.ToList();
        _geocoders = geocoders.ToList();
        _options = options;
        _logger = logger;
    }

    public async Task<BusinessVerificationResult> VerifyAsync(BusinessIdentity identity, CancellationToken ct = default)
    {
        var sourceTasks = _providers.Where(p => p.IsEnabled).Select(async p =>
        {
            try
            {
                var records = await p.SearchAsync(identity, ct);
                var matches = records.Select(r => Score(identity, r))
                    .OrderByDescending(m => m.OverallScore)
                    .ToList();
                return new RegistrySourceResult(p.Name, true, matches);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Registry provider {Provider} failed", p.Name);
                return new RegistrySourceResult(p.Name, false, Array.Empty<RegistryMatch>(), ex.Message);
            }
        }).ToList();

        var disabled = _providers.Where(p => !p.IsEnabled)
            .Select(p => new RegistrySourceResult(p.Name, false, Array.Empty<RegistryMatch>(), "Not configured (API key missing)."));

        var addressTask = VerifyAddressAsync(identity, ct);
        var sources = (await Task.WhenAll(sourceTasks)).Concat(disabled).ToList();
        var address = await addressTask;

        var best = sources.SelectMany(s => s.Matches).OrderByDescending(m => m.OverallScore).FirstOrDefault();
        var flags = new List<KybFlag>();
        var status = Classify(best, sources.Any(s => s.Succeeded));

        int? ageMonths = null;
        if (best?.Record.IncorporationDate is DateOnly inc)
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            ageMonths = Math.Max(0, (today.Year - inc.Year) * 12 + today.Month - inc.Month);
            if (ageMonths < _options.NewEntityThresholdMonths)
                flags.Add(new KybFlag("NEW_ENTITY", $"Entity incorporated {ageMonths} month(s) ago ({inc:yyyy-MM-dd}); elevated shell-company / bust-out risk.", RiskTier.High));
        }

        if (best is not null)
        {
            if (best.NameScore < 0.85)
                flags.Add(new KybFlag("NAME_MISMATCH", $"Closest registry name '{best.Record.LegalName}' differs from declared '{identity.LegalName}' (similarity {best.NameScore:P0}).", RiskTier.Medium));
            if (!string.IsNullOrWhiteSpace(identity.AddressLine) && best.Record.Address is not null && best.AddressScore < 0.5)
                flags.Add(new KybFlag("REGISTERED_ADDRESS_MISMATCH", $"Declared address does not match registry address '{best.Record.Address}'.", RiskTier.Medium));
            if (best.Record.Status is not null && InactiveStatuses.Any(s => best.Record.Status.Contains(s, StringComparison.OrdinalIgnoreCase)))
                flags.Add(new KybFlag("INACTIVE_ENTITY", $"Registry status is '{best.Record.Status}'.", RiskTier.High));
            if (!string.IsNullOrWhiteSpace(identity.RegistrationNumber) && !string.IsNullOrWhiteSpace(best.Record.RegistrationNumber)
                && NameMatcher.Normalize(identity.RegistrationNumber).Replace(" ", "") != NameMatcher.Normalize(best.Record.RegistrationNumber).Replace(" ", ""))
                flags.Add(new KybFlag("REGISTRATION_NUMBER_MISMATCH", $"Declared registration number '{identity.RegistrationNumber}' ≠ registry '{best.Record.RegistrationNumber}'.", RiskTier.High));
        }
        else if (sources.Any(s => s.Succeeded))
        {
            flags.Add(new KybFlag("ENTITY_NOT_FOUND", "No registry record found in any enabled source. Coverage is limited to LEI holders, SEC filers and configured registries.", RiskTier.Medium));
        }

        if (!string.IsNullOrWhiteSpace(identity.AddressLine) && VirtualOfficeHint.IsMatch(identity.AddressLine))
            flags.Add(new KybFlag("VIRTUAL_OFFICE_ADDRESS", "Declared address looks like a PO box, mailbox service or registered-agent address.", RiskTier.Medium));
        if (address is { Verified: false, Error: not null } && !string.IsNullOrWhiteSpace(identity.AddressLine) && !address.Error.Contains("only covers", StringComparison.Ordinal))
            flags.Add(new KybFlag("ADDRESS_UNVERIFIED", $"Address could not be geocoded: {address.Error}", RiskTier.Low));

        var confidence = best is null ? 0 : Math.Round(best.OverallScore * 100, 1);
        return new BusinessVerificationResult(identity, status, confidence, best, ageMonths, address, sources, flags);
    }

    private async Task<AddressVerification?> VerifyAddressAsync(BusinessIdentity identity, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(identity.AddressLine)) return null;
        foreach (var geocoder in _geocoders)
        {
            try
            {
                var result = await geocoder.VerifyAsync(identity, ct);
                if (result.Verified || !result.Error!.Contains("only covers", StringComparison.Ordinal)) return result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Geocoder {Geocoder} failed", geocoder.Name);
                return new AddressVerification(geocoder.Name, false, null, null, null, ex.Message);
            }
        }
        return new AddressVerification("none", false, null, null, null, "No geocoder covers this country.");
    }

    internal static RegistryMatch Score(BusinessIdentity identity, RegistryRecord record)
    {
        var nameScore = NameMatcher.Similarity(identity.LegalName, record.LegalName);
        if (!string.IsNullOrWhiteSpace(identity.TradingName))
            nameScore = Math.Max(nameScore, NameMatcher.Similarity(identity.TradingName, record.LegalName) * 0.95);

        var addressScore = AddressMatcher.Similarity(identity.FullAddress, record.Address);
        var regMatch = !string.IsNullOrWhiteSpace(identity.RegistrationNumber) && !string.IsNullOrWhiteSpace(record.RegistrationNumber)
            && NameMatcher.Normalize(identity.RegistrationNumber).Replace(" ", "") == NameMatcher.Normalize(record.RegistrationNumber).Replace(" ", "");

        var overall = nameScore;
        if (!string.IsNullOrWhiteSpace(identity.AddressLine) && record.Address is not null)
            overall = 0.7 * nameScore + 0.3 * addressScore;
        if (regMatch) overall = Math.Min(1, overall + 0.15);
        var recordCountry = record.Jurisdiction?.Split('-', '_')[0];
        if (!string.IsNullOrWhiteSpace(identity.Country) && identity.Country.Length == 2
            && recordCountry is { Length: 2 }
            && !recordCountry.Equals(identity.Country, StringComparison.OrdinalIgnoreCase))
            overall *= 0.85;

        return new RegistryMatch(record, nameScore, addressScore, Math.Round(overall, 4));
    }

    private static VerificationStatus Classify(RegistryMatch? best, bool anySourceSucceeded)
    {
        if (!anySourceSucceeded) return VerificationStatus.Inconclusive;
        if (best is null) return VerificationStatus.NotFound;
        return best.OverallScore switch
        {
            >= 0.85 => VerificationStatus.Verified,
            >= 0.6 => VerificationStatus.PartialMatch,
            _ => VerificationStatus.NotFound
        };
    }
}
