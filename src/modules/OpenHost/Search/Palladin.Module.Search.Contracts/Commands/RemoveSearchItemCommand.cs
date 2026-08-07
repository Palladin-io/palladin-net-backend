using Palladin.Core.Events;
using JetBrains.Annotations;
using NodaTime;

namespace Palladin.Module.Search.Contracts.Commands;

// OpenHost command: an owning module removes one tenant-owned administrative catalog item.
[PublicAPI]
public sealed record RemoveSearchItemCommand(
    Guid OrganizationId,
    Guid ItemId,
    string Type,
    Instant OccurredAt) : IIntegrationCommand;
