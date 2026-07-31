using Palladin.Core.Events;
using Palladin.Core.Types;
using JetBrains.Annotations;

namespace Palladin.Module.Identity.Contracts.Commands;

// OpenHost-style command: an owning module (Vault entry-created, Agents api-key-created / agent-enrolled,
// Notification mobile-registered) tells Identity that a user reached an onboarding milestone. Identity
// stores it as materialized per-user state — GetAccount reads that state, never a cross-module query.
[PublicAPI]
public sealed record MarkOnboardingStepCommand(Guid UserId, OnboardingStep Step) : IIntegrationCommand;
