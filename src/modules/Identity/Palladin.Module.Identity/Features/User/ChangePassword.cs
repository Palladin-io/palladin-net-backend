using Palladin.Core.Api;
using Palladin.Core.Security;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Infrastructure.PasswordAuth;
using Palladin.Module.Identity.Infrastructure.Persistence;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using System.Text.Json.Serialization;
using Palladin.Core.Json;

namespace Palladin.Module.Identity.Features;

// Change the master password of the logged-in user. Variant A: the client re-derives authHash (new
// authSalt) + MK (new encSalt) and re-wraps the private key. currentAuthHash proves knowledge of the
// old password so a stolen JWT alone cannot overwrite key material. The recovery mnemonic is a separate
// path and is never rotated here. No password or master key ever reaches the server.
[PublicAPI]
public sealed record ChangePasswordRequest
{
    public ushort SecurityVersion { get; init; }
    public string KdfProfileId { get; init; } = string.Empty;
    public uint BaseCredentialRevision { get; init; }
    public uint BasePrivateKeyWrapRevision { get; init; }
    [JsonConverter(typeof(Base64UrlByteArrayJsonConverter))] public byte[] CurrentAuthCredential { get; init; } = [];
    [JsonConverter(typeof(Base64UrlByteArrayJsonConverter))] public byte[] NewAuthCredential { get; init; } = [];
    [JsonConverter(typeof(Base64UrlByteArrayJsonConverter))] public byte[] NewKdfSalt { get; init; } = [];
    [JsonConverter(typeof(Base64UrlByteArrayJsonConverter))] public byte[] NewEncryptedPrivateKey { get; init; } = [];
}

[UsedImplicitly]
internal sealed class ChangePasswordValidator : Validator<ChangePasswordRequest>
{
    public ChangePasswordValidator()
    {
        RuleFor(x => x.SecurityVersion).Equal(IdentityKdfProfiles.CurrentSecurityVersion);
        RuleFor(x => x.KdfProfileId).Equal(IdentityKdfProfiles.CurrentProfileId);
        RuleFor(x => x.BaseCredentialRevision).GreaterThan(0u);
        RuleFor(x => x.BasePrivateKeyWrapRevision).GreaterThan(0u);
        RuleFor(x => x.NewKdfSalt).Must(b => b is { Length: IdentityKdfProfiles.KdfSaltBytes });
        RuleFor(x => x.NewEncryptedPrivateKey).NotEmpty().Must(b => b is { Length: >= 32 and <= 4096 });
        RuleFor(x => x.CurrentAuthCredential).Must(b => b is
            { Length: IdentityKdfProfiles.AuthCredentialBytes });
        RuleFor(x => x.NewAuthCredential).Must(b => b is { Length: IdentityKdfProfiles.AuthCredentialBytes });
    }
}

[PublicAPI]
[AllowNonActiveOrganizationMembership]
internal sealed class ChangePasswordEndpoint(
    IdentityDomainWriteContext domainWriteContext,
    IPasswordHasher passwordHasher,
    IClock clock) : Endpoint<ChangePasswordRequest>
{
    public override void Configure()
    {
        Put("api/account/password");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        Summary(summary =>
        {
            summary.Summary = "Change the master password";
            summary.Description = "Re-wraps the private key under the new master key and, for password users, "
                + "rotates the login credential after verifying the current authHash (constant-time). The "
                + "recovery mnemonic is never rotated. Revokes ALL sessions (every active refresh token, "
                + "including the current one); the caller's access token stays valid until it expires.";
        });
        Tags("Identity/Account");
    }

    public override async Task HandleAsync(ChangePasswordRequest req, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var user = await domainWriteContext.Users
            .Include(u => u.RefreshTokens)
            .Include(u => u.PasswordCredential)
            .FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null || !user.IsOnboarded)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (user.PasswordCredential is not { } credential)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (user.SecurityVersion != req.SecurityVersion
            || user.MinimumSecurityVersion > req.SecurityVersion
            || user.KdfProfileId != req.KdfProfileId)
        {
            AddError(ErrorResponses.General("security-version-downgrade"));
            await Send.ErrorsAsync(StatusCodes.Status409Conflict, ct);
            return;
        }

        if (!passwordHasher.Verify(req.CurrentAuthCredential, credential.AuthHash, credential.ServerHashSalt))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        var now = clock.GetCurrentInstant();
        try
        {
            user.ChangeMasterPassword(
                req.BaseCredentialRevision,
                req.BasePrivateKeyWrapRevision,
                req.NewKdfSalt,
                req.NewEncryptedPrivateKey,
                now);
            var (serverHash, serverSalt) = passwordHasher.Hash(req.NewAuthCredential);
            credential.Reset(serverHash, req.NewKdfSalt, serverSalt, now);
            await domainWriteContext.CommitAsync(ct);
        }
        catch (InvalidOperationException)
        {
            AddError(ErrorResponses.General("migration-conflict"));
            await Send.ErrorsAsync(StatusCodes.Status409Conflict, ct);
            return;
        }
        catch (DbUpdateConcurrencyException)
        {
            AddError(ErrorResponses.General("migration-conflict"));
            await Send.ErrorsAsync(StatusCodes.Status409Conflict, ct);
            return;
        }

        await Send.NoContentAsync(ct);
    }
}
