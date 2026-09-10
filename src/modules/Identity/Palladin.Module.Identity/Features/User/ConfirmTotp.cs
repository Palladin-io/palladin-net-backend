using Palladin.Core.Api;
using Palladin.Core.Guid;
using Palladin.Core.Security;
using Palladin.Module.Identity.Domain;
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
public sealed record ConfirmTotpRequest
{
    public string Code { get; init; } = string.Empty;
}

[PublicAPI]
public sealed record ConfirmTotpResponse(IReadOnlyList<string> RecoveryCodes);

[UsedImplicitly]
internal sealed class ConfirmTotpValidator : Validator<ConfirmTotpRequest>
{
    public ConfirmTotpValidator()
    {
        RuleFor(x => x.Code).NotEmpty();
    }
}

[PublicAPI]
internal sealed class ConfirmTotpEndpoint(
    IdentityDomainWriteContext domainWriteContext,
    ITotpService totpService,
    IGuidProvider guidProvider,
    IClock clock) : Endpoint<ConfirmTotpRequest, ConfirmTotpResponse>
{
    public override void Configure()
    {
        Post("api/auth/totp/confirm");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        Options(builder => builder.AllowNonActiveOrganizationMembership());
        Summary(summary =>
        {
            summary.Summary = "Confirm and enable TOTP";
            summary.Description = "Verifies a code against the pending secret, enables the factor and returns "
                + "one-time recovery codes (only their hashes are stored — shown exactly once).";
        });
        Tags("Identity/Auth");
    }

    public override async Task HandleAsync(ConfirmTotpRequest req, CancellationToken ct)
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

        if (user?.TotpCredential is not { PendingSecret: { } pendingSecret } credential)
        {
            AddError(ErrorResponses.General("totp-enrollment-not-started"));
            await Send.ErrorsAsync(400, ct);
            return;
        }

        if (!totpService.VerifyCode(pendingSecret, req.Code, 0, out var matchedTimeStep))
        {
            AddError(ErrorResponses.General("totp-code-invalid"));
            await Send.ErrorsAsync(400, ct);
            return;
        }

        var recoveryCodes = totpService.GenerateRecoveryCodes();
        var recoveryEntities = recoveryCodes
            .Select(code => TotpRecoveryCode.Create(guidProvider.Generate(), user.Id, totpService.HashRecoveryCode(code)))
            .ToArray();

        credential.Confirm(matchedTimeStep, clock.GetCurrentInstant());
        domainWriteContext.AddRange(recoveryEntities);
        if (!user.TryAdvanceSharedUnlockSequence())
        {
            await Send.StatusCodeAsync(409, ct);
            return;
        }

        try
        {
            await domainWriteContext.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            await Send.StatusCodeAsync(409, ct);
            return;
        }

        await Send.OkAsync(new ConfirmTotpResponse(recoveryCodes), ct);
    }
}
