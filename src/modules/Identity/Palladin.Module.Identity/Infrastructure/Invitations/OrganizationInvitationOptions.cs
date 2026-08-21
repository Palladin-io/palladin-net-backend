namespace Palladin.Module.Identity.Infrastructure.Invitations;

internal sealed class OrganizationInvitationOptions
{
    public const string Position = "Modules:Identity:OrganizationInvitations";

    public int TokenTtlHours { get; init; } = 72;
    public int ResendCooldownSeconds { get; init; } = 60;
    public string AcceptanceUrlBase { get; init; } = string.Empty;
}
