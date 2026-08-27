using MassTransit;
using NSubstitute;
using Palladin.Core.Types;
using Palladin.Module.Audit.Contracts.Commands;
using Palladin.Module.Vault.Contracts.Events;
using Palladin.Module.Vault.Triggers;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Mocks;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Vault;

[Collection<ApiFactoryCollection>]
public sealed class OnGrantSupersededTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_GrantSuperseded_Then_AuditUsesExplicitEventType()
    {
        // Given
        AppendAuditLogCommand? captured = null;
        var publishEndpoint = Substitute.For<IPublishEndpoint>();
        publishEndpoint.When(p => p.Publish(Arg.Any<AppendAuditLogCommand>(), Arg.Any<CancellationToken>()))
            .Do(call => captured = call.Arg<AppendAuditLogCommand>());
        var @event = new GrantSupersededEvent(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            GrantType.Full, "agent", "entry", "vault", 60, apiFactory.FakeClock.GetCurrentInstant());

        // When
        await new OnGrantSupersededAudit(publishEndpoint).Consume(apiFactory.MockConsumeContext(@event));

        // Then
        captured.ShouldNotBeNull();
        captured.Metadata["grantType"].ShouldBe(GrantType.Full.ToString());
    }
}
