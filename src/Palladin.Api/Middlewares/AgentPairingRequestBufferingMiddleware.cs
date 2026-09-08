using Palladin.Api.Framework;

namespace Palladin.Api.Middlewares;

/// <summary>
/// Preserves the exact anonymous pairing request bytes so the endpoint can verify the runtime's
/// Ed25519 signature after FastEndpoints has deserialized the request model.
/// </summary>
public sealed class AgentPairingRequestBufferingMiddleware(RequestDelegate next)
{
    private const int MemoryThresholdBytes = 16 * 1024;
    private const long MaximumBufferedBytes = 64 * 1024;

    public Task InvokeAsync(HttpContext context)
    {
        if (HttpMethods.IsPost(context.Request.Method)
            && AgentPairingRoute.IsStart(context.Request.Path))
        {
            context.Request.EnableBuffering(MemoryThresholdBytes, MaximumBufferedBytes);
        }

        return next(context);
    }
}
