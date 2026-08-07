using Palladin.Core.Security;
using Palladin.Module.Identity.Infrastructure.Persistence;
using FastEndpoints;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.WebUtilities;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
public sealed record OnboardingStepsResponse(bool EntryCreated, bool ApiKeyCreated, bool AgentEnrolled, bool MobileRegistered);

[PublicAPI]
public sealed record IdentityKdfStateResponse(
    ushort SecurityVersion,
    ushort MinimumSecurityVersion,
    string ProfileId,
    string KdfSalt,
    uint CredentialRevision,
    uint PrivateKeyWrapRevision,
    string? DeviceWrapperMetadata);

[PublicAPI]
public sealed record GetAccountResponse(
    Guid UserId,
    string Email,
    string DisplayName,
    string? AvatarUrl,
    bool IsOnboarded,
    bool EmailVerified,
    string? Salt,
    string? EncryptedPrivateKey,
    string? RecoverySalt,
    string? EncryptedPrivateKeyByRecovery,
    uint? MemberKeyVersion,
    IdentityKdfStateResponse? Kdf,
    OnboardingStepsResponse OnboardingSteps);

[PublicAPI]
internal sealed class GetAccountEndpoint(IdentityDomainReadContext domainReadContext)
    : EndpointWithoutRequest<GetAccountResponse>
{
    public override void Configure()
    {
        Get("api/account");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        Summary(summary =>
        {
            summary.Summary = "Get current user's account";
            summary.Description = "Returns the authenticated user's account data, key setup status, and onboarding-step state. Onboarding is materialized per-user in Identity (fed by commands from the owning modules), so it is a plain single-row read — never a cross-module query.";
        });
        Tags("Identity/Account");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var userId = User.GetUserId();
        var organizationId = User.GetOrganizationId();
        if (userId is null || organizationId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var organizationSteps = await domainReadContext.Organizations
            .Where(o => o.Id == organizationId)
            .Select(o => new { o.ApiKeyCreated, o.AgentEnrolled })
            .FirstOrDefaultAsync(ct);
        if (organizationSteps is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var account = await domainReadContext.Users
            .Where(u => u.Id == userId)
            .Select(u => new GetAccountResponse(
                u.Id,
                u.Email,
                u.DisplayName,
                u.AvatarUrl,
                u.IsOnboarded,
                u.EmailVerified,
                u.Salt != null ? WebEncoders.Base64UrlEncode(u.Salt) : null,
                u.EncryptedPrivateKey != null ? WebEncoders.Base64UrlEncode(u.EncryptedPrivateKey) : null,
                u.RecoverySalt != null ? WebEncoders.Base64UrlEncode(u.RecoverySalt) : null,
                u.EncryptedPrivateKeyByRecovery != null
                    ? WebEncoders.Base64UrlEncode(u.EncryptedPrivateKeyByRecovery)
                    : null,
                u.MemberKeyVersion,
                u.KdfProfileId == null || u.Salt == null
                    ? null
                    : new IdentityKdfStateResponse(
                        u.SecurityVersion,
                        u.MinimumSecurityVersion,
                        u.KdfProfileId!,
                        WebEncoders.Base64UrlEncode(u.Salt),
                        u.CredentialRevision,
                        u.PrivateKeyWrapRevision,
                        u.DeviceWrapperMetadata != null
                            ? WebEncoders.Base64UrlEncode(u.DeviceWrapperMetadata)
                            : null),
                new OnboardingStepsResponse(
                    u.EntryCreated, organizationSteps.ApiKeyCreated, organizationSteps.AgentEnrolled, u.MobileRegistered)))
            .FirstOrDefaultAsync(ct);

        if (account is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        await Send.OkAsync(account, ct);
    }
}
