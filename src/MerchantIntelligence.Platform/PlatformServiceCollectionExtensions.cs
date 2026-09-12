using MerchantIntelligence.CreditDecision;
using MerchantIntelligence.Platform.Assessment;
using MerchantIntelligence.Platform.Cases;
using MerchantIntelligence.Platform.Integrations;
using MerchantIntelligence.Platform.ModelOps;
using MerchantIntelligence.Platform.Rules;
using MerchantIntelligence.Platform.Scoring;
using MerchantIntelligence.Platform.Storage;
using MerchantIntelligence.Platform.Webhooks;
using MerchantIntelligence.Platform.Workflows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MerchantIntelligence.Platform;

public static class PlatformServiceCollectionExtensions
{
    /// <summary>
    /// Registers scoring, rules, cases/audit, webhooks, model ops and the MATCH boundary. The credit model
    /// registered under <see cref="IDecisionPredictor"/> before this call becomes the bootstrap champion; after
    /// this call <see cref="IDecisionPredictor"/> resolves to the <see cref="ModelRegistry"/> so promotions take
    /// effect without a restart.
    /// </summary>
    public static IServiceCollection AddMerchantPlatform(this IServiceCollection services, PlatformOptions options,
        MatchOptions matchOptions, Func<IServiceProvider, IDecisionPredictor> bootstrapPredictor, string bootstrapModelPath)
    {
        services.AddSingleton(options);
        services.AddSingleton(matchOptions);
        services.AddSingleton<PlatformDatabase>();
        services.AddSingleton<AuditTrail>();
        services.AddSingleton<RulesEngine>();
        services.AddSingleton<RuleSetRepository>();
        services.AddSingleton<UnifiedRiskScorer>();
        services.AddHttpClient(WebhookDispatcher.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(10));
        services.AddSingleton(sp => new WebhookDispatcher(sp.GetRequiredService<PlatformDatabase>(),
            sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<ILogger<WebhookDispatcher>>()));
        services.AddSingleton<CaseService>();

        services.AddSingleton(sp => new ModelRegistry(sp.GetRequiredService<PlatformDatabase>(), sp.GetRequiredService<AuditTrail>(),
            sp.GetRequiredService<WebhookDispatcher>(), options, bootstrapPredictor(sp), bootstrapModelPath));
        services.AddSingleton<IDecisionPredictor>(sp => sp.GetRequiredService<ModelRegistry>());
        services.AddSingleton<ModelOpsService>();
        services.AddSingleton<IAssessmentStep, VerificationStep>();
        services.AddSingleton<IAssessmentStep, ScreeningStep>();
        services.AddSingleton<IAssessmentStep, WebsiteStep>();
        services.AddSingleton<IAssessmentStep, ProhibitedStep>();
        services.AddSingleton<IAssessmentStep, MccStep>();
        services.AddSingleton<IAssessmentStep, MatchStep>();
        services.AddSingleton<IAssessmentStep, BankStatementStep>();
        services.AddSingleton<IAssessmentStep, FinancialsStep>();
        services.AddSingleton<IAssessmentStep, PlausibilityStep>();
        services.AddSingleton<IAssessmentStep, CreditStep>();
        services.AddSingleton<IAssessmentStep, TermsStep>();
        services.AddSingleton<IAssessmentStep, ScoreStep>();
        services.AddSingleton<IAssessmentStep, CaseStep>();
        services.AddSingleton<WorkflowPlanner>();
        services.AddSingleton<WorkflowRepository>();
        services.AddSingleton<WorkflowRunner>();
        services.AddSingleton<AssessmentService>();

        services.AddHttpClient(HttpMatchProvider.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(15));
        if (!string.IsNullOrWhiteSpace(matchOptions.Endpoint))
            services.AddSingleton<IMatchProvider, HttpMatchProvider>();
        else if (!string.IsNullOrWhiteSpace(matchOptions.LocalListPath) && File.Exists(matchOptions.LocalListPath))
            services.AddSingleton<IMatchProvider>(new LocalListMatchProvider(matchOptions.LocalListPath));
        else
            services.AddSingleton<IMatchProvider, UnavailableMatchProvider>();

        return services;
    }
}
