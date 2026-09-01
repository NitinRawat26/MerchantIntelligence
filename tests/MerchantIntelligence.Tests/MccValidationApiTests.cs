using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MerchantIntelligence.MccValidation.Web;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace MerchantIntelligence.Tests;

public sealed class MccValidationApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    /// <summary>Serves a canned restaurant homepage for any host so tests never hit the network.</summary>
    private sealed class FakeWebsiteHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.Host == "down.example")
                throw new HttpRequestException("connection refused");

            const string html = """
                <html><head><title>Luigi's Trattoria</title>
                <script type="application/ld+json">{"@type":"Restaurant"}</script></head>
                <body><h1>Luigi's Restaurant</h1><p>View our menu: pizza, pasta, dining, reservations, cafe, grill.</p></body></html>
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(html, System.Text.Encoding.UTF8, "text/html")
            });
        }
    }

    private readonly HttpClient _client;

    public MccValidationApiTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
                s.AddHttpClient(WebsiteContentFetcher.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => new FakeWebsiteHandler())))
            .CreateClient();
    }

    [Fact]
    public async Task Validate_returns_consistent_verdict_with_evidence()
    {
        var response = await _client.PostAsJsonAsync("/api/mcc-validation/validate", new { mcc = 5812, websiteUrl = "luigis.example" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;

        Assert.Equal(5812, root.GetProperty("declaredMcc").GetInt32());
        Assert.Equal("Consistent", root.GetProperty("verdict").GetString());
        Assert.True(root.GetProperty("accuracyPercent").GetDouble() > 50);
        Assert.Equal(5812, root.GetProperty("suggestedMccs")[0].GetProperty("mcc").GetInt32());
        Assert.True(root.GetProperty("evidence").GetArrayLength() >= 3);
        Assert.StartsWith("https://luigis.example", root.GetProperty("pagesAnalyzed")[0].GetString());
    }

    [Fact]
    public async Task Validate_flags_mismatch_for_wrong_mcc()
    {
        var response = await _client.PostAsJsonAsync("/api/mcc-validation/validate", new { mcc = 7372, websiteUrl = "https://luigis.example" });

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var verdict = json.RootElement.GetProperty("verdict").GetString();
        Assert.True(verdict is "Inconsistent" or "Questionable", verdict);
        Assert.Equal(5812, json.RootElement.GetProperty("suggestedMccs")[0].GetProperty("mcc").GetInt32());
    }

    [Fact]
    public async Task Validate_unreachable_site_is_insufficient_not_500()
    {
        var response = await _client.PostAsJsonAsync("/api/mcc-validation/validate", new { mcc = 5812, websiteUrl = "https://down.example" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Insufficient", json.RootElement.GetProperty("verdict").GetString());
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://example.com")]
    [InlineData("localhost")]
    public async Task Validate_rejects_bad_url(string url)
    {
        var response = await _client.PostAsJsonAsync("/api/mcc-validation/validate", new { mcc = 5812, websiteUrl = url });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Validate_rejects_bad_mcc()
    {
        var response = await _client.PostAsJsonAsync("/api/mcc-validation/validate", new { mcc = 0, websiteUrl = "https://example.com" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Catalog_lists_mccs()
    {
        var response = await _client.GetAsync("/api/mcc-validation/catalog");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.GetArrayLength() > 100);
        Assert.Equal("Low", json.RootElement[0].GetProperty("riskTier").GetString());
    }
}
