using Palladin.Core.Events;
using JetBrains.Annotations;
using NodaTime;

namespace Palladin.Module.Search.Contracts.Commands;

// OpenHost command for the server-visible administrative catalog. Only Agent and Member publishers
// are accepted. Vault/Entry presentation data is forbidden by the Search consumer's fail-closed type
// policy. UpdatedAt drives idempotency.
[PublicAPI]
public sealed record IndexSearchItemCommand(
    Guid OrganizationId,
    Guid ItemId,
    string Type,
    string Name,
    IReadOnlyList<string> SearchTerms,
    Instant UpdatedAt) : IIntegrationCommand;
