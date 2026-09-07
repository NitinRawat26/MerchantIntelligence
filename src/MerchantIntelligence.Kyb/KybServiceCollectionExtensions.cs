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
            c.Timeout = TimeSpan.FromSeconds(20);
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
        services.AddSingleton<IAddressGeocoder, CensusAddressGeocoder>();
        services.AddSingleton<BusinessVerificationService>();

        services.AddSingleton<ISanctionsListSource>(new OpenSanctionsSource("sanctions"));
        if (sanctionsOptions.IncludePeps) services.AddSingleton<ISanctionsListSource>(new OpenSanctionsSource("peps"));
        if (sanctionsOptions.IncludeRawGovernmentLists)
        {
            services.AddSingleton<ISanctionsListSource, OfacSdnSource>();
            services.AddSingleton<ISanctionsListSource, UnConsolidatedSource>();
        }
        services.AddSingleton<IAdverseMediaProvider, GdeltAdverseMediaProvider>();
        services.AddSingleton<SanctionsScreeningService>();

        services.AddSingleton(ProhibitedBusinessDetector.Default);
        services.AddSingleton<WebsiteComplianceScanner>();
        services.AddSingleton<KybReportService>();
        return services;
    }
}
