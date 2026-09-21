using MerchantIntelligence.Platform.Profiling;
using MerchantIntelligence.Platform.Workflows;
using Microsoft.Extensions.DependencyInjection;

namespace MerchantIntelligence.Platform.Agents.Profile;

public static class ProfileAgentRegistration
{
    public static IServiceCollection AddProfileAgent(this IServiceCollection services)
    {
        services.AddSingleton<MerchantProfiler>();
        services.AddSingleton<IAssessmentStep, EntityStep>();
        services.AddSingleton<IAssessmentStep, SegmentStep>();
        services.AddSingleton<IAssessmentAgent, ProfileAgent>();
        return services;
    }
}
