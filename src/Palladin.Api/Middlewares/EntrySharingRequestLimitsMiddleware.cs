using System.Security.Cryptography;

namespace Palladin.Api.Middlewares;

internal sealed class EntrySharingRequestLimitsMiddleware(RequestDelegate next)
{
    internal const int MaximumBodyBytes = 4096;

    public async Task InvokeAsync(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method)
            || !context.Request.Path.StartsWithSegments("/api/entry-shares"))
        {
            await next(context);
            return;
        }

        if (context.Request.ContentLength > MaximumBodyBytes)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        var originalBody = context.Request.Body;
        var buffer = new byte[MaximumBodyBytes + 1];
        try
        {
            var count = 0;
            while (count < buffer.Length)
            {
                var read = await originalBody.ReadAsync(buffer.AsMemory(count), context.RequestAborted);
                if (read == 0)
                {
                    break;
                }

                count += read;
            }

            if (count > MaximumBodyBytes)
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                return;
            }

            using var boundedBody = new MemoryStream(buffer, 0, count, writable: false);
            context.Request.Body = boundedBody;
            await next(context);
        }
        finally
        {
            context.Request.Body = originalBody;
            CryptographicOperations.ZeroMemory(buffer);
        }
    }
}
