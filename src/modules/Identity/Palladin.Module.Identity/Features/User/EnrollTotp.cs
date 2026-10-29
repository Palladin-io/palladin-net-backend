using Palladin.Core.Security;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Infrastructure.PasswordAuth;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Module.Identity.Infrastructure.Totp;
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
public sealed record EnrollTotpRequest
{
    [JsonConverter(typeof(Base64UrlByteArrayJsonConverter))] public byte[] CurrentAuthCredential { get; init; } = [];
}

[PublicAPI]
public sealed record EnrollTotpResponse(string Secret, string OtpauthUri);

[UsedImplicitly]
internal sealed class EnrollTotpValidator : Validator<EnrollTotpRequest>
{
    public EnrollTotpValidator() =>
        RuleFor(x => x.CurrentAuthCredential).Must(b => b is { Length: IdentityKdfProfiles.AuthCredentialBytes });
}

[PublicAPI]
internal sealed class EnrollTotpEndpoint(
    IdentityDomainWriteContext domainWriteContext,
    ITotpService totpService,
    IPasswordHasher passwordHasher,
    IClock clock) : Endpoint<EnrollTotpRequest, EnrollTotpResponse>
{
    public override void Configure()
    {
        Post("api/auth/totp/enroll");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        Options(builder => builder.AllowNonActiveOrganizationMembership());
        Summary(summary =>
        {
            summary.Summary = "Begin TOTP enrollment";
            summary.Description = "Verifies the current authCredential, then generates a pending TOTP secret and its otpauth URI for QR display. "
                + "The factor is not active until confirmed with a valid code.";
        });
        Tags("Identity/Auth");
    }

    public override async Task HandleAsync(EnrollTotpRequest req, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var user = await domainWriteContext.Users
            .Include(u => u.TotpCredential)
            .Include(u => u.PasswordCredential)
            .FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        if (user?.PasswordCredential is not { } passwordCredential)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (!passwordHasher.Verify(req.CurrentAuthCredential, passwordCredential.AuthHash, passwordCredential.ServerHashSalt))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        if (user.TotpCredential is { IsEnabled: true })
        {
            await Send.StatusCodeAsync(StatusCodes.Status409Conflict, ct);
            return;
        }

        var secret = totpService.GenerateSecret();
        var now = clock.GetCurrentInstant();

        if (user.TotpCredential is { } credential)
        {
            credential.RestartEnrollment(secret, now);
        }
        else
        {
            domainWriteContext.Add(TotpCredential.StartEnrollment(user.Id, secret, now));
        }

        await domainWriteContext.CommitAsync(ct);

        await Send.OkAsync(new EnrollTotpResponse(secret, totpService.BuildOtpAuthUri(secret, user.Email)), ct);
    }
}
