using System.Text.Json.Serialization;
using MerchantIntelligence.CreditDecision;
using MerchantIntelligence.MccValidation.Classification;
using MerchantIntelligence.MccValidation.Taxonomy;
using MerchantIntelligence.MccValidation.Validation;
using MerchantIntelligence.MccValidation.Web;

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

builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? new[] { "http://localhost:4200" })
    .AllowAnyHeader()
    .AllowAnyMethod()));

builder.Services.AddHttpClient(WebsiteContentFetcher.HttpClientName, c =>
{
    c.Timeout = TimeSpan.FromSeconds(15);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; MerchantIntelligenceBot/1.0)");
    c.MaxResponseContentBufferSize = 4 * 1024 * 1024;
});
builder.Services.AddSingleton(MccCatalog.Default);
builder.Services.AddSingleton<WebsiteContentFetcher>();
builder.Services.AddSingleton<EvidenceAggregator>();
builder.Services.AddSingleton<MccValidationService>();
builder.Services.AddSingleton<IMccEvidenceProvider, KeywordTaxonomyProvider>();
builder.Services.AddSingleton<IMccEvidenceProvider, StructuredDataProvider>();
builder.Services.AddSingleton<IEdgarDomainIndex>(_ =>
    new FileEdgarDomainIndex(ResolvePath(builder.Configuration["MccValidation:FilerIndexPath"] ?? "models/filer-domains.json")));
builder.Services.AddSingleton<IMccEvidenceProvider, EdgarSicProvider>();

var mccModelPath = ResolvePath(builder.Configuration["MccValidation:ModelPath"] ?? "models/mcc-classifier.zip");
if (File.Exists(mccModelPath))
{
    builder.Services.AddSingleton(_ => MccTextClassifier.Load(mccModelPath));
    builder.Services.AddSingleton<IMccEvidenceProvider, TextClassifierProvider>();
}

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

app.UseCors();
app.UseAuthorization();

app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

if (!File.Exists(mccModelPath))
{
    app.Logger.LogWarning("MCC classifier not found at {Path}; running without the ML text classifier provider.", mccModelPath);
}

app.Run();

static string ResolvePath(string path) =>
    Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);

public partial class Program;
