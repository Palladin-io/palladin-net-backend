using JetBrains.Annotations;
using NodaTime;
using Palladin.Core.Events;

namespace Palladin.Module.Vault.Contracts.Commands;

[PublicAPI]
public sealed record RevokeMemberEntrySharingCommand(
    Guid OrganizationId, Guid UserId, uint AuthorizationVersion, Instant UpdatedAt) : IIntegrationCommand;

[PublicAPI]
public sealed record RevokeOrganizationEntrySharingCommand(
    Guid OrganizationId, Instant UpdatedAt) : IIntegrationCommand;

[PublicAPI]
public sealed record MemberEntrySharingRevoked(Guid OrganizationId, Guid UserId, uint AuthorizationVersion);

[PublicAPI]
public sealed record OrganizationEntrySharingRevoked(Guid OrganizationId);
