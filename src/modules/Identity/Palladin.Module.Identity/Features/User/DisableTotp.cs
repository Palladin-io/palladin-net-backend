using Palladin.Core.Api;
using Palladin.Core.Security;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Module.Identity.Infrastructure.Totp;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
public sealed record DisableTotpRequest
{
    public string Code { get; init; } = string.Empty;
}

[UsedImplicitly]
internal sealed class DisableTotpValidator : Validator<DisableTotpRequest>
{
    public DisableTotpValidator()
    {
        RuleFor(x => x.Code).NotEmpty();
    }
}

[PublicAPI]
internal sealed class DisableTotpEndpoint(
    IdentityDomainWriteContext domainWriteContext,
    ITotpService totpService,
    IClock clock) : Endpoint<DisableTotpRequest>
{
    public override void Configure()
    {
        Post("api/auth/totp/disable");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        Summary(summary =>
        {
            summary.Summary = "Disable TOTP";
            summary.Description = "Verifies a current TOTP or recovery code, then removes the factor and wipes recovery codes.";
        });
        Tags("Identity/Auth");
    }

    public override async Task HandleAsync(DisableTotpRequest req, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var user = await domainWriteContext.Users
            .Include(u => u.TotpCredential).ThenInclude(t => t!.RecoveryCodes)
            .FirstOrDefaultAsync(u => u.Id == userId, ct);

        if (user?.TotpCredential is not { IsEnabled: true, Secret: { } secret } credential)
        {
            AddError(ErrorResponses.General("totp-not-enabled"));
            await Send.ErrorsAsync(400, ct);
            return;
        }

        var now = clock.GetCurrentInstant();
        var codeAccepted = totpService.VerifyCode(secret, req.Code, credential.LastUsedTimeStep, out _)
            || credential.FindAvailableRecoveryCode(totpService.HashRecoveryCode(req.Code)) is not null;

        if (!codeAccepted)
        {
            AddError(ErrorResponses.General("totp-code-invalid"));
            await Send.ErrorsAsync(400, ct);
            return;
        }

        credential.Disable(now);
        await domainWriteContext.CommitAsync(ct);

        await Send.NoContentAsync(ct);
    }
}
