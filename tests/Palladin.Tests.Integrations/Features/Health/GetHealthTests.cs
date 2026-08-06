using System.Net;
using System.Net.Http.Json;
using Palladin.Api.Features.Health;
using Palladin.Tests.Integrations.Shared;

namespace Palladin.Tests.Integrations.Features.Health;

[Collection<ApiFactoryCollection>]
public sealed class GetHealthTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task GetHealth_ShouldReturnHealthy()
    {
        // Arrange
        var client = apiFactory.CreateClient();

        // Act
        var response = await client.GetAsync("api/health");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<GetHealthResponse>();
        Assert.NotNull(result);
        Assert.Equal("Healthy", result.Status);
        Assert.StartsWith("https://github.com/Palladin-io/palladin-net-backend/tree/", result.SourceCode);
        Assert.Equal("AGPL-3.0-only", result.License);
    }
}
