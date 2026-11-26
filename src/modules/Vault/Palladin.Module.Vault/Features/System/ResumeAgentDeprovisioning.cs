using JetBrains.Annotations;
using MassTransit;
using NodaTime;
using Palladin.Module.Vault.Contracts.Commands;
using Palladin.Module.Vault.Infrastructure.MassTransit;

namespace Palladin.Module.Vault.Features;

[UsedImplicitly]
internal sealed class ResumeAgentDeprovisioningConsumerDefinition
    : ConsumerDefinition<ResumeAgentDeprovisioningConsumer>
{
    public ResumeAgentDeprovisioningConsumerDefinition() => EndpointName = VaultEndpoints.Deprovisioning;
}

[PublicAPI]
internal sealed class ResumeAgentDeprovisioningConsumer(
    VaultPrincipalDeprovisioningCoordinator coordinator,
    IClock clock) : IConsumer<ResumeAgentDeprovisioningCommand>
{
    public Task Consume(ConsumeContext<ResumeAgentDeprovisioningCommand> context) =>
        coordinator.ResumeAgentAsync(
            context.Message.OrganizationId,
            context.Message.OperationId,
            clock.GetCurrentInstant(),
            context.CancellationToken);
}
