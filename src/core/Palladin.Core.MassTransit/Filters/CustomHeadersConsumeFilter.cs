using Palladin.Core.Transport;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;

namespace Palladin.Core.MassTransit.Filters;

public sealed class CustomHeadersConsumeFilter<T> : IFilter<ConsumeContext<T>>
    where T : class
{
    public void Probe(ProbeContext context)
    {
    }

    public async Task Send(ConsumeContext<T> context, IPipe<ConsumeContext<T>> next)
    {
        var serviceProvider = context.GetPayload<IServiceProvider>();
        var transportContext = serviceProvider.GetRequiredService<ITransportContext>();

        var correlationId = context.CorrelationId ?? Guid.NewGuid();
        var headers = context.Headers.ToDictionary(x => x.Key, x => x.Value.ToString() ?? string.Empty);
        headers[CustomHeaders.CorrelationIdHeaderName] = correlationId.ToString();

        transportContext.Fill(headers);

        await next.Send(context);
    }
}
