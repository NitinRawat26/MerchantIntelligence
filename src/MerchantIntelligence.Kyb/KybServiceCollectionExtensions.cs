using MerchantIntelligence.Kyb.Compliance;
using MerchantIntelligence.Kyb.Prohibited;
using MerchantIntelligence.Kyb.Registry;
using MerchantIntelligence.Kyb.Sanctions;
using Microsoft.Extensions.DependencyInjection;

namespace MerchantIntelligence.Kyb;

public static class KybServiceCollectionExtensions
{
    public static IServiceCollection AddMerchantKyb(this IServiceCollection services, KybOptions kybOptions, SanctionsOptions sanctionsOptions)
    {
        services.AddSingleton(kybOptions);
        services.AddSingleton(sanctionsOptions);

        services.AddHttpClient(KybOptions.HttpClientName, c =>
        {
            c.Timeout = TimeSpan.FromSeconds(25);
            c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", kybOptions.UserAgent);
            c.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        });
        services.AddHttpClient(SanctionsOptions.HttpClientName, c =>
        {
            c.Timeout = TimeSpan.FromMinutes(5);
            c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", kybOptions.UserAgent);
        });

        services.AddSingleton<IBusinessRegistryProvider, GleifRegistryProvider>();
        services.AddSingleton<IBusinessRegistryProvider, EdgarRegistryProvider>();
        services.AddSingleton<IBusinessRegistryProvider, OpenCorporatesRegistryProvider>();
        services.AddSingleton<IBusinessRegistryProvider, CompaniesHouseRegistryProvider>();
        services.AddSingleton<IBusinessRegistryProvider, KentuckySosRegistryProvider>();
        services.AddSingleton<IAddressGeocoder, CensusAddressGeocoder>();
        services.AddSingleton<NominatimGeocoder>();
        services.AddSingleton<ILocalPresenceProvider, OsmLocalPresenceProvider>();
        services.AddSingleton<ILocalPresenceProvider, FoursquareLocalPresenceProvider>();
        services.AddSingleton<ILocalPresenceProvider, GooglePlacesLocalPresenceProvider>();
        services.AddSingleton<LocalPresenceService>();
        services.AddSingleton<BusinessVerificationService>();

        services.AddSingleton<ISanctionsListSource>(new OpenSanctionsSource("sanctions"));
        if (sanctionsOptions.IncludePeps) services.AddSingleton<ISanctionsListSource>(new OpenSanctionsSource("peps"));
        if (sanctionsOptions.IncludeRawGovernmentLists)
        {
            services.AddSingleton<ISanctionsListSource, OfacSdnSource>();
            services.AddSingleton<ISanctionsListSource, UnConsolidatedSource>();
        }
        foreach (var source in sanctionsOptions.AdverseMediaSources.Select(s => s.Trim().ToLowerInvariant()).Distinct())
        {
            switch (source)
            {
                case "gdelt": services.AddSingleton<IAdverseMediaSource, GdeltAdverseMediaSource>(); break;
                case "googlenews": services.AddSingleton<IAdverseMediaSource, GoogleNewsAdverseMediaSource>(); break;
                case "bingnews": services.AddSingleton<IAdverseMediaSource, BingNewsAdverseMediaSource>(); break;
                case "wikipedia": services.AddSingleton<IAdverseMediaSource, WikipediaAdverseMediaSource>(); break;
                case "courtlistener": services.AddSingleton<IAdverseMediaSource, CourtListenerAdverseMediaSource>(); break;
                default: throw new ArgumentException($"Unknown adverse-media source '{source}'. Known: gdelt, googlenews, bingnews, wikipedia, courtlistener.");
            }
        }
        services.AddSingleton<IAdverseMediaProvider, CompositeAdverseMediaProvider>();
        services.AddSingleton<SanctionsScreeningService>();

        services.AddSingleton(ProhibitedBusinessDetector.Default);
        services.AddSingleton<RdapDomainLookup>();
        services.AddSingleton<WebsiteComplianceScanner>();
        services.AddSingleton<KybReportService>();
        return services;
    }
}
