using MerchantIntelligence.Api.Controllers;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace MerchantIntelligence.Api.Swagger;

/// <summary>
/// The assessment run endpoints read their body by hand (JSON or multipart with files), so nothing is bound and
/// Swagger would show no request body. This describes both shapes so the "Try it out" panel offers an editor.
/// </summary>
public sealed class AssessmentRunRequestBodyFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (context.MethodInfo.DeclaringType != typeof(AssessmentController) ||
            context.MethodInfo.Name is not (nameof(AssessmentController.Run) or nameof(AssessmentController.RunStream)))
            return;

        var request = context.SchemaGenerator.GenerateSchema(typeof(AssessmentRequest), context.SchemaRepository);
        var file = new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" };

        operation.RequestBody = new OpenApiRequestBody
        {
            Required = true,
            Description = "Assessment intake as JSON, or multipart/form-data with a 'request' JSON part plus optional statement files.",
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["application/json"] = new() { Schema = request },
                ["multipart/form-data"] = new()
                {
                    Schema = new OpenApiSchema
                    {
                        Type = JsonSchemaType.Object,
                        Required = new HashSet<string> { "request" },
                        Properties = new Dictionary<string, IOpenApiSchema>
                        {
                            ["request"] = new OpenApiSchema { Type = JsonSchemaType.String, Description = "AssessmentRequest as a JSON string." },
                            ["bankStatement"] = file,
                            ["financialStatement"] = file,
                        }
                    }
                }
            }
        };
    }
}
