using JetBrains.Annotations;

namespace Palladin.Core.Types;

[PublicAPI]
public enum OnboardingStep
{
    EntryCreated = 1,
    ApiKeyCreated = 2,
    AgentEnrolled = 3,
    MobileRegistered = 4,
}
