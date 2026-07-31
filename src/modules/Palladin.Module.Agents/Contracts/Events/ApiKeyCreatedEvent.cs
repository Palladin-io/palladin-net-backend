using Palladin.Core.Events;
using JetBrains.Annotations;
using NodaTime;

namespace Palladin.Module.Agents.Contracts.Events;

// API keys grant org-wide access. Only non-secret metadata is ever carried — never the plaintext key
// or its hash; KeySuffix is the same last-4 hint already shown in the UI as `pl_••••{suffix}`.
[PublicAPI]
public sealed record ApiKeyCreatedEvent(
    Guid ApiKeyId,
    Guid OrganizationId,
    string Name,
    string KeySuffix,
    Guid CreatedBy,
    string CreatedByName,
    Instant CreatedAt) : IIntegrationEvent;
