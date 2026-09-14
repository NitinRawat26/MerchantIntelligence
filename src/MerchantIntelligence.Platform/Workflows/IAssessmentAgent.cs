using MerchantIntelligence.Platform.Assessment;



namespace MerchantIntelligence.Platform.Workflows;

/// <summary>What an agent concluded once its tools have run.</summary>
public sealed record AgentReview(string Summary, IReadOnlyList<AgentFinding> Findings);

/// <summary>
/// A rule-based agent: owns a group of steps (its tools), runs them through the workflow engine and then
/// reviews their combined output – raising advisories, taking extra deterministic actions and summarising
/// for the analyst. Agents never change scores, hard stops or the policy outcome.
/// </summary>
public interface IAssessmentAgent
{
    WorkflowAgentDescriptor Descriptor { get; }
    Task<AgentReview> ReviewAsync(AssessmentContext ctx, IReadOnlyList<string> ownedSteps);
}
