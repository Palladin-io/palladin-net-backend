using FastEndpoints;
using System.Globalization;

namespace Palladin.Module.Identity.Shared;

internal abstract class IdentityOperationEndpoint<TRequest, TResponse> : Endpoint<TRequest, TResponse>
    where TRequest : notnull
{
    protected async Task SendResultAsync(IdentityOperationResult<TResponse> result, CancellationToken ct)
    {
        if (result.RetryAfterSeconds is { } retry)
        {
            HttpContext.Response.Headers.RetryAfter = retry.ToString(CultureInfo.InvariantCulture);
        }
        if (result.Error is { } error)
        {
            AddError(error);
            await Send.ErrorsAsync(result.StatusCode, ct);
        }
        else if (result.Body is { } body)
        {
            await Send.OkAsync(body, ct);
        }
        else
        {
            await Send.StatusCodeAsync(result.StatusCode, ct);
        }
    }
}
