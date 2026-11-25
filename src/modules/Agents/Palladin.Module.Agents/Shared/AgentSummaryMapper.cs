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
    string EncryptionPublicKey,
    string EncryptionKeySuiteId,
    byte[]? PreviousEncryptionKeyFingerprint,
    byte[]? EncryptionKeyBindingSignature,
    uint RecipientKeyVersion,
    uint AccessEpoch,
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
            x.EncryptionPublicKey,
            x.EncryptionKeySuiteId,
            x.PreviousEncryptionKeyFingerprint,
            x.EncryptionKeyBindingSignature,
            x.RecipientKeyVersion,
            x.AccessEpoch,
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

    // The prefix/suffix hint is the stable X-Agent-Key identifier. Only the agent-detail endpoint returns the full
    // encryption key publication (key, suite, and the Ed25519-signed rotation binding) that clients verify before
    // sealing to it; the list keeps only the suite.
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
            includeFullPublicKey ? p.EncryptionPublicKey : null,
            p.EncryptionKeySuiteId,
            includeFullPublicKey && p.PreviousEncryptionKeyFingerprint is { } previous
                ? Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(previous)
                : null,
            includeFullPublicKey && p.EncryptionKeyBindingSignature is { } signature
                ? Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(signature)
                : null,
            p.RecipientKeyVersion,
            p.AccessEpoch,
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
