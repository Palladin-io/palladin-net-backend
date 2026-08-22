namespace Palladin.Module.Identity.Infrastructure.MassTransit;

internal static class IdentityEndpoints
{
    internal const string Self = "identity.events.self";
    internal const string FromVault = "identity.events.vault";
    internal const string Onboarding = "identity.commands.onboarding";
}
