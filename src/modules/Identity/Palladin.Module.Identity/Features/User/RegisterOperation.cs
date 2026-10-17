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

internal sealed class RegisterOperation(
    IdentityDomainWriteContext domainWriteContext,
    IPasswordHasher passwordHasher,
    IAuthSessionIssuer sessionIssuer,
    IGuidProvider guidProvider,
    IOptions<EmailVerificationOptions> emailVerificationOptions,
    ITransportContext transportContext,
    IClock clock)
{
    public async Task<IdentityOperationResult<AuthSessionResponse>> ExecuteAsync(RegisterRequest req, HttpContext httpContext, CancellationToken ct)
    {
        var email = req.Email.Trim().ToLowerInvariant();
        var now = clock.GetCurrentInstant();

        if (await domainWriteContext.Users.AnyAsync(u => u.Email == email, ct))
        {
            return IdentityOperationResult<AuthSessionResponse>.Status(StatusCodes.Status409Conflict);
        }

        var orgId = guidProvider.Generate();
        var userId = req.AccountId;

        var organization = Organization.Create(orgId, $"{req.DisplayName}'s Organization", PlanType.Basic, userId, req.DisplayName, now);
        domainWriteContext.Add(organization);

        var adminRole = Role.CreateAdministrator(guidProvider.Generate(), orgId, now);
        domainWriteContext.Add(adminRole);
        domainWriteContext.Add(Role.CreateDefaultUser(guidProvider.Generate(), orgId, now));

        var user = Domain.User.RegisterWithPassword(
            userId, email, req.DisplayName, req.PreferredLanguage, orgId, adminRole.Permissions,
            req.KdfSalt, req.RecoverySalt, req.PublicKey, req.EncryptedPrivateKey, req.EncryptedPrivateKeyByRecovery,
            req.DeviceWrapperMetadata,
            transportContext.Platform ?? "unknown", now);
        domainWriteContext.Add(user);
        domainWriteContext.Add(OrganizationMember.CreateOwner(orgId, userId, adminRole, now));
        domainWriteContext.Add(OrganizationMemberDirectoryEntry.Create(
            orgId, userId, req.DisplayName, now));

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
            user, organization, adminRole.Permissions, authorizationVersion: 1, now);

        try
        {
            await domainWriteContext.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: Core.Persistence.PostgresErrorCodes.UniqueViolation })
        {
            return IdentityOperationResult<AuthSessionResponse>.Status(StatusCodes.Status409Conflict);
        }

        return IdentityOperationResult<AuthSessionResponse>.Ok(new AuthSessionResponse(
            accessToken,
            refreshToken,
            userId,
            user.IsOnboarded,
            user.EmailVerified,
            null,
            null));
    }
}
