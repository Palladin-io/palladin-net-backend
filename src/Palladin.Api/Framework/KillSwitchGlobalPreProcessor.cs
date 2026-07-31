using System.Net;
using FastEndpoints;
using JetBrains.Annotations;

namespace Palladin.Api.Framework;

[UsedImplicitly]
internal sealed class KillSwitchGlobalPreProcessor : IGlobalPreProcessor
{
    public async Task PreProcessAsync(IPreProcessorContext context, CancellationToken ct)
    {
        await context.HttpContext.Response.SendStatusCodeAsync((int)HttpStatusCode.ServiceUnavailable, ct);
    }
}
