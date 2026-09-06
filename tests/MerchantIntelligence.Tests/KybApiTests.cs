using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MerchantIntelligence.Kyb.Registry;
using MerchantIntelligence.MccValidation.Web;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace MerchantIntelligence.Tests;

public sealed class KybApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private sealed class FakeSiteHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            string html = path switch
            {
                "/privacy" => "<html><body><h1>Privacy Policy</h1>" + new string('x', 1200) + " we collect personal data cookies</body></html>",
                "/terms" => "<html><body><h1>Terms and Conditions</h1>" + new string('x', 1200) + " governing law</body></html>",
                "/refunds" => "<html><body><h1>Refund Policy</h1>Returns accepted within 30 days for a full refund. " + new string('y', 600) + "</body></html>",
                _ => """
                    <html><head><title>Good Shoes Ltd</title></head><body>
                    <h1>Good Shoes Ltd - Handmade leather shoes</h1>
                    <p>All prices in USD $. We accept Visa, Mastercard and American Express.</p>
                    <p>Contact us: support@goodshoes.example, +1 555 010 0100, 12 High Street, Springfield</p>
                    <p>We ship worldwide except embargoed countries.</p>
                    <a href="/shop">Shop</a> <a href="/cart">Cart</a> <a href="/checkout">Checkout</a>
                    <a href="/privacy">Privacy Policy</a> <a href="/terms">Terms &amp; Conditions</a> <a href="/refunds">Refund Policy</a>
                    </body></html>
                    """
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(html, System.Text.Encoding.UTF8, "text/html")
            });
        }
    }

    /// <summary>RDAP / registry stand-in; returns a plausible domain record for every lookup.</summary>
    private sealed class FakeRdapHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            const string json = """
                {"ldhName":"goodshoes.example","status":["client transfer prohibited"],
                 "events":[{"eventAction":"registration","eventDate":"2012-04-01T00:00:00Z"},{"eventAction":"expiration","eventDate":"2032-04-01T00:00:00Z"}],
                 "entities":[{"roles":["registrar"],"vcardArray":["vcard",[["fn",{},"text","Example Registrar"]]]}]}
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    private readonly HttpClient _client;

    public KybApiTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
            {
                s.AddHttpClient(WebsiteContentFetcher.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new FakeSiteHandler());
                s.AddHttpClient(KybOptions.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new FakeRdapHandler());
            }))
            .CreateClient();
    }

    [Fact]
    public async Task Website_compliance_scores_compliant_site()
    {
        var response = await _client.PostAsJsonAsync("/api/kyb/website-compliance",
            new { websiteUrl = "goodshoes.example", legalName = "Good Shoes Ltd", declaredMcc = 5661 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;

        Assert.True(root.GetProperty("score").GetInt32() >= 75, root.ToString());
        var checks = root.GetProperty("checks").EnumerateArray().ToDictionary(c => c.GetProperty("code").GetString()!, c => c.GetProperty("status").GetString());
        Assert.Equal("Pass", checks["PRIVACY_POLICY"]);
        Assert.Equal("Pass", checks["TERMS_CONDITIONS"]);
        Assert.Equal("Pass", checks["REFUND_POLICY"]);
        Assert.Equal("Pass", checks["CUSTOMER_SERVICE_CONTACT"]);
        Assert.Equal("Pass", checks["LEGAL_NAME_DISCLOSED"]);
        Assert.Equal("Pass", checks["DOMAIN_AGE"]);
        Assert.Equal("Acceptable", root.GetProperty("prohibitedBusiness").GetProperty("verdict").GetString());
    }

    [Fact]
    public async Task Website_compliance_rejects_bad_url()
    {
        var response = await _client.PostAsJsonAsync("/api/kyb/website-compliance", new { websiteUrl = "not a url" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Prohibited_business_endpoint_classifies_text()
    {
        var response = await _client.PostAsJsonAsync("/api/kyb/prohibited-business",
            new { text = "Online casino with live dealer blackjack, slots and sports betting. Deposit bonus for new players.", declaredMcc = 7995 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Restricted", json.RootElement.GetProperty("verdict").GetString());
        Assert.Equal("GAMBLING", json.RootElement.GetProperty("matches")[0].GetProperty("category").GetProperty("code").GetString());

        var categories = await _client.GetFromJsonAsync<JsonElement>("/api/kyb/prohibited-business/categories");
        Assert.True(categories.GetArrayLength() >= 20);
    }
}
