namespace Palladin.Module.Vault.Infrastructure.MassTransit;

internal static class VaultEndpoints
{
    internal const string Self = "vault.events.self";
    internal const string FromAgents = "vault.events.agents";
    internal const string FromIdentity = "vault.events.identity";
    internal const string Notification = "vault.notification";
    internal const string Onboarding = "vault.onboarding";
    internal const string Audit = "vault.audit";
}
