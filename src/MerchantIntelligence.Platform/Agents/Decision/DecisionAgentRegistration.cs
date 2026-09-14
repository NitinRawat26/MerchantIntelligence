using MerchantIntelligence.Platform.Workflows;
using Microsoft.Extensions.DependencyInjection;

namespace MerchantIntelligence.Platform.Agents.Decision;

public static class DecisionAgentRegistration
{
    /// <summary>Registers the Decision agent and the steps it owns by default.</summary>
    public static IServiceCollection AddDecisionAgent(this IServiceCollection services)
    {
        services.AddSingleton<IAssessmentStep, TermsStep>();
        services.AddSingleton<IAssessmentStep, ScoreStep>();
        services.AddSingleton<IAssessmentStep, CaseStep>();
        services.AddSingleton<IAssessmentAgent, DecisionAgent>();
        return services;
    }
}
