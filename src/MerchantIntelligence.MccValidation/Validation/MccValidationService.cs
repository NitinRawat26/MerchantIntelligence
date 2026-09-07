using MerchantIntelligence.MccValidation.Web;
using Microsoft.Extensions.Logging;

namespace MerchantIntelligence.MccValidation.Validation;

public sealed class MccValidationService
{
    private readonly WebsiteContentFetcher _fetcher;
    private readonly IReadOnlyList<IMccEvidenceProvider> _providers;
    private readonly EvidenceAggregator _aggregator;
    private readonly ILogger<MccValidationService> _logger;

    public MccValidationService(
        WebsiteContentFetcher fetcher,
        IEnumerable<IMccEvidenceProvider> providers,
        EvidenceAggregator aggregator,
        ILogger<MccValidationService> logger)
    {
        _fetcher = fetcher;
        _providers = providers.ToList();
        _aggregator = aggregator;
        _logger = logger;
    }

    public async Task<MccValidationResult> ValidateAsync(int declaredMcc, Uri websiteUrl, CancellationToken ct = default)
    {
        var website = await _fetcher.FetchAsync(websiteUrl, ct: ct);
        var context = new ValidationContext(declaredMcc, websiteUrl, website);

        var tasks = _providers.Select(async p =>
        {
            try
            {
                return (Provider: p, Evidence: await p.EvaluateAsync(context, ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Evidence provider {Provider} failed", p.Name);
                return (Provider: p, Evidence: ProviderEvidence.Failed(p.Name, ex.Message));
            }
        });

        var results = await Task.WhenAll(tasks);
        return _aggregator.Aggregate(declaredMcc, websiteUrl, results, website.FetchedPages);
    }
}
