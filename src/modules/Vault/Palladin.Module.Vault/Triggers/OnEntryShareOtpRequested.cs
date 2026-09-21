using JetBrains.Annotations;
using MassTransit;
using Palladin.Module.Vault.Contracts.Events;
using Palladin.Module.Vault.Features;
using Palladin.Module.Vault.Infrastructure.MassTransit;

namespace Palladin.Module.Vault.Triggers;

[UsedImplicitly]
internal sealed class OnEntryShareOtpRequestedDefinition : ConsumerDefinition<OnEntryShareOtpRequested>
{
    public OnEntryShareOtpRequestedDefinition() => EndpointName = VaultEndpoints.Self;
}

[UsedImplicitly]
internal sealed class OnEntryShareOtpRequested(EntryShareOtpDispatcher dispatcher) : IConsumer<EntryShareOtpRequestedEvent>
{
    public Task Consume(ConsumeContext<EntryShareOtpRequestedEvent> context) =>
        dispatcher.DispatchAsync(context.Message.ShareId, context.Message.SessionId,
            context.Message.Generation, context.CancellationToken);
}
