using MerchantIntelligence.Platform.Workflows;
using Microsoft.Extensions.DependencyInjection;

namespace MerchantIntelligence.Platform.Agents.Financial;

public static class FinancialAgentRegistration
{
    /// <summary>Registers the Financial agent and the steps it owns by default.</summary>
    public static IServiceCollection AddFinancialAgent(this IServiceCollection services)
    {
        services.AddSingleton<IAssessmentStep, BankStatementStep>();
        services.AddSingleton<IAssessmentStep, FinancialsStep>();
        services.AddSingleton<IAssessmentStep, PlausibilityStep>();
        services.AddSingleton<IAssessmentStep, CreditStep>();
        services.AddSingleton<IAssessmentAgent, FinancialAgent>();
        return services;
    }
}
