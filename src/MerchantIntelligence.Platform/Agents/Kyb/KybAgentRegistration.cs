using MerchantIntelligence.Platform.Workflows;
using Microsoft.Extensions.DependencyInjection;

namespace MerchantIntelligence.Platform.Agents.Kyb;

public static class KybAgentRegistration
{
    /// <summary>Registers the Kyb agent and the steps it owns by default.</summary>
    public static IServiceCollection AddKybAgent(this IServiceCollection services)
    {
        services.AddSingleton<IAssessmentStep, VerificationStep>();
        services.AddSingleton<IAssessmentStep, LocalPresenceStep>();
        services.AddSingleton<IAssessmentStep, ScreeningStep>();
        services.AddSingleton<IAssessmentStep, MatchStep>();
        services.AddSingleton<IAssessmentAgent, KybAgent>();
        return services;
    }
}
