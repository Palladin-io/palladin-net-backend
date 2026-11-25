using JetBrains.Annotations;
using Palladin.Core.Events;

namespace Palladin.Module.Identity.Contracts.Commands;

// Vault owns every envelope addressed to a Member key, so it decides when the wrapped legacy key is no longer
// needed and tells Identity to delete it. Identity never queries Vault.
[PublicAPI]
public sealed record DeleteLegacyMemberKeyCommand(Guid UserId, uint LegacyMemberKeyVersion) : IIntegrationCommand;
