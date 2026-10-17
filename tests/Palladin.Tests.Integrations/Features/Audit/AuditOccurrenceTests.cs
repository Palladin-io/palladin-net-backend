using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Palladin.Module.Audit.Contracts.Commands;
using Palladin.Module.Audit.Contracts.ValueObjects;
using Palladin.Module.Audit.Features;
using Palladin.Module.Audit.Infrastructure.Persistence;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Audit;

[Collection<ApiFactoryCollection>]
public sealed class AuditOccurrenceTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_AnOccurrenceHasNoExplicitId_Then_TheNaturalKeyStillDeduplicatesIndependently()
    {
        // Given
        var command = new AppendAuditLogCommand(Guid.NewGuid(), AuditEventType.EntryShareDelivered,
            AuditActorType.ExternalRecipient, AuditResult.Succeeded, apiFactory.FakeClock.GetCurrentInstant(),
            null, null, Guid.NewGuid(), Guid.NewGuid(), null, null, null,
            new Dictionary<string, string>(), Guid.NewGuid());

        // When
        await apiFactory.ConsumeAsync<AppendAuditLogConsumer, AppendAuditLogCommand>(command);
        await apiFactory.ConsumeAsync<AppendAuditLogConsumer, AppendAuditLogCommand>(command with { IdempotencyKey = null });
        await apiFactory.ConsumeAsync<AppendAuditLogConsumer, AppendAuditLogCommand>(command with { IdempotencyKey = null });

        // Then
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AuditDbReadContext>();
        var rows = await database.AuditLogEntries.Where(x => x.OrganizationId == command.OrganizationId).ToListAsync();
        rows.Count.ShouldBe(2);
        rows.Count(x => x.HasExplicitOccurrenceId).ShouldBe(1);
    }

    [Fact]
    public async Task When_DifferentExplicitOccurrencesHaveTheSameTimestamp_Then_BothSurviveAndRetriesDeduplicate()
    {
        // Given
        var command = new AppendAuditLogCommand(Guid.NewGuid(), "entry-share.delivered",
            AuditActorType.System, AuditResult.Succeeded, apiFactory.FakeClock.GetCurrentInstant(),
            null, null, Guid.NewGuid(), Guid.NewGuid(), null, null, null,
            new Dictionary<string, string>(), Guid.NewGuid());
        var second = command with { IdempotencyKey = Guid.NewGuid() };

        // When
        await apiFactory.ConsumeAsync<AppendAuditLogConsumer, AppendAuditLogCommand>(command);
        await apiFactory.ConsumeAsync<AppendAuditLogConsumer, AppendAuditLogCommand>(second);
        await apiFactory.ConsumeAsync<AppendAuditLogConsumer, AppendAuditLogCommand>(command);
        await apiFactory.ConsumeAsync<AppendAuditLogConsumer, AppendAuditLogCommand>(second);

        // Then
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AuditDbReadContext>();
        var ids = await database.AuditLogEntries.Where(x => x.OrganizationId == command.OrganizationId)
            .Select(x => x.Id).ToListAsync();
        ids.Count.ShouldBe(2);
        ids.ShouldContain(command.IdempotencyKey!.Value);
        ids.ShouldContain(second.IdempotencyKey!.Value);
    }
}
