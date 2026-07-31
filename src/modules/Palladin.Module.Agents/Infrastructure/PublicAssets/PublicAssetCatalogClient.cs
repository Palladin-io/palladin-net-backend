using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using Palladin.Core.Guid;

namespace Palladin.Module.Agents.Infrastructure.PublicAssets;

internal sealed class PublicAssetCatalogClientOptions
{
    internal const string Position = "Modules:Agents:PublicAssetCatalog";
    public string BaseUrl { get; init; } = "http://localhost:5000";
}

internal sealed record AgentIconUpload(Guid AssetId, Guid UploadSessionId, string UploadUrl, long MaximumBytes);
internal sealed record AgentIconAsset(Guid AssetId, string PublicUrl, int Revision);

internal interface IPublicAssetCatalogClient
{
    Task<AgentIconUpload> CreateAgentIconUploadAsync(Guid organizationId, Guid agentId, string mediaType, long byteLength, string sha256, CancellationToken ct);
    Task<AgentIconAsset> CompleteAgentIconUploadAsync(Guid organizationId, Guid agentId, Guid uploadSessionId, CancellationToken ct);
}

internal sealed class PublicAssetCatalogClient(HttpClient http, IOptions<PublicAssetCatalogClientOptions> configured, IConfiguration configuration, IGuidProvider ids) : IPublicAssetCatalogClient
{
    private readonly PublicAssetCatalogClientOptions _options = configured.Value;
    private readonly string _signingSecret = ValidatedSecret(configuration);

    public async Task<AgentIconUpload> CreateAgentIconUploadAsync(Guid organizationId, Guid agentId, string mediaType, long byteLength, string sha256, CancellationToken ct)
    {
        using var request = Authorized(HttpMethod.Post, "api/public-assets/uploads");
        request.Content = JsonContent.Create(new { type = "agentIcon", name = $"Agent {agentId:N} icon", hostnames = Array.Empty<string>(), aliases = (string[]?)null, mediaType, byteLength, sha256, organizationId, ownerId = agentId });
        using var response = await http.SendAsync(request, ct); response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct)); var root = json.RootElement;
        return new(root.GetProperty("assetId").GetGuid(), root.GetProperty("uploadSessionId").GetGuid(), root.GetProperty("uploadUrl").GetString()!, root.GetProperty("maximumBytes").GetInt64());
    }

    public async Task<AgentIconAsset> CompleteAgentIconUploadAsync(Guid organizationId, Guid agentId, Guid uploadSessionId, CancellationToken ct)
    {
        using var request = Authorized(HttpMethod.Post, $"api/public-assets/uploads/{uploadSessionId}/complete");
        request.Content = JsonContent.Create(new { uploadSessionId, organizationId, ownerId = agentId });
        using var response = await http.SendAsync(request, ct); response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct)); var root = json.RootElement;
        return new(root.GetProperty("id").GetGuid(), root.GetProperty("url").GetString()!, root.GetProperty("revision").GetInt32());
    }

    private HttpRequestMessage Authorized(HttpMethod method, string path)
    {
        var now = DateTimeOffset.UtcNow;
        var claims = new[] { new Claim(JwtRegisteredClaimNames.Sub, "agents"), new Claim("role", "PublicAssetCatalogService"), new Claim(JwtRegisteredClaimNames.Jti, ids.Generate().ToString("N")), new Claim(JwtRegisteredClaimNames.Iat, now.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64) };
        var token = new JwtSecurityToken("palladin-internal", "public-asset-catalog", claims, now.UtcDateTime, now.AddSeconds(60).UtcDateTime,
            new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_signingSecret)), SecurityAlgorithms.HmacSha256));
        var request = new HttpRequestMessage(method, new Uri(new Uri(_options.BaseUrl.TrimEnd('/') + "/"), path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return request;
    }

    private static string ValidatedSecret(IConfiguration configuration)
    {
        var secret = configuration["Modules:PublicAssetCatalog:ServiceAuth:SigningSecret"] ?? string.Empty;
        return Encoding.UTF8.GetByteCount(secret) >= 32
            ? secret
            : throw new InvalidOperationException("Public Asset Catalog service signing secret must contain at least 32 UTF-8 bytes.");
    }
}
