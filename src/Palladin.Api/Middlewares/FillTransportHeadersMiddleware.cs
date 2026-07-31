using Palladin.Core.Guid;
using Palladin.Core.Transport;

namespace Palladin.Api.Middlewares;

public sealed class FillTransportHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context, ITransportContext transportContext, IGuidProvider guidProvider)
    {
        var headers = context.Request.Headers.ToDictionary(x => x.Key, x => x.Value.ToString());
        if (!headers.ContainsKey(CustomHeaders.CorrelationIdHeaderName))
        {
            headers.Add(CustomHeaders.CorrelationIdHeaderName, guidProvider.Generate().ToString());
        }

        transportContext.Fill(headers);

        return next(context);
    }
}
