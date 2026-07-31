using Palladin.Core.Events;
using Palladin.Core.Types;
using JetBrains.Annotations;

namespace Palladin.Module.Identity.Contracts.Commands;

// Org-level onboarding milestone (ApiKeyCreated / AgentEnrolled): once ANYONE in the org reaches it,
// it is done for every user in that org. Published by the owning module's trigger (Agents), consumed
// by Identity into materialized Organization state. Monotonic. (Per-user steps use
// MarkOnboardingStepCommand instead.)
[PublicAPI]
public sealed record MarkOrganizationOnboardingStepCommand(Guid OrganizationId, OnboardingStep Step) : IIntegrationCommand;
