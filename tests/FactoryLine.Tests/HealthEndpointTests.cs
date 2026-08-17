using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace FactoryLine.Tests;

public class HealthEndpointTests : IClassFixture<FactoryLineAppFactory>
{
    private readonly HttpClient _client;

    public HealthEndpointTests(FactoryLineAppFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Health_ReturnsOk()
    {
        var response = await _client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Dashboard_ReturnsOk()
    {
        var response = await _client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
