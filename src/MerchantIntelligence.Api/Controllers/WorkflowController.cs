using System.ComponentModel.DataAnnotations;
using MerchantIntelligence.Platform.Assessment;
using MerchantIntelligence.Platform.Cases;
using MerchantIntelligence.Platform.Workflows;
using Microsoft.AspNetCore.Mvc;

namespace MerchantIntelligence.Api.Controllers;

public sealed class PublishWorkflowRequest
{
    [Required] public WorkflowDefinition Workflow { get; set; } = new();
    [Required, MinLength(1)] public string Author { get; set; } = string.Empty;
    public string? Comment { get; set; }
}

public sealed record WorkflowValidationResponse(bool Valid, string? Error, WorkflowPlan? Plan);

/// <summary>
/// Versioned, data-driven assessment workflows. The active definition decides which checks the Full Assessment
/// runs, in what order and with which failure policy; the plan endpoints show how the engine will schedule it.
/// </summary>
[ApiController]
[Route("api/workflows")]
public sealed class WorkflowController(WorkflowRepository workflows, WorkflowPlanner planner, AssessmentService assessments, AuditTrail audit) : ControllerBase
{
    /// <summary>Every step the engine knows, with its default dependencies and tunable parameters.</summary>
    [HttpGet("catalog")]
    public ActionResult<IReadOnlyList<WorkflowStepDescriptor>> Catalog() => Ok(planner.Catalog);

    /// <summary>Every agent the engine knows, with its mandate and the steps it owns by default.</summary>
    [HttpGet("agents")]
    public ActionResult<IReadOnlyList<WorkflowAgentDescriptor>> Agents() => Ok(planner.AgentCatalog);

    /// <summary>The built-in default definition, for "reset to default" in the editor.</summary>
    [HttpGet("default")]
    public ActionResult<WorkflowDefinition> Default() => Ok(WorkflowRepository.LoadDefault());

    [HttpGet("active")]
    public ActionResult<WorkflowDefinition> Active() => Ok(workflows.Active);

    /// <summary>Stages and diagram for the active workflow.</summary>
    [HttpGet("active/plan")]
    public ActionResult<WorkflowPlan> ActivePlan() => Ok(planner.Plan(workflows.Active));

    /// <summary>The active workflow's steps as the Assess page will list them.</summary>
    [HttpGet("active/steps")]
    public ActionResult<IReadOnlyList<AssessmentStepDescriptor>> ActiveSteps() => Ok(assessments.PlannedSteps());

    [HttpGet("history")]
    public ActionResult<IReadOnlyList<WorkflowVersion>> History() => Ok(workflows.History());

    [HttpGet("{version:int}")]
    public ActionResult<WorkflowDefinition> Version(int version) =>
        workflows.GetVersion(version) is { } def ? Ok(def) : NotFound();

    /// <summary>Validates a draft and, when valid, returns the stages it would run in plus any degradation warnings.</summary>
    [HttpPost("validate")]
    public ActionResult<WorkflowValidationResponse> Validate([FromBody] WorkflowDefinition definition)
    {
        try { return Ok(new WorkflowValidationResponse(true, null, planner.Plan(definition))); }
        catch (WorkflowValidationException ex) { return Ok(new WorkflowValidationResponse(false, ex.Message, null)); }
    }

    [HttpPost("publish")]
    public ActionResult<WorkflowVersion> Publish([FromBody] PublishWorkflowRequest request)
    {
        try { return Ok(workflows.Publish(request.Workflow, request.Author, request.Comment)); }
        catch (WorkflowValidationException ex) { return ValidationProblem(ex.Message); }
    }

    [HttpPost("rollback/{version:int}")]
    public ActionResult<WorkflowVersion> Rollback(int version, [FromBody] ActorRequest request)
    {
        try
        {
            var result = workflows.Rollback(version, request.Actor);
            audit.Record(null, request.Actor, "workflow.rolled_back", new { to = version, published = result.Version, request.Reason });
            return Ok(result);
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }
}
