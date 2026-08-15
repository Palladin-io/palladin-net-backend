using System.Text.Json;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Core.Guid;
using Palladin.Module.Agents.Domain;
using Palladin.Module.Agents.Infrastructure.AgentAuth;
using Palladin.Module.Agents.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Palladin.Core.Security;

namespace Palladin.Module.Agents.Features;

[PublicAPI]
public sealed record SubmitFormDiscoveryMapRequest(
    string Domain,
    string LoginUrl,
    string Provider,
    string Fingerprint,
    JsonElement Map);

[PublicAPI]
public sealed record SubmitFormDiscoveryMapResponse(Guid MapId, int MapVersion, string Status, Instant CreatedAt);

[UsedImplicitly]
internal sealed class SubmitFormDiscoveryMapValidator : Validator<SubmitFormDiscoveryMapRequest>
{
    public SubmitFormDiscoveryMapValidator()
    {
        RuleFor(x => x.Domain).Must(domain => FormDiscoveryMapContract.TryNormalizeDomain(domain, out _));
        RuleFor(x => x.LoginUrl).NotEmpty().MaximumLength(2048).Must(x => Uri.TryCreate(x, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps);
        RuleFor(x => x.Provider).Must(provider => FormDiscoveryMapContract.TryNormalizeProvider(provider, out _));
        RuleFor(x => x.Fingerprint).NotEmpty().Matches("^[a-fA-F0-9]{64}$");
        RuleFor(x => x.Map).Must(x => x.ValueKind == JsonValueKind.Object);
    }
}

internal sealed class SubmitFormDiscoveryMapEndpoint(
    AgentsDomainReadContext readContext,
    AgentsDomainWriteContext writeContext,
    IGuidProvider guidProvider,
    IClock clock) : Endpoint<SubmitFormDiscoveryMapRequest, SubmitFormDiscoveryMapResponse>
{
    public override void Configure()
    {
        Post("api/agent/form-discovery-maps");
        AuthSchemes(AgentAuthenticationOptions.SchemeName);
        Tags("Agents/DiscoveryMaps");
    }

    public override async Task HandleAsync(SubmitFormDiscoveryMapRequest req, CancellationToken ct)
    {
        var agentId = User.GetAgentId();
        if (agentId is null) { await Send.UnauthorizedAsync(ct); return; }
        var agent = await readContext.Agents.Where(x => x.Id == agentId).Select(x => new { x.OrganizationId }).FirstOrDefaultAsync(ct);
        if (agent is null) { await Send.UnauthorizedAsync(ct); return; }
        if (!FormDiscoveryMapContract.TryNormalizeDomain(req.Domain, out var domain)
            || !FormDiscoveryMapContract.TryNormalizeProvider(req.Provider, out var provider)
            || !FormDiscoveryMapContract.IsSafe(req.Map, domain, req.LoginUrl)
            || !FormDiscoveryMapContract.FingerprintMatches(
                req.Map,
                domain,
                req.LoginUrl,
                req.Fingerprint))
        {
            AddError(r => r.Map, "The map contains unsupported or unsafe fields.");
            await Send.ErrorsAsync(400, ct);
            return;
        }

        var now = clock.GetCurrentInstant();
        await using var transaction = await writeContext.BeginTransactionAsync(ct);
        var mapVersion = await writeContext.LockAndLoadNextFormDiscoveryMapVersionAsync(
            agent.OrganizationId,
            domain,
            provider,
            ct);
        var map = FormDiscoveryMap.CreateCandidate(guidProvider.Generate(), agent.OrganizationId, agentId.Value,
            domain, req.LoginUrl, provider, req.Fingerprint.ToLowerInvariant(), req.Map.GetRawText(), mapVersion, now);
        writeContext.Add(map);
        await writeContext.CommitAsync(transaction, ct);
        await Send.OkAsync(new SubmitFormDiscoveryMapResponse(map.Id, map.MapVersion, "candidate", now), ct);
    }
}

[PublicAPI]
public sealed record GetFormDiscoveryMapResponse(Guid MapId, int MapVersion, string Scope, string Domain, string LoginUrl, string Provider, string Fingerprint, JsonElement Map, Instant UpdatedAt);

internal sealed class GetFormDiscoveryMapEndpoint(AgentsDomainReadContext readContext) : EndpointWithoutRequest<GetFormDiscoveryMapResponse>
{
    public override void Configure()
    {
        Get("api/agent/form-discovery-maps/{domain}");
        AuthSchemes(AgentAuthenticationOptions.SchemeName);
        Tags("Agents/DiscoveryMaps");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var agentId = User.GetAgentId();
        if (agentId is null) { await Send.UnauthorizedAsync(ct); return; }
        if (!FormDiscoveryMapContract.TryNormalizeDomain(Route<string>("domain"), out var domain)
            || !FormDiscoveryMapContract.TryNormalizeProvider(HttpContext.Request.Query["provider"].ToString(), out var provider))
        {
            await Send.ErrorsAsync(400, ct);
            return;
        }
        var agent = await readContext.Agents.Where(x => x.Id == agentId).Select(x => new { x.OrganizationId }).FirstOrDefaultAsync(ct);
        if (agent is null) { await Send.UnauthorizedAsync(ct); return; }
        var map = await FormDiscoveryMapContract.FindVerifiedAsync(
            readContext.FormDiscoveryMaps,
            agent.OrganizationId,
            domain,
            provider,
            ct);
        if (map is null) { await Send.NotFoundAsync(ct); return; }
        using var document = JsonDocument.Parse(map.DefinitionJson);
        await Send.OkAsync(new GetFormDiscoveryMapResponse(map.Id, map.MapVersion, map.Scope == FormDiscoveryMapScope.System ? "system" : "organization", map.Domain, map.LoginUrl, map.Provider, map.Fingerprint, document.RootElement.Clone(), map.UpdatedAt), ct);
    }
}

internal sealed class VerifyFormDiscoveryMapEndpoint(
    AgentsDomainWriteContext writeContext,
    IClock clock) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Put("api/form-discovery-maps/{mapId:guid}/verify");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.AgentManage);
        this.RequireEmailVerified();
        Tags("Agents/DiscoveryMaps");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId();
        var mapId = Route<Guid>("mapId");
        if (organizationId is null) { await Send.UnauthorizedAsync(ct); return; }
        var map = await writeContext.FormDiscoveryMaps.FirstOrDefaultAsync(x => x.Id == mapId && x.OrganizationId == organizationId, ct);
        if (map is null) { await Send.NotFoundAsync(ct); return; }
        map.MarkVerified(clock.GetCurrentInstant());
        await writeContext.CommitAsync(ct);
        await Send.NoContentAsync(ct);
    }
}
