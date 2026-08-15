using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Core.Guid;
using Palladin.Module.Agents.Domain;
using Palladin.Module.Agents.Infrastructure.AgentAuth;
using Palladin.Module.Agents.Infrastructure.Persistence;
using Palladin.Core.Security;

namespace Palladin.Module.Agents.Features;

[PublicAPI]
public sealed record SubmitFormDiscoveryMapRequest(
    string Domain,
    string LoginUrl,
    string Provider,
    string Fingerprint,
    FormDiscoveryMapDefinition? Map);

[PublicAPI]
public sealed record SubmitFormDiscoveryMapResponse(Guid MapId, int MapVersion, string Status, Instant CreatedAt);

[UsedImplicitly]
internal sealed class SubmitFormDiscoveryMapValidator : Validator<SubmitFormDiscoveryMapRequest>
{
    public SubmitFormDiscoveryMapValidator()
    {
        RuleFor(x => x.Domain).NotEmpty();
        RuleFor(x => x.LoginUrl).NotEmpty();
        RuleFor(x => x.Provider).NotEmpty();
        RuleFor(x => x.Fingerprint).NotEmpty().Matches("^[a-fA-F0-9]{64}$");
        RuleFor(x => x.Map).NotNull();
    }
}

internal sealed class SubmitFormDiscoveryMapEndpoint(
    AgentsDomainReadContext readContext,
    AgentsDomainWriteContext writeContext,
    FormDiscoveryMapContract contract,
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
        if (agentId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        if (!await readContext.Agents.AnyAsync(agent => agent.Id == agentId, ct))
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        if (!contract.TryValidate(
                req.Domain,
                req.LoginUrl,
                req.Provider,
                req.Map,
                out var validated)
            || !contract.FingerprintMatches(validated, req.Fingerprint))
        {
            AddError(r => r.Map, "The map contains unsupported or unsafe fields.");
            await Send.ErrorsAsync(400, ct);
            return;
        }

        var now = clock.GetCurrentInstant();
        await using var transaction = await writeContext.BeginTransactionAsync(ct);
        var mapVersion = await writeContext.LockAndLoadNextFormDiscoveryMapVersionAsync(
            validated.Domain,
            validated.Provider,
            ct);
        var normalizedFingerprint = req.Fingerprint.ToLowerInvariant();
        var existing = await writeContext.FormDiscoveryMaps
            .Where(map => map.Domain == validated.Domain
                && map.Provider == validated.Provider
                && map.Fingerprint == normalizedFingerprint)
            .OrderByDescending(map => map.MapVersion)
            .FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            await writeContext.CommitAsync(transaction, ct);
            await Send.OkAsync(new SubmitFormDiscoveryMapResponse(
                existing.Id,
                existing.MapVersion,
                existing.Status.ToString().ToLowerInvariant(),
                existing.CreatedAt), ct);
            return;
        }

        var map = FormDiscoveryMap.CreateCandidate(
            guidProvider.Generate(),
            agentId.Value,
            validated.Domain,
            validated.LoginUrl,
            validated.Provider,
            normalizedFingerprint,
            validated.DefinitionJson,
            mapVersion,
            now);
        writeContext.Add(map);
        await writeContext.CommitAsync(transaction, ct);
        await Send.OkAsync(new SubmitFormDiscoveryMapResponse(map.Id, map.MapVersion, "candidate", now), ct);
    }
}

[PublicAPI]
public sealed record GetFormDiscoveryMapResponse(
    Guid MapId,
    int MapVersion,
    string Domain,
    string LoginUrl,
    string Provider,
    string Fingerprint,
    FormDiscoveryMapDefinition Map,
    Instant UpdatedAt);

internal sealed class GetFormDiscoveryMapEndpoint(
    AgentsDomainReadContext readContext,
    FormDiscoveryMapContract contract) : EndpointWithoutRequest<GetFormDiscoveryMapResponse>
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
        if (agentId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        if (!contract.TryNormalizeDomain(Route<string>("domain"), out var domain)
            || !contract.TryNormalizeProvider(
                HttpContext.Request.Query["provider"].ToString(),
                out var provider))
        {
            await Send.ErrorsAsync(400, ct);
            return;
        }

        if (!await readContext.Agents.AnyAsync(agent => agent.Id == agentId, ct))
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var publication = await contract.FindVerifiedAsync(
            readContext.FormDiscoveryMaps,
            domain,
            provider,
            ct);
        if (publication is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var map = publication.Map;
        await Send.OkAsync(new GetFormDiscoveryMapResponse(
            map.Id,
            map.MapVersion,
            map.Domain,
            map.LoginUrl,
            map.Provider,
            map.Fingerprint,
            publication.Definition,
            map.UpdatedAt), ct);
    }
}
