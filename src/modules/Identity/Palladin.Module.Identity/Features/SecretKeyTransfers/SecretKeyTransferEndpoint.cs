using System.Globalization;
using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Palladin.Core.Api;
using Palladin.Module.Identity.Infrastructure.Login;

namespace Palladin.Module.Identity.Features;

internal abstract class SecretKeyTransferEndpoint<TRequest, TResponse> : Endpoint<TRequest, TResponse>
    where TRequest : notnull
{
    protected string ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    protected async Task<bool> RejectWhenLimitedAsync(LoginRateLimitLease lease, CancellationToken ct)
    {
        if (lease.IsAcquired)
        {
            return false;
        }

        HttpContext.Response.Headers.RetryAfter = lease.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        await Send.StatusCodeAsync(StatusCodes.Status429TooManyRequests, ct);
        return true;
    }

    protected Task SendTransferErrorAsync(int statusCode, string errorKey, CancellationToken ct)
    {
        AddError(ErrorResponses.General(errorKey));
        return Send.ErrorsAsync(statusCode, ct);
    }
}
