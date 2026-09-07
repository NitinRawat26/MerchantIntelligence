using System.Text.Json.Serialization;
using MerchantIntelligence.CreditDecision;
using MerchantIntelligence.Kyb;
using MerchantIntelligence.Kyb.Registry;
using MerchantIntelligence.Kyb.Sanctions;
using MerchantIntelligence.MccValidation.Classification;
using MerchantIntelligence.MccValidation.Taxonomy;
using MerchantIntelligence.MccValidation.Validation;
using MerchantIntelligence.MccValidation.Web;
using MerchantIntelligence.Platform;
using MerchantIntelligence.Platform.Integrations;
using MerchantIntelligence.Platform.Storage;
using MerchantIntelligence.Underwriting.Benchmarks;
using MerchantIntelligence.Underwriting.Explainability;
using MerchantIntelligence.Underwriting.Plausibility;
using MerchantIntelligence.Underwriting.Pricing;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var modelPath = ResolvePath(builder.Configuration["CreditDecision:ModelPath"] ?? "models/credit-decision.zip");
IDecisionPredictor LoadBootstrapModel(IServiceProvider sp)
{
    if (!File.Exists(modelPath))
    {
        throw new FileNotFoundException(
            $"Credit decision model not found at '{modelPath}'. Run the Trainer project or set CreditDecision:ModelPath.");
    }
    sp.GetRequiredService<ILogger<Program>>().LogInformation("Loading credit decision model from {Path}", modelPath);
    return DecisionPredictor.Load(modelPath);
}

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

var kybOptions = builder.Configuration.GetSection("Kyb").Get<KybOptions>() ?? new KybOptions();
var sanctionsOptions = builder.Configuration.GetSection("Sanctions").Get<SanctionsOptions>() ?? new SanctionsOptions();
sanctionsOptions.CacheDirectory = ResolvePath(sanctionsOptions.CacheDirectory);
builder.Services.AddMerchantKyb(kybOptions, sanctionsOptions);

builder.Services.AddSingleton(IndustryBenchmarks.Default);
builder.Services.AddSingleton(sp => new DecisionExplainer(sp.GetRequiredService<IDecisionPredictor>()));
builder.Services.AddSingleton<ReservePricingRecommender>();
builder.Services.AddSingleton<VolumePlausibilityAnalyzer>();

var platformOptions = builder.Configuration.GetSection("Platform").Get<PlatformOptions>() ?? new PlatformOptions();
platformOptions.DatabasePath = platformOptions.DatabasePath == ":memory:" ? platformOptions.DatabasePath : ResolvePath(platformOptions.DatabasePath);
platformOptions.ModelsDirectory = ResolvePath(platformOptions.ModelsDirectory);
var matchOptions = builder.Configuration.GetSection("Match").Get<MatchOptions>() ?? new MatchOptions();
if (matchOptions.LocalListPath is not null) matchOptions.LocalListPath = ResolvePath(matchOptions.LocalListPath);
builder.Services.AddMerchantPlatform(platformOptions, matchOptions, LoadBootstrapModel, modelPath);

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
