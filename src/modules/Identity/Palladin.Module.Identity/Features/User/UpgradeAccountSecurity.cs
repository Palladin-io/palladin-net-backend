using System.Globalization;
using System.Text.Json.Serialization;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Core.Api;
using Palladin.Core.Cryptography;
using Palladin.Core.Json;
using Palladin.Core.Security;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Infrastructure.Login;
using Palladin.Module.Identity.Infrastructure.PasswordAuth;
using Palladin.Module.Identity.Infrastructure.Persistence;

namespace Palladin.Module.Identity.Features;

// identity-kdf v3 "migration": the client proves the source profile with its current AuthCredential and uploads only
// derived credentials, public keys and ciphertext. Account Secret, AccountRoot, MK, private keys and the recovery
// phrase are structurally absent.
[PublicAPI]
public sealed record UpgradeAccountSecurityRequest
{
    public Guid MigrationId { get; init; }
    public ushort SourceSecurityVersion { get; init; }
    public uint BaseCredentialRevision { get; init; }
    public uint BasePrivateKeyWrapRevision { get; init; }
    public uint BaseMemberKeyVersion { get; init; }
    public ushort SecurityVersion { get; init; }
    public string KdfProfileId { get; init; } = string.Empty;
    [JsonConverter(typeof(Base64UrlByteArrayJsonConverter))] public byte[] CurrentAuthCredential { get; init; } = [];
    [JsonConverter(typeof(Base64UrlByteArrayJsonConverter))] public byte[] NewAuthCredential { get; init; } = [];
    [JsonConverter(typeof(Base64UrlByteArrayJsonConverter))] public byte[] KdfSalt { get; init; } = [];
    public string MemberPublicKeySuiteId { get; init; } = string.Empty;
    [JsonConverter(typeof(Base64UrlByteArrayJsonConverter))] public byte[] MemberPublicKey { get; init; } = [];
    [JsonConverter(typeof(Base64UrlByteArrayJsonConverter))] public byte[] EncryptedPrivateKey { get; init; } = [];
    [JsonConverter(typeof(Base64UrlByteArrayJsonConverter))] public byte[] RecoverySalt { get; init; } = [];
    [JsonConverter(typeof(Base64UrlByteArrayJsonConverter))] public byte[] EncryptedPrivateKeyByRecovery { get; init; } = [];
    [JsonConverter(typeof(Base64UrlByteArrayJsonConverter))] public byte[] LegacyEncryptedPrivateKey { get; init; } = [];
    [JsonConverter(typeof(Base64UrlByteArrayJsonConverter))] public byte[]? DeviceWrapperMetadata { get; init; }
}

[PublicAPI]
public sealed record UpgradeAccountSecurityResponse(
    Guid MigrationId,
    ushort SecurityVersion,
    ushort MinimumSecurityVersion,
    string KdfProfileId,
    uint CredentialRevision,
    uint PrivateKeyWrapRevision,
    uint MemberKeyVersion,
    string MemberPublicKeySuiteId,
    uint? LegacyMemberKeyVersion);

[UsedImplicitly]
internal sealed class UpgradeAccountSecurityValidator : Validator<UpgradeAccountSecurityRequest>
{
    public UpgradeAccountSecurityValidator()
    {
        RuleFor(x => x.MigrationId).NotEmpty();
        RuleFor(x => x.SourceSecurityVersion).GreaterThan((ushort)0);
        RuleFor(x => x.BaseCredentialRevision).GreaterThan(0u);
        RuleFor(x => x.BasePrivateKeyWrapRevision).GreaterThan(0u);
        RuleFor(x => x.BaseMemberKeyVersion).GreaterThan(0u);
        RuleFor(x => x.SecurityVersion).Equal(IdentityKdfProfiles.Current.SecurityVersion);
        RuleFor(x => x.KdfProfileId).Equal(IdentityKdfProfiles.Current.Id);
        RuleFor(x => x.CurrentAuthCredential).Must(b => b is { Length: IdentityKdfProfiles.AuthCredentialBytes });
        RuleFor(x => x.NewAuthCredential).Must(b => b is { Length: IdentityKdfProfiles.AuthCredentialBytes });
        RuleFor(x => x.KdfSalt).Must(b => b is { Length: IdentityKdfProfiles.KdfSaltBytes });
        RuleFor(x => x.MemberPublicKeySuiteId).Equal(KeyWrapperSuites.Current.Id);
        RuleFor(x => x.MemberPublicKey).Must(b => b.Length == KeyWrapperSuites.Current.PublicKeyBytes);
        RuleFor(x => x.EncryptedPrivateKey).Must(b => b is { Length: >= 32 and <= 4096 });
        RuleFor(x => x.RecoverySalt).Must(b => b is { Length: >= 16 and <= 64 });
        RuleFor(x => x.EncryptedPrivateKeyByRecovery).Must(b => b is { Length: >= 32 and <= 4096 });
        RuleFor(x => x.LegacyEncryptedPrivateKey).Must(b => b is { Length: >= 32 and <= 4096 });
        RuleFor(x => x.DeviceWrapperMetadata!).Must(value => value.Length <= 16384)
            .When(x => x.DeviceWrapperMetadata is not null);
    }
}

[PublicAPI]
internal sealed class UpgradeAccountSecurityEndpoint(
    IdentityDomainWriteContext domainWriteContext,
    CurrentAuthCredentialVerifier credentialVerifier,
    IPasswordHasher passwordHasher,
    IClock clock) : Endpoint<UpgradeAccountSecurityRequest, UpgradeAccountSecurityResponse>
{
    public override void Configure()
    {
        Post("api/account/security-upgrade");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        Options(builder => builder.AllowNonActiveOrganizationMembership());
        Summary(summary =>
        {
            summary.Summary = "Upgrade the account to the current Identity security version";
            summary.Description = "Atomically replaces the source-profile credential, KDF salt, Member key pair "
                + "(current key-wrapper suite, next key version), private-key wrappers and recovery material after "
                + "verifying the current AuthCredential and the compare-and-swap bases. The previous private key is "
                + "kept only as an opaque wrapped legacy record. A retry with the committed migrationId returns the "
                + "resulting state without another write; stale bases return 409 security-upgrade-conflict.";
        });
        Tags("Identity/Account");
    }

    public override async Task HandleAsync(UpgradeAccountSecurityRequest req, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var user = await domainWriteContext.Users
            .Include(u => u.PasswordCredential)
            .FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is not { IsOnboarded: true, PasswordCredential: { } credential })
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (user.HasCompletedSecurityUpgrade(req.MigrationId))
        {
            await Send.OkAsync(ToResponse(user, req.MigrationId), ct);
            return;
        }

        if (user.SecurityVersion != req.SourceSecurityVersion || user.MinimumSecurityVersion > req.SourceSecurityVersion)
        {
            await SendConflictAsync(ct);
            return;
        }

        var credentialCheck = await credentialVerifier.VerifyAsync(user, credential, req.CurrentAuthCredential, HttpContext, ct);
        if (credentialCheck.RetryAfterSeconds is { } retryAfter)
        {
            HttpContext.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
            await Send.StatusCodeAsync(StatusCodes.Status429TooManyRequests, ct);
            return;
        }

        if (!credentialCheck.IsVerified)
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        var now = clock.GetCurrentInstant();
        try
        {
            user.UpgradeSecurity(
                req.MigrationId,
                req.SourceSecurityVersion,
                req.BaseCredentialRevision,
                req.BasePrivateKeyWrapRevision,
                req.BaseMemberKeyVersion,
                req.KdfSalt,
                req.MemberPublicKeySuiteId,
                req.MemberPublicKey,
                req.EncryptedPrivateKey,
                req.RecoverySalt,
                req.EncryptedPrivateKeyByRecovery,
                req.LegacyEncryptedPrivateKey,
                req.DeviceWrapperMetadata,
                now);
            var (serverHash, serverSalt) = passwordHasher.Hash(req.NewAuthCredential);
            credential.Reset(serverHash, req.KdfSalt, serverSalt, now);
            await domainWriteContext.CommitAsync(ct);
        }
        catch (InvalidOperationException)
        {
            await SendConflictAsync(ct);
            return;
        }
        catch (DbUpdateConcurrencyException)
        {
            await SendConflictAsync(ct);
            return;
        }

        await Send.OkAsync(ToResponse(user, req.MigrationId), ct);
    }

    private Task SendConflictAsync(CancellationToken ct)
    {
        AddError(ErrorResponses.General("security-upgrade-conflict"));
        return Send.ErrorsAsync(StatusCodes.Status409Conflict, ct);
    }

    private static UpgradeAccountSecurityResponse ToResponse(Domain.User user, Guid migrationId) => new(
        migrationId,
        user.SecurityVersion,
        user.MinimumSecurityVersion,
        user.KdfProfileId!,
        user.CredentialRevision,
        user.PrivateKeyWrapRevision,
        user.MemberKeyVersion!.Value,
        user.MemberPublicKeySuiteId!,
        user.LegacyMemberKeyVersion);
}
