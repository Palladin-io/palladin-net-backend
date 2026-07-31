using JetBrains.Annotations;
using MassTransit.Testing;

namespace Palladin.Core.MassTransit.Extensions;

[PublicAPI]
public static class ReceivedMessageExtensions
{
    private const int DefaultInterval = 100;

    public static async Task<bool> AnyAsync<T>(
        this IReceivedMessageList receivedMessageList,
        FilterDelegate<IReceivedMessage<T>> filter,
        TimeSpan timeout,
        CancellationToken cancellationToken = default
    )
        where T : class
    {
        while (true)
        {
            var result = await receivedMessageList.Any(filter, cancellationToken);
            if (result)
            {
                return true;
            }

            if (timeout <= TimeSpan.Zero)
            {
                return false;
            }

            await Task.Delay(DefaultInterval, cancellationToken);
            timeout -= TimeSpan.FromMilliseconds(DefaultInterval);
        }
    }

    public static async Task<bool> AnyAsync<T>(
        this IReceivedMessageList receivedMessageList,
        FilterDelegate<IReceivedMessage<T>> filter,
        CancellationToken cancellationToken
    )
        where T : class
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var result = await receivedMessageList.Any(filter, cancellationToken);
            if (result)
            {
                return true;
            }

            await Task.Delay(DefaultInterval, cancellationToken);
        }

        return false;
    }
}
