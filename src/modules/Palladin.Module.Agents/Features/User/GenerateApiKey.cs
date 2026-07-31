using Palladin.Core.Guid;
using Palladin.Core.Security;
using Palladin.Module.Agents.Domain;
using Palladin.Module.Agents.Infrastructure.Persistence;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using NodaTime;

namespace Palladin.Module.Agents.Features;

[PublicAPI]
public sealed record GenerateApiKeyRequest
{
    public string Name { get; init; } = string.Empty;
}

[PublicAPI]
public sealed record GenerateApiKeyResponse(
    Guid ApiKeyId,
    string Name,
    string Plaintext,
    Instant CreatedAt);

[UsedImplicitly]
internal sealed class GenerateApiKeyValidator : Validator<GenerateApiKeyRequest>
{
    public GenerateApiKeyValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
    }
}

[PublicAPI]
internal sealed class GenerateApiKeyEndpoint(
    AgentsDomainReadContext domainReadContext,
    AgentsDomainWriteContext domainWriteContext,
    IGuidProvider guidProvider,
    IClock clock) : Endpoint<GenerateApiKeyRequest, GenerateApiKeyResponse>
{
    public override void Configure()
    {
        Post("api/api-keys");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.WriteApiKey);
        this.RequireEmailVerified();
        Summary(summary =>
        {
            summary.Summary = "Generate an organization API key";
            summary.Description = "Creates an API key for the current organization. The plaintext key is returned only once and cannot be retrieved later — store it securely.";
        });
        Tags("Agents/ApiKeys");
    }

    public override async Task HandleAsync(GenerateApiKeyRequest req, CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId();
        var userId = User.GetUserId();
        if (organizationId is null || userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var actorName = await ApiKeyActor.ResolveActorNameAsync(domainReadContext, userId.Value, ct);

        var (apiKey, plaintext) = ApiKey.Generate(
            guidProvider.Generate(),
            organizationId.Value,
            req.Name.Trim(),
            userId.Value,
            actorName,
            clock.GetCurrentInstant());

        domainWriteContext.Add(apiKey);
        await domainWriteContext.CommitAsync(ct);

        await Send.OkAsync(
            new GenerateApiKeyResponse(apiKey.Id, apiKey.Name, plaintext, apiKey.CreatedAt),
            ct);
    }
}
