using Palladin.Core.Security;
using Palladin.Core.Api;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Infrastructure;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Module.Identity.Infrastructure.PasswordAuth;
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
public sealed record SetupAccountRequest
{
    public ushort SecurityVersion { get; init; }
    public string KdfProfileId { get; init; } = string.Empty;
    [JsonConverter(typeof(Base64UrlByteArrayJsonConverter))] public byte[] KdfSalt { get; init; } = [];
    [JsonConverter(typeof(Base64UrlByteArrayJsonConverter))] public byte[] RecoverySalt { get; init; } = [];
    [JsonConverter(typeof(Base64UrlByteArrayJsonConverter))] public byte[] PublicKey { get; init; } = [];
    [JsonConverter(typeof(Base64UrlByteArrayJsonConverter))] public byte[] EncryptedPrivateKey { get; init; } = [];
    [JsonConverter(typeof(Base64UrlByteArrayJsonConverter))] public byte[] EncryptedPrivateKeyByRecovery { get; init; } = [];
    [JsonConverter(typeof(Base64UrlByteArrayJsonConverter))] public byte[] NewAuthCredential { get; init; } = [];
    [JsonConverter(typeof(Base64UrlByteArrayJsonConverter))] public byte[]? DeviceWrapperMetadata { get; init; }
}

[UsedImplicitly]
internal sealed class SetupAccountValidator : Validator<SetupAccountRequest>
{
    public SetupAccountValidator()
    {
        RuleFor(x => x.SecurityVersion).Equal(IdentityKdfProfiles.CurrentSecurityVersion);
        RuleFor(x => x.KdfProfileId).Equal(IdentityKdfProfiles.CurrentProfileId);
        RuleFor(x => x.KdfSalt).Must(b => b is { Length: IdentityKdfProfiles.KdfSaltBytes });
        RuleFor(x => x.RecoverySalt).NotEmpty().Must(b => b is { Length: >= 16 and <= 64 });
        RuleFor(x => x.PublicKey).NotEmpty().Must(b => b is { Length: 32 });
        RuleFor(x => x.EncryptedPrivateKey).NotEmpty().Must(b => b is { Length: >= 32 and <= 4096 });
        RuleFor(x => x.EncryptedPrivateKeyByRecovery).NotEmpty()
            .Must(b => b is { Length: >= 32 and <= 4096 });
        RuleFor(x => x.NewAuthCredential)
            .Must(b => b is { Length: IdentityKdfProfiles.AuthCredentialBytes });
        RuleFor(x => x.DeviceWrapperMetadata!).Must(value => value.Length <= 16384)
            .When(x => x.DeviceWrapperMetadata is not null);
    }
}

[PublicAPI]
internal sealed class SetupAccountEndpoint(
    IdentityDomainWriteContext domainWriteContext,
    IPasswordHasher passwordHasher,
    IClock clock) : Endpoint<SetupAccountRequest>
{
    public override void Configure()
    {
        Post("api/account/setup");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        Summary(summary =>
        {
            summary.Summary = "Complete account setup with key material";
            summary.Description = "Stores the user's password-only v1 KDF state, public key, and encrypted private keys. An exact public-key retry is idempotent and republishes the Vault directory command without replacing stored key material; a different key returns 409.";
        });
        Tags("Identity/Account");
    }

    public override async Task HandleAsync(SetupAccountRequest req, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var user = await domainWriteContext.Users
            .Include(account => account.PasswordCredential)
            .FirstOrDefaultAsync(account => account.Id == userId, ct);
        if (user is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        if (user.IsOnboarded)
        {
            if (user.PublicKey is null || !user.PublicKey.AsSpan().SequenceEqual(req.PublicKey))
            {
                await Send.StatusCodeAsync(StatusCodes.Status409Conflict, ct);
                return;
            }

            if (user.PasswordCredential is null)
            {
                var retryTime = clock.GetCurrentInstant();
                var (retryServerHash, retryServerSalt) = passwordHasher.Hash(req.NewAuthCredential);
                domainWriteContext.Add(PasswordCredential.Create(
                    user.Id,
                    retryServerHash,
                    user.Salt!,
                    retryServerSalt,
                    retryTime));
                user.EnablePasswordCredential(retryTime);
            }

            user.RepublishMemberKeyDirectory();
            await domainWriteContext.CommitAsync(ct);
            await Send.NoContentAsync(ct);
            return;
        }

        if (user.PasswordCredential is not null)
        {
            await Send.StatusCodeAsync(StatusCodes.Status409Conflict, ct);
            return;
        }

        var now = clock.GetCurrentInstant();
        user.SetupAccount(
            req.SecurityVersion,
            req.KdfProfileId,
            req.KdfSalt,
            req.RecoverySalt,
            req.PublicKey,
            req.EncryptedPrivateKey,
            req.EncryptedPrivateKeyByRecovery,
            req.DeviceWrapperMetadata,
            now);
        var (serverHash, serverSalt) = passwordHasher.Hash(req.NewAuthCredential);
        domainWriteContext.Add(PasswordCredential.Create(
            user.Id,
            serverHash,
            req.KdfSalt,
            serverSalt,
            now));

        try
        {
            await domainWriteContext.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            await Send.StatusCodeAsync(StatusCodes.Status409Conflict, ct);
            return;
        }

        await Send.NoContentAsync(ct);
    }
}
