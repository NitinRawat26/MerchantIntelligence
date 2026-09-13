using MerchantIntelligence.Platform.Workflows;
using Microsoft.Extensions.DependencyInjection;

namespace MerchantIntelligence.Platform.Agents.PreCheck;

public static class PreCheckAgentRegistration
{
    /// <summary>Registers the PreCheck agent and the steps it owns by default.</summary>
    public static IServiceCollection AddPreCheckAgent(this IServiceCollection services)
    {
        services.AddSingleton<IAssessmentStep, WebsiteStep>();
        services.AddSingleton<IAssessmentStep, ProhibitedStep>();
        services.AddSingleton<IAssessmentStep, MccStep>();
        services.AddSingleton<IAssessmentAgent, PreCheckAgent>();
        return services;
    }
}
