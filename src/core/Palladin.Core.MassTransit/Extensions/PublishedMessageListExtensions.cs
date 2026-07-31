using MassTransit.Testing;

namespace Palladin.Core.MassTransit.Extensions;

public static class PublishedMessageListExtensions
{
    public static IEnumerable<TMessage> List<TMessage>(this IPublishedMessageList publishedMessageList)
        where TMessage : class =>
        publishedMessageList.Select<TMessage>().Select(x => x.Context.Message);

    public static async Task<bool> AnyAsync<T>(
        this IPublishedMessageList publishedMessages,
        FilterDelegate<IPublishedMessage<T>> filter,
        TimeSpan timeout = default,
        CancellationToken cancellationToken = default)
        where T : class
    {
        const int defaultInterval = 100;

        while (true)
        {
            var result = await publishedMessages.Any(filter, cancellationToken);
            if (result)
            {
                return true;
            }

            if (timeout <= TimeSpan.Zero)
            {
                return false;
            }

            await Task.Delay(defaultInterval, cancellationToken);
            timeout -= TimeSpan.FromMilliseconds(defaultInterval);
        }
    }
}
