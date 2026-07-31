using Palladin.Tests.Integrations.Shared.Mocks;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Palladin.Tests.Integrations.Shared.Extensions;

public static class ApiFactoryExtensions
{
    public static async Task ConsumeAsync<TConsumer, TMessage>(
        this ApiFactory apiFactory,
        TMessage message,
        CancellationToken cancellationToken = default)
        where TConsumer : class, IConsumer<TMessage>
        where TMessage : class
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var consumer = scope.ServiceProvider.GetRequiredService<TConsumer>();
        var context = apiFactory.MockConsumeContext(message);
        await consumer.Consume(context);
    }
}
