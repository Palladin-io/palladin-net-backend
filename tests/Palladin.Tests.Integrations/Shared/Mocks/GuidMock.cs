using NSubstitute;

namespace Palladin.Tests.Integrations.Shared.Mocks;

public static class GuidMock
{
    public static void MockId(this ApiFactory apiFactory, Guid id)
    {
        var isFirstCall = true;
        apiFactory.GuidProvider.Generate().Returns(_ =>
        {
            if (!isFirstCall)
            {
                return Guid.NewGuid();
            }

            isFirstCall = false;
            return id;
        });
    }
}
