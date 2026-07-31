namespace Palladin.Module.Identity.Infrastructure.MassTransit;

internal static class IdentityEndpoints
{
    internal const string Self = "identity.events.self";
    internal const string Onboarding = "identity.commands.onboarding";
    internal const string Audit = "identity.audit";
    internal const string Search = "identity.search";
}
