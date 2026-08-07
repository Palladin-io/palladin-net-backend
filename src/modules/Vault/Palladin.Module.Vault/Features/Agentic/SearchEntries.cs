using Palladin.Core.Api;
using Palladin.Core.Events;
using Palladin.Core.Types;
using Palladin.Module.Agents.Infrastructure.AgentAuth;
using Palladin.Module.Vault.Contracts.Events;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Shared;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record SearchEntriesRequest
{
    public string? Query { get; init; }
    public string? Cursor { get; init; }
    public int? PageSize { get; init; }
}

// Metadata only — discovery layer, never delivery. No EncryptedBlob/Nonce/DEK or any crypto field.
// VaultId is included so the agent can target request-access at the right vault. AgentFields are
// custom fields the owner explicitly marked agent-visible — metadata, never secrets.
[PublicAPI]
public sealed record EntryDiscoveryItem(
    Guid EntryId,
    Guid VaultId,
    string Label,
    EntryType Type,
    string? UrlDomain,
    string? Description,
    IReadOnlyList<AgentField>? AgentFields);

[PublicAPI]
public sealed record SearchEntriesResponse(IReadOnlyList<EntryDiscoveryItem> Items, string? NextCursor);

[UsedImplicitly]
internal sealed class SearchEntriesValidator : Validator<SearchEntriesRequest>
{
    public SearchEntriesValidator()
    {
        // Minimum query length prevents a "dump all" enumeration on an empty/too-short query.
        RuleFor(x => x.Query).NotEmpty().MinimumLength(2);
        RuleFor(x => x.PageSize!.Value).InclusiveBetween(1, 25).When(x => x.PageSize is not null);
    }
}

[PublicAPI]
internal sealed class SearchEntriesEndpoint(
    VaultDomainReadContext domainReadContext,
    IEnumerable<IEventPublisher> eventPublishers,
    IClock clock) : Endpoint<SearchEntriesRequest, SearchEntriesResponse>
{
    private const int DefaultPageSize = 10;
    private const int MaxPageSize = 25;

    public override void Configure()
    {
        Get("api/agent/entries");
        AuthSchemes(AgentAuthenticationOptions.SchemeName);
        Summary(summary =>
        {
            summary.Summary = "Agent entry discovery (org-wide, metadata only)";
            summary.Description = "Returns metadata (entryId, vaultId, label, type, urlDomain, description) for entries across every vault in the agent's own organization that match the query. The agent does not need to know the vault — it discovers an entry, then uses the returned vaultId to request access. No ciphertext or secrets are ever returned — this is the discovery step before request-access. Authenticated via X-Api-Key + X-Agent-Key.";
        });
        Tags("Vault/Entries");
    }

    public override async Task HandleAsync(SearchEntriesRequest req, CancellationToken ct)
    {
        var agentId = User.GetAgentId();
        var organizationId = User.GetAgentOrganizationId();
        var accessEpoch = User.GetAgentAccessEpoch();
        if (agentId is null || organizationId is null || accessEpoch is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var agent = await domainReadContext.Agents
            .Where(a => a.Id == agentId.Value
                        && a.OrganizationId == organizationId.Value
                        && a.AccessEpoch == accessEpoch.Value)
            .Select(a => new { a.OrganizationId, a.Status })
            .FirstOrDefaultAsync(ct);
        if (agent is null || agent.Status != AgentStatus.Active)
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        var items = new List<EntryDiscoveryItem>();

        // Analytics flow per CLAUDE.md: domain event -> MassTransit trigger -> analytics publish.
        // Never call analyticsService.CaptureEvent directly from an endpoint.
        foreach (var publisher in eventPublishers)
        {
            await publisher.PublishAsync(
                new EntryDiscoveryRequestedEvent(agentId.Value, agent.OrganizationId, items.Count, clock.GetCurrentInstant()),
                ct);
        }

        await Send.OkAsync(new SearchEntriesResponse(items, null), ct);
    }
}
