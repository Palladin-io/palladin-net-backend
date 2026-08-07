using Palladin.Core.Guid;
using Palladin.Core.Persistence;
using Palladin.Core.Security;
using Palladin.Core.Transport;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Contracts.ValueObjects;
using Palladin.Module.Identity.Infrastructure;
using Palladin.Module.Identity.Infrastructure.Jwt;
using Palladin.Module.Identity.Infrastructure.Login;
using Palladin.Module.Identity.Infrastructure.PasswordAuth;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Module.Identity.Shared;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using NodaTime;
using System.Text.Json.Serialization;
using Palladin.Core.Json;

namespace Palladin.Module.Identity.Features;

// AuthCredential is the registered password-only v1 HKDF output. AccountRoot, password and MK are
// structurally absent from this request and never cross the client boundary.
[PublicAPI]
public sealed record RegisterRequest
{
    public Guid AccountId { get; init; }
    public string Email { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string? PreferredLanguage { get; init; }
    public ushort SecurityVersion { get; init; }
    public string KdfProfileId { get; init; } = string.Empty;
    [JsonConverter(typeof(Base64UrlByteArrayJsonConverter))] public byte[] AuthCredential { get; init; } = [];
    [JsonConverter(typeof(Base64UrlByteArrayJsonConverter))] public byte[] KdfSalt { get; init; } = [];
    [JsonConverter(typeof(Base64UrlByteArrayJsonConverter))] public byte[] RecoverySalt { get; init; } = [];
    [JsonConverter(typeof(Base64UrlByteArrayJsonConverter))] public byte[] PublicKey { get; init; } = [];
    [JsonConverter(typeof(Base64UrlByteArrayJsonConverter))] public byte[] EncryptedPrivateKey { get; init; } = [];
    [JsonConverter(typeof(Base64UrlByteArrayJsonConverter))] public byte[] EncryptedPrivateKeyByRecovery { get; init; } = [];
    [JsonConverter(typeof(Base64UrlByteArrayJsonConverter))] public byte[]? DeviceWrapperMetadata { get; init; }
}

[UsedImplicitly]
internal sealed class RegisterValidator : Validator<RegisterRequest>
{
    public RegisterValidator()
    {
        RuleFor(x => x.AccountId).Must(IdentityKdfProfiles.IsCurrentAccountId);
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.SecurityVersion).Equal(IdentityKdfProfiles.CurrentSecurityVersion);
        RuleFor(x => x.KdfProfileId).Equal(IdentityKdfProfiles.CurrentProfileId);
        RuleFor(x => x.AuthCredential).Must(b => b is { Length: IdentityKdfProfiles.AuthCredentialBytes });
        RuleFor(x => x.KdfSalt).Must(b => b is { Length: IdentityKdfProfiles.KdfSaltBytes });
        RuleFor(x => x.RecoverySalt).NotEmpty().Must(b => b is { Length: >= 16 and <= 64 });
        RuleFor(x => x.PublicKey).NotEmpty().Must(b => b is { Length: 32 });
        RuleFor(x => x.EncryptedPrivateKey).NotEmpty().Must(b => b is { Length: >= 32 and <= 4096 });
        RuleFor(x => x.EncryptedPrivateKeyByRecovery).NotEmpty()
            .Must(b => b is { Length: >= 32 and <= 4096 });
        RuleFor(x => x.DeviceWrapperMetadata!).Must(value => value.Length <= 16384)
            .When(x => x.DeviceWrapperMetadata is not null);
    }
}

[PublicAPI]
internal sealed class RegisterEndpoint(
    IdentityDomainWriteContext domainWriteContext,
    IPasswordHasher passwordHasher,
    IAuthSessionIssuer sessionIssuer,
    IGuidProvider guidProvider,
    IOptions<EmailVerificationOptions> emailVerificationOptions,
    ITransportContext transportContext,
    IClock clock) : Endpoint<RegisterRequest, AuthSessionResponse>
{
    public override void Configure()
    {
        Post("api/auth/register");
        // Anonymous by design: this creates the account. Zero-knowledge material (salt, wrapped keys)
        // and the re-hashed authHash are stored; the password and master key never reach the server.
        AllowAnonymous();
        Summary(summary =>
        {
            summary.Summary = "Register with email + password";
            summary.Description = "Creates an organization and admin user with the client-generated AccountId "
                + "bound into the password-only versioned Identity KDF. "
                + "Stores the client authHash re-hashed with Argon2id, plus the zero-knowledge key material. "
                + "Issues a session and sends an email-verification link (emailVerified starts false).";
        });
        Tags("Identity/Auth");
    }

    public override async Task HandleAsync(RegisterRequest req, CancellationToken ct)
    {
        var email = req.Email.Trim().ToLowerInvariant();
        var now = clock.GetCurrentInstant();

        if (await domainWriteContext.Users.AnyAsync(u => u.Email == email, ct))
        {
            await Send.StatusCodeAsync(StatusCodes.Status409Conflict, ct);
            return;
        }

        var orgId = guidProvider.Generate();
        var userId = req.AccountId;

        var organization = Organization.Create(orgId, $"{req.DisplayName}'s Organization", PlanType.Basic, userId, req.DisplayName, now);
        domainWriteContext.Add(organization);

        var adminRole = Role.CreateAdministrator(guidProvider.Generate(), orgId, now);
        domainWriteContext.Add(adminRole);

        var user = Domain.User.RegisterWithPassword(
            userId, email, req.DisplayName, req.PreferredLanguage, orgId, adminRole.Permissions,
            req.KdfSalt, req.RecoverySalt, req.PublicKey, req.EncryptedPrivateKey, req.EncryptedPrivateKeyByRecovery,
            req.DeviceWrapperMetadata,
            transportContext.Platform ?? "unknown", now);
        domainWriteContext.Add(user);
        domainWriteContext.Add(OrganizationMember.CreateOwner(orgId, userId, adminRole, now));

        var (serverHash, serverSalt) = passwordHasher.Hash(req.AuthCredential);
        domainWriteContext.Add(PasswordCredential.Create(
            userId,
            serverHash,
            req.KdfSalt,
            serverSalt,
            now));

        var (token, tokenHash) = SecureToken.Generate();
        domainWriteContext.Add(VerificationToken.CreateEmailVerification(
            guidProvider.Generate(), userId, email, user.PreferredLanguage.Code, token, tokenHash,
            Duration.FromMinutes(emailVerificationOptions.Value.TokenTtlMinutes), now));

        var (accessToken, refreshToken) = sessionIssuer.Issue(
            user, orgId, adminRole.Permissions, PlanType.Basic, now);

        try
        {
            await domainWriteContext.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: Core.Persistence.PostgresErrorCodes.UniqueViolation })
        {
            await Send.StatusCodeAsync(StatusCodes.Status409Conflict, ct);
            return;
        }

        await Send.OkAsync(new AuthSessionResponse(accessToken, refreshToken, userId, user.IsOnboarded, user.EmailVerified), ct);
    }
}
