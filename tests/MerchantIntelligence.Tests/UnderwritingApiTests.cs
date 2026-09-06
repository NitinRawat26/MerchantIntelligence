using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MerchantIntelligence.CreditDecision;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MerchantIntelligence.Tests;

public sealed class UnderwritingApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public UnderwritingApiTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.WithWebHostBuilder(b =>
            b.ConfigureServices(s => s.Replace(ServiceDescriptor.Singleton<IDecisionPredictor, RuleBasedPredictor>())))
            .CreateClient();
    }

    [Fact]
    public async Task Explain_returns_contributions_and_reason_codes()
    {
        var response = await _client.PostAsJsonAsync("/api/underwriting/explain", new
        {
            merchantCategoryCode = 7995, annualVolume = 5000000, averageTicket = 900, highestTicket = 20000, matchFound = true, existingRelationship = false
        });
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        Assert.Equal("Declined", root.GetProperty("decision").GetString());
        Assert.Equal(6, root.GetProperty("contributions").GetArrayLength());
        Assert.Contains(root.GetProperty("reasonCodes").EnumerateArray(), r => r.GetProperty("code").GetString() == "MATCH_LISTED");
        Assert.False(string.IsNullOrEmpty(root.GetProperty("narrative").GetString()));
    }

    [Fact]
    public async Task Explain_rejects_highest_below_average()
    {
        var response = await _client.PostAsJsonAsync("/api/underwriting/explain", new
        {
            merchantCategoryCode = 5812, annualVolume = 100000, averageTicket = 500, highestTicket = 100
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RecommendTerms_returns_band_reserve_and_pricing()
    {
        var response = await _client.PostAsJsonAsync("/api/underwriting/recommend-terms", new
        {
            merchantCategoryCode = 5411, annualVolume = 600000, averageTicket = 40, highestTicket = 250, existingRelationship = true,
            deliveryDays = 0, cardNotPresentShare = 0.05
        });
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        Assert.Contains(root.GetProperty("riskBand").GetString(), new[] { "A", "B" });
        Assert.True(root.GetProperty("reserve").GetProperty("rollingPercent").GetDouble() <= 5);
        Assert.True(root.GetProperty("pricing").GetProperty("interchangePlusMarkupBps").GetDouble() > 0);
        Assert.True(root.GetProperty("factors").GetArrayLength() > 0);
    }

    [Fact]
    public async Task RecommendTerms_validates_ranges()
    {
        var response = await _client.PostAsJsonAsync("/api/underwriting/recommend-terms", new
        {
            merchantCategoryCode = 5411, annualVolume = 600000, averageTicket = 40, highestTicket = 250, cardNotPresentShare = 1.7
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task VolumePlausibility_flags_startup_outlier()
    {
        var response = await _client.PostAsJsonAsync("/api/underwriting/volume-plausibility", new
        {
            annualVolume = 20000000, averageTicket = 50, highestTicket = 500, merchantCategoryCode = 5812, employeeCount = 1, yearsInBusiness = 0.2
        });
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var flags = json.RootElement.GetProperty("flags").EnumerateArray().Select(f => f.GetProperty("code").GetString()).ToList();
        Assert.Contains("STARTUP_WITH_LARGE_VOLUME", flags);
        Assert.True(json.RootElement.GetProperty("plausibilityScore").GetInt32() < 50);
    }

    [Fact]
    public async Task BankStatement_upload_returns_cash_flow_analysis()
    {
        const string csv = """
            Date,Description,Debit,Credit,Balance
            2024-01-02,STRIPE TRANSFER,,4200.00,14200.00
            2024-01-03,PAYROLL GUSTO,3100.00,,11100.00
            2024-02-01,STRIPE TRANSFER,,5100.00,16200.00
            """;
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(csv)), "file", "statement.csv");
        var response = await _client.PostAsync("/api/underwriting/bank-statement", form);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(3, json.RootElement.GetProperty("transactionCount").GetInt32());
        Assert.Contains(json.RootElement.GetProperty("detectedProcessors").EnumerateArray(), p => p.GetString() == "Stripe");
    }

    [Fact]
    public async Task BankStatement_csv_inline_works()
    {
        var response = await _client.PostAsJsonAsync("/api/underwriting/bank-statement/csv", new
        {
            csv = "Date,Description,Amount\n2024-01-02,SQUARE INC,1000\n2024-01-05,RENT,-500\n"
        });
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1000, json.RootElement.GetProperty("totalInflows").GetDecimal());
        Assert.Equal(500, json.RootElement.GetProperty("totalOutflows").GetDecimal());
    }

    [Fact]
    public async Task BankStatement_invalid_pdf_returns_400()
    {
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("definitely not a pdf")), "file", "statement.pdf");
        var response = await _client.PostAsync("/api/underwriting/bank-statement", form);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task FinancialStatement_text_returns_ratios_and_flags()
    {
        var response = await _client.PostAsJsonAsync("/api/underwriting/financial-statement/text", new
        {
            text = "Revenue,1000000\nCost of goods sold,950000\nNet income,-20000\n",
            declaredAnnualCardVolume = 3000000
        });
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var flags = json.RootElement.GetProperty("flags").EnumerateArray().Select(f => f.GetProperty("code").GetString()).ToList();
        Assert.Contains("LOSS_MAKING", flags);
        Assert.Contains("THIN_GROSS_MARGIN", flags);
        Assert.Contains("CARD_VOLUME_EXCEEDS_REVENUE", flags);
    }

    [Fact]
    public async Task FinancialStatement_upload_parses_csv_file()
    {
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("Revenue,500000\nNet Income,50000\n")), "file", "pnl.csv");
        var response = await _client.PostAsync("/api/underwriting/financial-statement", form);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(500000, json.RootElement.GetProperty("statement").GetProperty("revenue").GetDecimal());
    }
}
