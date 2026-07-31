using Palladin.Core.Types;
using Palladin.Module.Agents.Domain;
using Palladin.Module.Agents.Features;
using Palladin.Module.Agents.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Palladin.Module.Agents.Shared;

internal sealed record AgentSummaryProjection(
    Guid Id,
    string? Name,
    string? Description,
    string? Type,
    string? IconKey,
    string? IconColor,
    AgentStatus Status,
    string PublicKey,
    uint RecipientKeyVersion,
    NodaTime.Instant CreatedAt,
    NodaTime.Instant? EnrolledAt,
    Guid? EnrolledBy,
    NodaTime.Instant? DeactivatedAt,
    Guid? DeactivatedBy,
    NodaTime.Instant? ReactivatedAt,
    Guid? ReactivatedBy,
    NodaTime.Instant? LastAccessAt,
    string? LastIp,
    string? LastHostname);

internal static class AgentSummaryMapper
{
    public static IQueryable<AgentSummaryProjection> ToSummaryProjection(this IQueryable<Agent> agents) =>
        agents.Select(x => new AgentSummaryProjection(
            x.Id,
            x.Name,
            x.Description,
            x.Type,
            x.IconKey,
            x.IconColor,
            x.Status,
            x.PublicKey,
            x.RecipientKeyVersion,
            x.CreatedAt,
            x.EnrolledAt,
            x.EnrolledBy,
            x.DeactivatedAt,
            x.DeactivatedBy,
            x.ReactivatedAt,
            x.ReactivatedBy,
            x.LastAccessAt,
            x.LastIp,
            x.LastHostname));

    public static async Task<IReadOnlyDictionary<Guid, string>> LoadUserNamesAsync(
        AgentsDomainReadContext domainReadContext,
        IReadOnlyCollection<AgentSummaryProjection> projections,
        CancellationToken ct)
    {
        var userIds = projections
            .SelectMany(p => new[] { p.EnrolledBy, p.DeactivatedBy, p.ReactivatedBy })
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();

        return await domainReadContext.Users
            .Where(u => userIds.Contains(u.Id))
            .Select(u => new { u.Id, u.DisplayName })
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
    }

    // includeFullPublicKey: only the agent-detail endpoint returns the full X25519 public key (base64),
    // which the client needs to seal a DEK when proactively granting access. The list omits it (null).
    // The public key is not a secret, but keeping it off the list payload limits incidental exposure.
    public static AgentSummary ToSummary(
        this AgentSummaryProjection p,
        IReadOnlyDictionary<Guid, string> userNames,
        bool includeFullPublicKey = false) =>
        new(
            p.Id,
            p.Name,
            p.Description,
            p.Type,
            p.IconKey,
            p.IconColor,
            p.Status,
            AgentPublicKey.Prefix(p.PublicKey),
            AgentPublicKey.Suffix(p.PublicKey),
            includeFullPublicKey ? p.PublicKey : null,
            p.RecipientKeyVersion,
            p.CreatedAt,
            p.EnrolledAt,
            p.EnrolledBy.HasValue ? userNames.GetValueOrDefault(p.EnrolledBy.Value) : null,
            p.DeactivatedAt,
            p.DeactivatedBy.HasValue ? userNames.GetValueOrDefault(p.DeactivatedBy.Value) : null,
            p.ReactivatedAt,
            p.ReactivatedBy.HasValue ? userNames.GetValueOrDefault(p.ReactivatedBy.Value) : null,
            p.LastAccessAt,
            p.LastIp,
            p.LastHostname);
}
