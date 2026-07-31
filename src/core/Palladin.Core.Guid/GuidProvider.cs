namespace Palladin.Core.Guid;

public interface IGuidProvider
{
    public System.Guid Generate();
}

internal sealed class GuidProvider(TimeProvider timeProvider) : IGuidProvider
{
    public System.Guid Generate() => System.Guid.CreateVersion7(timeProvider.GetUtcNow());
}
