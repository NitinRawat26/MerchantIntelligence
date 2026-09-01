using System.Text.Json.Serialization;
using MerchantIntelligence.CreditDecision;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var modelPath = builder.Configuration["CreditDecision:ModelPath"] ?? "models/credit-decision.zip";
builder.Services.AddSingleton<IDecisionPredictor>(sp =>
{
    var resolved = Path.IsPathRooted(modelPath)
        ? modelPath
        : Path.Combine(AppContext.BaseDirectory, modelPath);
    if (!File.Exists(resolved))
    {
        throw new FileNotFoundException(
            $"Credit decision model not found at '{resolved}'. Run the Trainer project or set CreditDecision:ModelPath.");
    }
    sp.GetRequiredService<ILogger<Program>>().LogInformation("Loading credit decision model from {Path}", resolved);
    return DecisionPredictor.Load(resolved);
});

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

app.UseAuthorization();

app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.Run();

public partial class Program;
