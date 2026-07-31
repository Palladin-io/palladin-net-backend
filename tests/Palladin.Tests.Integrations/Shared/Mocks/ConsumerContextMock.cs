using MassTransit;
using NSubstitute;

namespace Palladin.Tests.Integrations.Shared.Mocks;

public static class ConsumerContextMock
{
    public static ConsumeContext<TObject> MockConsumeContext<TObject>(
        this ApiFactory apiFactory,
        TObject @object)
        where TObject : class
    {
        var mock = Substitute.For<ConsumeContext<TObject>>();

        mock.Message.Returns(@object);

        return mock;
    }
}
