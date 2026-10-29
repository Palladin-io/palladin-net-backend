using JetBrains.Annotations;
using NodaTime;
using Palladin.Core.Events;

namespace Palladin.Module.Vault.Contracts.Commands;

// Vault-owned command. Identity reports whether an organization membership is a current approver:
// an Active membership whose effective permissions include GrantManage. UpdatedAt orders the
// membership transitions; Vault keeps the latest state and fails closed on equal-time disagreement.
[PublicAPI]
public sealed record UpsertOrganizationApproverCommand(
    Guid OrganizationId,
    Guid UserId,
    bool IsApprover,
    Instant UpdatedAt) : IIntegrationCommand;
