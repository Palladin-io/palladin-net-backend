using Palladin.Core.Analytics;
using Palladin.Core.Types;
using Palladin.Module.Audit.Features;
using Palladin.Module.Audit.Contracts.Commands;
using Palladin.Module.Audit.Contracts.ValueObjects;
using Palladin.Module.Audit.Infrastructure.Persistence;
using Palladin.Module.Vault.Contracts.Events;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Triggers;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Mocks;
using Palladin.Tests.Integrations.Shared.Seeders;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Vault;

[Collection<ApiFactoryCollection>]
public sealed class OnEntryUpsertedTests(ApiFactory apiFactory) : TestBase
{
    private EntryUpsertedEvent CreatedEvent(Guid vaultId, Guid userId, bool viaImport) =>
        new(Guid.NewGuid(), vaultId, Guid.NewGuid(), userId, EntityChange.Created, 1,
            apiFactory.FakeClock.GetCurrentInstant(), viaImport);

    [Fact]
    public async Task When_EntryCreatedViaImport_Then_SkipsPerEntryAnalytics()
    {
        // Given — a Created event that arrived through bulk import
        var analytics = Substitute.For<IAnalyticsService>();
        var evt = CreatedEvent(Guid.NewGuid(), Guid.NewGuid(), viaImport: true);

        // When
        await new OnEntryUpserted(analytics).Consume(apiFactory.MockConsumeContext(evt));

        // Then — the batch entries-imported event counts it; no per-entry capture
        analytics.DidNotReceive().CaptureEvent(
            Arg.Any<string>(), Arg.Any<string>(), "entry-created", Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task When_EntryCreatedNotViaImport_Then_FiresPerEntryAnalytics()
    {
        // Given — a normal (non-import) Created event
        var analytics = Substitute.For<IAnalyticsService>();
        var userId = Guid.NewGuid();
        var evt = CreatedEvent(Guid.NewGuid(), userId, viaImport: false);

        // When
        await new OnEntryUpserted(analytics).Consume(apiFactory.MockConsumeContext(evt));

        // Then
        analytics.Received(1).CaptureEvent(
            userId.ToString(), "vault", "entry-created", Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task When_EntryCreatedViaImport_Then_AuditStillWritesPerEntry()
    {
        // Given — the audit trigger must ignore ViaImport: imported entries still get an audit row
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var evt = CreatedEvent(vault.Id, user.Id, viaImport: true);

        AppendAuditLogCommand? captured = null;
        var publishEndpoint = Substitute.For<IPublishEndpoint>();
        publishEndpoint.When(p => p.Publish(Arg.Any<AppendAuditLogCommand>(), Arg.Any<CancellationToken>()))
            .Do(call => captured = call.Arg<AppendAuditLogCommand>());

        // When
        await new OnEntryUpsertedAudit(publishEndpoint).Consume(apiFactory.MockConsumeContext(evt));

        captured.ShouldNotBeNull();
        await apiFactory.ConsumeAsync<AppendAuditLogConsumer, AppendAuditLogCommand>(captured);

        // Then
        await using var verifyScope = apiFactory.Services.CreateAsyncScope();
        var auditContext = verifyScope.ServiceProvider.GetRequiredService<AuditDbReadContext>();
        var entry = await auditContext.AuditLogEntries.FirstOrDefaultAsync(
            e => e.EventType == AuditEventType.EntryCreated && e.EntryId == evt.EntryId);
        entry.ShouldNotBeNull();
        entry.Result.ShouldBe(AuditResult.Succeeded);
        entry.Metadata["revision"].ShouldBe("1");
    }
}
