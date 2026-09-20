using JetBrains.Annotations;

namespace Palladin.Module.Notification.Contracts.ValueObjects;

// Template names are plain strings so adding a template never touches a shared enum — the name maps
// 1:1 to the .liquid resource base name in the Notification module. Publish with these constants,
// never inline literals; an unknown name fails at render time with EmailTemplateNotFoundException.
[PublicAPI]
public static class EmailTemplates
{
    public const string EmailVerification = "email-verification";
    public const string BetaInvitation = "beta-invitation";
    public const string SecurityAlert = "security-alert";
    public const string LoginLockoutAlert = "login-lockout-alert";
    public const string WaitlistVerification = "waitlist-verification";
    public const string WaitlistDeveloperBenefitActivated = "waitlist-developer-benefit-activated";
    public const string OrganizationInvitation = "organization-invitation";
    public const string EntryShareVerification = "entry-share-verification";
}
