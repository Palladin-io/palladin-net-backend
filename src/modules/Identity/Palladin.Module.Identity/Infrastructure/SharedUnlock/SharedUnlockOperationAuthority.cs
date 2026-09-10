using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Module.Identity.Shared;

namespace Palladin.Module.Identity.Infrastructure.SharedUnlock;

internal sealed record SharedUnlockOperationAuthority(User User, RefreshToken SourceSession,
    SharedUnlockAuthorization Source, OrganizationMember SourceMember, OrganizationMember TargetMember,
    Organization SourceOrganization, Organization TargetOrganization, SharedUnlockLink Link,
    SharedUnlockKeyContext KeyContext)
{
    internal static async Task<SharedUnlockOperationAuthority?> LoadAsync(IdentityDomainWriteContext context,
        RefreshToken session, Guid authorizationId, Guid targetOrganizationId, Guid linkId, uint epoch,
        uint preferenceRevision, byte[] sourceGeneration, Instant now, CancellationToken ct)
    {
        if (!session.IsActive(now))
        {
            return null;
        }
        var user = await context.Users.Include(user => user.TotpCredential)
            .SingleOrDefaultAsync(user => user.Id == session.UserId, ct);
        var sessionId = session.SessionId ?? session.Id;
        var source = await context.SharedUnlockAuthorizations.SingleOrDefaultAsync(source =>
            source.UserId == session.UserId && source.SessionId == sessionId, ct);
        if (user is null || source is null || source.Id != authorizationId
            || user.SharedUnlockRevision != preferenceRevision
            || source.LinkId != linkId || source.LinkEpoch != epoch
            || !source.SourceGeneration.AsSpan().SequenceEqual(sourceGeneration))
        {
            return null;
        }
        var members = await context.OrganizationMembers.Include(member => member.RoleAssignments)
            .ThenInclude(assignment => assignment.Role)
            .Where(member => member.UserId == user.Id
                && (member.OrganizationId == session.OrganizationId || member.OrganizationId == targetOrganizationId))
            .ToListAsync(ct);
        var sourceMember = members.SingleOrDefault(member => member.OrganizationId == session.OrganizationId);
        var targetMember = members.SingleOrDefault(member => member.OrganizationId == targetOrganizationId);
        if (sourceMember is null || targetMember is not { Status: OrganizationMemberStatus.Active }
            || !source.IsCurrent(user, session, sourceMember, user.TotpCredential, now))
        {
            return null;
        }
        var organizations = await context.Organizations.Where(organization =>
            organization.Id == session.OrganizationId || organization.Id == targetOrganizationId).ToListAsync(ct);
        var sourceOrganization = organizations.SingleOrDefault(organization => organization.Id == session.OrganizationId);
        var targetOrganization = organizations.SingleOrDefault(organization => organization.Id == targetOrganizationId);
        var link = await context.SharedUnlockLinks.SingleOrDefaultAsync(link =>
            link.UserId == user.Id && link.Id == linkId, ct);
        var keyContext = SharedUnlockKeyContextDigest.FromUser(user);
        if (sourceOrganization is null || targetOrganization is null || keyContext is null || link is null
            || !link.AllowsTransfer(epoch) || source.Sequence <= link.LastInvalidationSequence)
        {
            return null;
        }
        return new SharedUnlockOperationAuthority(user, session, source, sourceMember, targetMember,
            sourceOrganization, targetOrganization, link, keyContext);
    }

    internal static async Task<SharedUnlockOperationAuthority?> LoadAsync(IdentityDomainWriteContext context,
        SharedUnlockOperation operation, Instant now, CancellationToken ct)
    {
        if (now < operation.IssuedAt || now >= operation.ExpiresAt)
        {
            return null;
        }
        var session = await context.RefreshTokens.SingleOrDefaultAsync(token =>
            token.UserId == operation.UserId && token.Id == operation.SourceRefreshTokenId, ct);
        if (session is null)
        {
            return null;
        }
        var authority = await LoadAsync(context, session, operation.SourceAuthorizationId,
            operation.OrganizationId, operation.LinkId, operation.LinkEpoch, operation.PreferenceRevision,
            operation.SourceGeneration, now, ct);
        if (authority is null || authority.Source.SessionId != operation.SourceSessionId
            || authority.Source.Sequence != operation.SourceSequence
            || authority.SourceOrganization.Id != operation.SourceOrganizationId
            || authority.SourceOrganization.OfflineAccessPolicyVersion != operation.SourceOfflinePolicyVersion
            || authority.TargetOrganization.OfflineAccessPolicyVersion != operation.OfflinePolicyVersion
            || authority.TargetMember.AuthorizationVersion != operation.AuthorizationVersion
            || authority.Source.UnlockedAt != operation.UnlockedAt
            || operation.IdleDeadline > authority.Source.IdleDeadline
            || operation.AbsoluteDeadline > authority.Source.AbsoluteDeadline
            || operation.OfflineDeadline > authority.Source.OfflineDeadline
            || !SharedUnlockKeyContextDigest.Hash(authority.KeyContext).AsSpan().SequenceEqual(operation.KeyContextDigest))
        {
            return null;
        }
        return authority;
    }

    internal void Fence(IdentityDomainWriteContext context)
    {
        context.MarkPropertyAsUpdated(User, user => user.SharedUnlockSequence);
        context.MarkPropertyAsUpdated(SourceSession, session => session.RevokedAt);
        context.MarkPropertyAsUpdated(Source, source => source.Sequence);
        context.MarkPropertyAsUpdated(SourceMember, member => member.Status);
        context.MarkPropertyAsUpdated(TargetMember, member => member.Status);
        context.MarkPropertyAsUpdated(SourceOrganization, organization => organization.OfflineAccessPolicyVersion);
        context.MarkPropertyAsUpdated(TargetOrganization, organization => organization.OfflineAccessPolicyVersion);
        context.MarkPropertyAsUpdated(Link, link => link.Revision);
        if (User.TotpCredential is { } factor)
        {
            context.MarkPropertyAsUpdated(factor, factor => factor.ConfigurationRevision);
        }
    }
}
