using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MerchantIntelligence.CreditDecision;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MerchantIntelligence.Tests;

public sealed class CreditDecisionApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private sealed class FakePredictor : IDecisionPredictor
    {
        public DecisionResult Predict(MerchantApplication application) =>
            new(Decision.Approved, 0.9123, new Dictionary<Decision, double>
            {
                [Decision.Approved] = 0.9123,
                [Decision.Cancelled] = 0.0377,
                [Decision.Declined] = 0.05
            });
    }

    private readonly HttpClient _client;

    public CreditDecisionApiTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.WithWebHostBuilder(b =>
            b.ConfigureServices(s => s.Replace(ServiceDescriptor.Singleton<IDecisionPredictor, FakePredictor>())))
            .CreateClient();
    }

    [Fact]
    public async Task Predict_returns_decision_and_percentages()
    {
        var response = await _client.PostAsJsonAsync("/api/credit-decision/predict", new
        {
            merchantCategoryCode = 5411,
            annualVolume = 500000,
            averageTicket = 45,
            highestTicket = 300,
            matchFound = false,
            existingRelationship = true
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        Assert.Equal("Approved", root.GetProperty("decision").GetString());
        Assert.Equal(91.23, root.GetProperty("confidencePercent").GetDouble());
        Assert.Equal(5.0, root.GetProperty("probabilitiesPercent").GetProperty("Declined").GetDouble());
    }

    [Fact]
    public async Task Predict_rejects_highest_ticket_below_average()
    {
        var response = await _client.PostAsJsonAsync("/api/credit-decision/predict", new
        {
            merchantCategoryCode = 5411, annualVolume = 500000, averageTicket = 450, highestTicket = 300
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Predict_rejects_invalid_mcc()
    {
        var response = await _client.PostAsJsonAsync("/api/credit-decision/predict", new
        {
            merchantCategoryCode = 0, annualVolume = 500000, averageTicket = 45, highestTicket = 300
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
