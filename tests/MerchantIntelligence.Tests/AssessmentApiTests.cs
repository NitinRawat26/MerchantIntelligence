using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MerchantIntelligence.Kyb.Registry;
using MerchantIntelligence.Kyb.Sanctions;
using MerchantIntelligence.MccValidation.Web;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace MerchantIntelligence.Tests;

/// <summary>
/// Full-assessment orchestration, run offline: registry / sanctions / RDAP fetches are stubbed to fail so those checks
/// become coverage gaps, while the merchant website is served by a fake so website, prohibited and MCC checks run.
/// </summary>
public sealed class AssessmentApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private sealed class FakeSiteHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            const string html = """
                <html><head><title>Good Shoes Ltd</title></head><body>
                <h1>Good Shoes Ltd - Handmade leather shoes and boots</h1>
                <p>All prices in USD $. We accept Visa and Mastercard. Contact: support@goodshoes.example, +1 555 010 0100.</p>
                <a href="/privacy">Privacy Policy</a> <a href="/terms">Terms</a> <a href="/refunds">Refund Policy</a>
                </body></html>
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html, Encoding.UTF8, "text/html") });
        }
    }

    private sealed class UnavailableHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("public source unavailable (offline test)");
    }

    private readonly HttpClient _client;

    public AssessmentApiTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Platform:DatabasePath", ":memory:");
            b.UseSetting("Platform:ModelsDirectory", Path.Combine(Path.GetTempPath(), $"mi-assess-models-{Guid.NewGuid():N}"));
            b.ConfigureServices(s =>
            {
                s.AddHttpClient(WebsiteContentFetcher.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new FakeSiteHandler());
                s.AddHttpClient(KybOptions.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new UnavailableHandler());
                s.AddHttpClient(SanctionsOptions.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new UnavailableHandler());
            });
        }).CreateClient();
    }

    private static object Request(bool createCase = true, string? bankCsv = null, string? financials = null) => new
    {
        business = new { legalName = "Good Shoes Ltd", country = "US", websiteUrl = "https://goodshoes.example", city = "Springfield" },
        owners = new[] { new { fullName = "Jane Cobbler", role = "Owner", ownershipPercent = 100 } },
        businessDescription = "Handmade leather shoes sold online.",
        merchantCategoryCode = 5661, annualVolume = 600000, averageTicket = 120, highestTicket = 900,
        existingRelationship = false, employeeCount = 6, yearsInBusiness = 4, hasPhysicalLocation = true,
        bankStatementCsv = bankCsv, financialStatementText = financials, actor = "tester", createCase
    };

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    [Fact]
    public async Task Steps_catalogue_is_ordered_and_complete()
    {
        var steps = await Json(await _client.GetAsync("/api/assessment/steps"));
        var ids = steps.EnumerateArray().Select(s => s.GetProperty("id").GetString()).ToList();
        Assert.Equal(["verification", "screening", "website", "prohibited", "mcc", "match", "bank", "financials", "plausibility", "credit", "terms", "score", "case"], ids);
    }

    [Fact]
    public async Task Run_completes_every_step_keeps_failed_sources_as_gaps_and_persists_result_with_pdf()
    {
        const string csv = "Date,Description,Amount\n2024-01-02,STRIPE TRANSFER,4200\n2024-01-20,RENT,-1500\n2024-02-02,STRIPE TRANSFER,5100\n2024-03-02,STRIPE TRANSFER,4800\n";
        const string pnl = "Revenue,650000\nCost of goods sold,300000\nOperating expenses,200000\nNet income,150000\nTotal assets,400000\nTotal liabilities,150000\n";
        var root = await Json(await _client.PostAsJsonAsync("/api/assessment/run", Request(bankCsv: csv, financials: pnl)));

        var id = root.GetProperty("id").GetString()!;
        Assert.StartsWith("ASMT-", id);

        var steps = root.GetProperty("steps").EnumerateArray().ToDictionary(s => s.GetProperty("id").GetString()!, s => s.GetProperty("status").GetString());
        Assert.Equal(13, steps.Count);
        Assert.Equal("Succeeded", steps["verification"]);
        Assert.Equal("Succeeded", steps["screening"]);
        Assert.Equal("Succeeded", steps["website"]);
        Assert.Equal("Succeeded", steps["prohibited"]);
        Assert.Equal("Succeeded", steps["bank"]);
        Assert.Equal("Succeeded", steps["financials"]);
        Assert.Equal("Succeeded", steps["plausibility"]);
        Assert.Equal("Succeeded", steps["credit"]);
        Assert.Equal("Succeeded", steps["terms"]);
        Assert.Equal("Succeeded", steps["score"]);
        Assert.Equal("Succeeded", steps["case"]);

        Assert.All(root.GetProperty("verification").GetProperty("sources").EnumerateArray(), s => Assert.False(s.GetProperty("succeeded").GetBoolean()));
        Assert.All(root.GetProperty("screening").GetProperty("lists").EnumerateArray(), l => Assert.NotEqual(JsonValueKind.Null, l.GetProperty("error").ValueKind));
        Assert.Equal("NotConfigured", root.GetProperty("match").GetProperty("availability").GetString());
        Assert.Equal(4, root.GetProperty("bankStatement").GetProperty("transactionCount").GetInt32());
        Assert.True(root.GetProperty("financialStatement").GetProperty("ratios").GetArrayLength() > 0);

        var decision = root.GetProperty("decision");
        Assert.InRange(decision.GetProperty("score").GetInt32(), 0, 1000);
        Assert.NotEqual("Approve", decision.GetProperty("outcome").GetString());
        Assert.True(decision.GetProperty("coveragePercent").GetDouble() < 100);

        var expl = root.GetProperty("explainability");
        var gaps = expl.GetProperty("coverageGaps").EnumerateArray().Select(g => g.GetString()!).ToList();
        Assert.Contains(gaps, g => g.StartsWith("Business identity", StringComparison.Ordinal));
        Assert.Contains(gaps, g => g.StartsWith("Sanctions / PEP / media", StringComparison.Ordinal));
        Assert.Contains(gaps, g => g.StartsWith("MATCH / TMF", StringComparison.Ordinal));
        var outcomes = expl.GetProperty("checkOutcomes").EnumerateArray().ToDictionary(o => o.GetProperty("check").GetString()!, o => o);
        Assert.Equal("Unavailable", outcomes["Sanctions / PEP / media"].GetProperty("result").GetString());
        Assert.False(outcomes["Sanctions / PEP / media"].GetProperty("covered").GetBoolean());
        Assert.Equal("Unavailable", outcomes["Business identity"].GetProperty("result").GetString());
        Assert.True(expl.GetProperty("narrative").GetArrayLength() >= 5);
        Assert.True(expl.GetProperty("checkOutcomes").GetArrayLength() >= 10);
        Assert.True(expl.GetProperty("scoreComponents").GetArrayLength() > 0);
        Assert.True(expl.GetProperty("creditContributions").GetArrayLength() > 0);
        Assert.False(string.IsNullOrWhiteSpace(expl.GetProperty("decidingRule").GetString()));
        Assert.True(expl.GetProperty("analystNextSteps").GetArrayLength() > 0);

        Assert.StartsWith("CASE-", root.GetProperty("case").GetProperty("id").GetString());
        Assert.True(root.GetProperty("decisionLogId").GetInt64() > 0);
        Assert.Equal(JsonValueKind.Undefined, root.GetProperty("intake").TryGetProperty("bankStatementCsv", out var leaked) ? leaked.ValueKind : JsonValueKind.Undefined);

        var stored = await Json(await _client.GetAsync($"/api/assessment/{id}"));
        Assert.Equal(id, stored.GetProperty("id").GetString());
        Assert.Equal(decision.GetProperty("score").GetInt32(), stored.GetProperty("decision").GetProperty("score").GetInt32());

        var list = await Json(await _client.GetAsync("/api/assessment"));
        Assert.Contains(list.EnumerateArray(), a => a.GetProperty("id").GetString() == id);

        var pdf = await _client.GetAsync($"/api/assessment/{id}/pdf");
        Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);
        var bytes = await pdf.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length > 10_000, $"pdf too small: {bytes.Length}");
        Assert.Equal("%PDF", Encoding.ASCII.GetString(bytes, 0, 4));
    }

    [Fact]
    public async Task Run_skips_optional_checks_and_case_when_not_requested()
    {
        var root = await Json(await _client.PostAsJsonAsync("/api/assessment/run", Request(createCase: false)));
        var steps = root.GetProperty("steps").EnumerateArray().ToDictionary(s => s.GetProperty("id").GetString()!, s => s.GetProperty("status").GetString());
        Assert.Equal("Skipped", steps["bank"]);
        Assert.Equal("Skipped", steps["financials"]);
        Assert.Equal("Skipped", steps["case"]);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("case").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("bankStatement").ValueKind);
    }

    [Fact]
    public async Task Run_accepts_multipart_with_uploaded_statements()
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(JsonSerializer.Serialize(Request(createCase: false)), Encoding.UTF8, "application/json"), "request");
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("Date,Description,Amount\n2024-01-02,SQUARE INC,1000\n2024-02-02,SQUARE INC,1200\n")), "bankStatement", "bank.csv");
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("Revenue,100000\nNet income,10000\n")), "financialStatement", "pnl.csv");
        var root = await Json(await _client.PostAsync("/api/assessment/run", form));
        Assert.Equal(2, root.GetProperty("bankStatement").GetProperty("transactionCount").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, root.GetProperty("financialStatement").ValueKind);
        Assert.Equal("bank.csv", root.GetProperty("intake").GetProperty("bankStatementSource").GetString());
    }

    [Fact]
    public async Task Stream_emits_catalogue_step_events_then_result()
    {
        var response = await _client.PostAsJsonAsync("/api/assessment/run/stream", Request(createCase: false));
        response.EnsureSuccessStatusCode();
        var lines = (await response.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();

        Assert.Equal("steps", lines[0].GetProperty("type").GetString());
        Assert.Equal("result", lines[^1].GetProperty("type").GetString());
        var stepEvents = lines.Where(l => l.GetProperty("type").GetString() == "step").Select(l => l.GetProperty("step")).ToList();
        Assert.Contains(stepEvents, s => s.GetProperty("status").GetString() == "Running");
        Assert.Equal(13, stepEvents.Count(s => s.GetProperty("status").GetString() is "Succeeded" or "Failed" or "Skipped"));
        Assert.StartsWith("ASMT-", lines[^1].GetProperty("result").GetProperty("id").GetString());
    }

    [Fact]
    public async Task Run_validates_input()
    {
        var r = await _client.PostAsJsonAsync("/api/assessment/run", new { business = new { legalName = "X" }, merchantCategoryCode = 5661, annualVolume = 1000, averageTicket = 50, highestTicket = 10 });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        r = await _client.PostAsJsonAsync("/api/assessment/run", new { business = new { legalName = "X", websiteUrl = "not a url" }, merchantCategoryCode = 5661, annualVolume = 1000, averageTicket = 50, highestTicket = 100 });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/assessment/ASMT-DOES-NOT-EXIST")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/assessment/ASMT-DOES-NOT-EXIST/pdf")).StatusCode);
    }
}
