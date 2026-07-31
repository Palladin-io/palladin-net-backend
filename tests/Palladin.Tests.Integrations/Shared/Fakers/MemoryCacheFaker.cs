using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;

namespace Palladin.Tests.Integrations.Shared.Fakers;

public sealed class MemoryCacheFaker : IMemoryCache
{
    public ICacheEntry CreateEntry(object key)
    {
        return new NullCacheEntry { Key = key };
    }

    public void Dispose()
    {
    }

    public void Remove(object key)
    {
    }

    public bool TryGetValue(object key, out object? value)
    {
        value = null;
        return false;
    }

    private sealed class NullCacheEntry : ICacheEntry
    {
        public IList<IChangeToken> ExpirationTokens { get; set; } = new List<IChangeToken>();
        public IList<PostEvictionCallbackRegistration> PostEvictionCallbacks { get; set; } = new List<PostEvictionCallbackRegistration>();
        public CacheItemPriority Priority { get; set; }
        public TimeSpan? SlidingExpiration { get; set; }
        public DateTimeOffset? AbsoluteExpiration { get; set; }
        public TimeSpan? AbsoluteExpirationRelativeToNow { get; set; }
        public long? Size { get; set; }
        public object? Value { get; set; }
        public object Key { get; set; } = null!;

        public void Dispose()
        {
        }
    }
}
