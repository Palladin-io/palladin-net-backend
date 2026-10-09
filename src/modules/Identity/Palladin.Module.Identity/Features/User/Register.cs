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
internal sealed class RegisterEndpoint(RegisterOperation operation) : IdentityOperationEndpoint<RegisterRequest, AuthSessionResponse>
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
        var result = await operation.ExecuteAsync(req, HttpContext, ct);
        await SendResultAsync(result, ct);
    }
}
