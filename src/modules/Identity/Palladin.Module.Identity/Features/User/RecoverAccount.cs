using Palladin.Core.Security;
using Palladin.Core.Api;
using Palladin.Core.Transport;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Infrastructure;
using Palladin.Module.Identity.Infrastructure.PasswordAuth;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Module.Identity.Infrastructure.Recovery;
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

[PublicAPI]
public sealed record RecoverAccountRequest
{
    public ushort SecurityVersion { get; init; }
    public string KdfProfileId { get; init; } = string.Empty;
    public uint BaseCredentialRevision { get; init; }
    public uint BasePrivateKeyWrapRevision { get; init; }
    [JsonConverter(typeof(Base64UrlByteArrayJsonConverter))] public byte[] NewKdfSalt { get; init; } = [];
    [JsonConverter(typeof(Base64UrlByteArrayJsonConverter))] public byte[] NewEncryptedPrivateKey { get; init; } = [];
    [JsonConverter(typeof(Base64UrlByteArrayJsonConverter))] public byte[] NewRecoverySalt { get; init; } = [];
    [JsonConverter(typeof(Base64UrlByteArrayJsonConverter))] public byte[] NewEncryptedPrivateKeyByRecovery { get; init; } = [];
    [JsonConverter(typeof(Base64UrlByteArrayJsonConverter))] public byte[]? NewAuthCredential { get; init; }
    [JsonConverter(typeof(Base64UrlByteArrayJsonConverter))] public byte[]? DeviceWrapperMetadata { get; init; }
    [JsonConverter(typeof(Base64UrlByteArrayJsonConverter))] public byte[]? RecoveryProof { get; init; }
}

[UsedImplicitly]
internal sealed class RecoverAccountValidator : Validator<RecoverAccountRequest>
{
    public RecoverAccountValidator()
    {
        RuleFor(x => x).Must(x => IdentityKdfProfiles.IsRegistered(x.SecurityVersion, x.KdfProfileId))
            .WithName("kdfProfileId");
        RuleFor(x => x.BasePrivateKeyWrapRevision).GreaterThan(0u);
        RuleFor(x => x.NewKdfSalt).Must(b => b is { Length: IdentityKdfProfiles.KdfSaltBytes });
        RuleFor(x => x.NewEncryptedPrivateKey).NotEmpty().Must(b => b is { Length: >= 32 and <= 4096 });
        RuleFor(x => x.NewRecoverySalt).NotEmpty().Must(b => b is { Length: >= 16 and <= 64 });
        RuleFor(x => x.NewEncryptedPrivateKeyByRecovery).NotEmpty()
            .Must(b => b is { Length: >= 32 and <= 4096 });
        RuleFor(x => x.NewAuthCredential!).Must(b => b.Length == IdentityKdfProfiles.AuthCredentialBytes)
            .When(x => x.NewAuthCredential is not null);
        RuleFor(x => x.DeviceWrapperMetadata!).Must(value => value.Length <= 16384)
            .When(x => x.DeviceWrapperMetadata is not null);
    }
}

[PublicAPI]
internal sealed class RecoverAccountEndpoint(
    IdentityDomainWriteContext domainWriteContext,
    RecoveryChallengeConsumer recoveryChallengeConsumer,
    IPasswordHasher passwordHasher,
    ITransportContext transportContext,
    IClock clock) : Endpoint<RecoverAccountRequest>
{
    public override void Configure()
    {
        Put("api/account/recovery");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        Options(builder => builder.AllowNonActiveOrganizationMembership());
        Summary(summary =>
        {
            summary.Summary = "Recover the account using pre-encrypted key material";
            summary.Description = "Replaces the user's KDF salt, server verifier, encrypted private key and recovery key material within the account's registered profile, revokes every refresh session, clears the unusable wrapped legacy Member key and sends the account-recovered e-mail. Requires recoveryProof: the secret opened from POST api/account/recovery/challenge with the account private key; a missing, wrong, expired or reused proof returns 403 and consumes the challenge. The server never sees the plaintext master password or recovery key — only the ciphertexts and salts derived client-side from the new password and new recovery key.";
        });
        Tags("Identity/Account");
    }

    public override async Task HandleAsync(RecoverAccountRequest req, CancellationToken ct)
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

        if (!await recoveryChallengeConsumer.ConsumeAsync(user, req.RecoveryProof, ct))
        {
            AddError(ErrorResponses.General("recovery-proof-invalid"));
            await Send.ErrorsAsync(StatusCodes.Status403Forbidden, ct);
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

        var isPasswordAccount = user.PasswordCredential is not null;
        if (isPasswordAccount != (req.NewAuthCredential is not null)
            || (isPasswordAccount && req.BaseCredentialRevision == 0))
        {
            AddError(ErrorResponses.General("new-auth-material-required"));
            await Send.ErrorsAsync(StatusCodes.Status400BadRequest, ct);
            return;
        }

        var now = clock.GetCurrentInstant();
        try
        {
            user.RecoverAccount(
                req.BaseCredentialRevision,
                req.BasePrivateKeyWrapRevision,
                req.NewKdfSalt,
                req.NewEncryptedPrivateKey,
                req.NewRecoverySalt,
                req.NewEncryptedPrivateKeyByRecovery,
                req.DeviceWrapperMetadata,
                isPasswordAccount,
                transportContext.Platform ?? "unknown",
                now);

            if (user.PasswordCredential is { } credential)
            {
                var (serverHash, serverSalt) = passwordHasher.Hash(req.NewAuthCredential!);
                credential.Reset(serverHash, req.NewKdfSalt, serverSalt, now);
            }

            await domainWriteContext.CommitAsync(ct);
        }
        catch (InvalidOperationException)
        {
            AddError(ErrorResponses.General("recovery-conflict"));
            await Send.ErrorsAsync(StatusCodes.Status409Conflict, ct);
            return;
        }
        catch (DbUpdateConcurrencyException)
        {
            AddError(ErrorResponses.General("recovery-conflict"));
            await Send.ErrorsAsync(StatusCodes.Status409Conflict, ct);
            return;
        }

        await Send.NoContentAsync(ct);
    }
}
