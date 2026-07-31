using Palladin.Core.Api;
using FastEndpoints;
using FluentValidation.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Palladin.Core.Security;

// Gate for actions that require a verified email. Applied per-endpoint via RequireEmailVerified() —
// never blanket-applied, so unverified users can still reach onboarding/verification endpoints.
// Honours the IEmailVerificationGate runtime switch: when the gate is disabled, it lets everyone
// through (config kill-switch); when enabled, an unverified user gets 403.
public sealed class RequireEmailVerifiedPreProcessor<TRequest> : IPreProcessor<TRequest>
{
    public async Task PreProcessAsync(IPreProcessorContext<TRequest> context, CancellationToken ct)
    {
        var gate = context.HttpContext.RequestServices.GetService<IEmailVerificationGate>();
        if (gate is { IsEnabled: false })
        {
            return;
        }

        var user = context.HttpContext.User;
        if (user.GetUserId() is null)
        {
            await context.HttpContext.Response.SendUnauthorizedAsync(ct);
            return;
        }

        if (!user.GetEmailVerified())
        {
            // Distinct error key (not a bare 403) so the client can target ONLY the verification case
            // and redirect to /verify-email — plain permission 403s must stay untouched.
            await context.HttpContext.Response.SendErrorsAsync(
                [new ValidationFailure(string.Empty, ErrorResponses.General("email-not-verified"))],
                StatusCodes.Status403Forbidden,
                cancellation: ct);
        }
    }
}
