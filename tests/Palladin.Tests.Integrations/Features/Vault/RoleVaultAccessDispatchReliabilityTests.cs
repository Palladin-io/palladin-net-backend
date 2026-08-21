using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Palladin.Core.Events;
using Palladin.Core.Types;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Features;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Features;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Tests.Integrations.Shared;
using NodaTime;
using NodaTime.Testing;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Vault;

[Collection<ApiFactoryCollection>]
public sealed class RoleVaultAccessDispatchReliabilityTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_IdentityDispatchBatchContainsMultipleRows_Then_EveryPublishedRevisionIsPersisted()
    {
        // Given
        var jobClock = new FakeClock(apiFactory.FakeClock.GetCurrentInstant());
        var now = jobClock.GetCurrentInstant();
        var roleDispatches = Enumerable.Range(0, 2)
            .Select(_ => OrganizationRoleVaultAccessDispatch.Create(
                Guid.NewGuid(),
                Guid.NewGuid(),
                1,
                EntityChange.Created,
                isDeleted: false,
                isSystem: false,
                Palladin.Core.Security.Permission.VaultManage,
                now))
            .ToArray();
        var memberDispatches = Enumerable.Range(0, 2)
            .Select(_ => OrganizationMemberRoleSetDispatch.Create(
                Guid.NewGuid(),
                Guid.NewGuid(),
                [Guid.NewGuid()],
                1,
                1,
                isActive: true,
                now))
            .ToArray();
        await using (var seedScope = apiFactory.Services.CreateAsyncScope())
        {
            var writeContext = seedScope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();
            await writeContext.OrganizationRoleVaultAccessDispatches.ExecuteDeleteAsync(
                TestContext.Current.CancellationToken);
            await writeContext.OrganizationMemberRoleSetDispatches.ExecuteDeleteAsync(
                TestContext.Current.CancellationToken);
            writeContext.OrganizationRoleVaultAccessDispatches.AddRange(roleDispatches);
            writeContext.OrganizationMemberRoleSetDispatches.AddRange(memberDispatches);
            await writeContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var publisher = new RecordingEventPublisher();

        // When
        await using (var jobScope = apiFactory.Services.CreateAsyncScope())
        {
            var job = new DispatchVaultAccessReplicasJob(
                jobScope.ServiceProvider.GetRequiredService<IdentityDomainWriteContext>(),
                [publisher],
                Options.Create(new DispatchVaultAccessReplicasJobOptions { BatchSize = 10 }),
                jobClock,
                NullLogger<DispatchVaultAccessReplicasJob>.Instance);
            await job.ExecuteAsync(TestContext.Current.CancellationToken);
        }

        // Then
        await using var assertScope = apiFactory.Services.CreateAsyncScope();
        var readContext = assertScope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        var roleIds = roleDispatches.Select(x => x.RoleId).ToArray();
        var userIds = memberDispatches.Select(x => x.UserId).ToArray();
        (await readContext.OrganizationRoleVaultAccessDispatches
                .Where(x => roleIds.Contains(x.RoleId))
                .Select(x => x.PublishedRevision)
                .ToArrayAsync(TestContext.Current.CancellationToken))
            .ShouldAllBe(revision => revision == 1);
        (await readContext.OrganizationMemberRoleSetDispatches
                .Where(x => userIds.Contains(x.UserId))
                .Select(x => x.PublishedRevision)
                .ToArrayAsync(TestContext.Current.CancellationToken))
            .ShouldAllBe(revision => revision == 1);
        publisher.Events.Count.ShouldBe(4);
    }

    [Fact]
    public async Task When_IdentityBrokerRecoversAfterMoreThanTenRecordedFailures_Then_DispatchStillPublishes()
    {
        // Given
        var jobClock = new FakeClock(apiFactory.FakeClock.GetCurrentInstant());
        var dispatch = OrganizationRoleVaultAccessDispatch.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            EntityChange.Created,
            isDeleted: false,
            isSystem: false,
            Palladin.Core.Security.Permission.VaultManage,
            jobClock.GetCurrentInstant());
        await using (var seedScope = apiFactory.Services.CreateAsyncScope())
        {
            var writeContext = seedScope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();
            await writeContext.OrganizationRoleVaultAccessDispatches.ExecuteDeleteAsync(
                TestContext.Current.CancellationToken);
            await writeContext.OrganizationMemberRoleSetDispatches.ExecuteDeleteAsync(
                TestContext.Current.CancellationToken);
            writeContext.OrganizationRoleVaultAccessDispatches.Add(dispatch);
            await writeContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var publisher = new RecordingEventPublisher(failuresAfterRecording: 11);

        // When
        await using (var jobScope = apiFactory.Services.CreateAsyncScope())
        {
            var job = new DispatchVaultAccessReplicasJob(
                jobScope.ServiceProvider.GetRequiredService<IdentityDomainWriteContext>(),
                [publisher],
                Options.Create(new DispatchVaultAccessReplicasJobOptions { BatchSize = 1 }),
                jobClock,
                NullLogger<DispatchVaultAccessReplicasJob>.Instance);
            for (var attempt = 0; attempt < 12; attempt++)
            {
                await job.ExecuteAsync(TestContext.Current.CancellationToken);
                jobClock.AdvanceSeconds(301);
            }
        }

        // Then
        await using var assertScope = apiFactory.Services.CreateAsyncScope();
        var readContext = assertScope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        var persisted = await readContext.OrganizationRoleVaultAccessDispatches.SingleAsync(
            x => x.OrganizationId == dispatch.OrganizationId && x.RoleId == dispatch.RoleId,
            TestContext.Current.CancellationToken);
        persisted.PublishAttempts.ShouldBe(11);
        persisted.PublishedRevision.ShouldBe(1UL);
        persisted.LastErrorCode.ShouldBeNull();
        publisher.Events.Count.ShouldBe(12);
    }

    [Fact]
    public async Task When_IdentitySnapshotWasAcknowledgedButConsumerDidNotPersist_Then_AntiEntropyRepublishesFullState()
    {
        // Given
        var jobClock = new FakeClock(apiFactory.FakeClock.GetCurrentInstant());
        var initiallyPublishedAt = jobClock.GetCurrentInstant();
        var roleDispatch = OrganizationRoleVaultAccessDispatch.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            EntityChange.Created,
            isDeleted: false,
            isSystem: false,
            Palladin.Core.Security.Permission.VaultManage,
            initiallyPublishedAt);
        roleDispatch.MarkPublished(1, initiallyPublishedAt);
        var deletedRoleDispatch = OrganizationRoleVaultAccessDispatch.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            2,
            EntityChange.Updated,
            isDeleted: true,
            isSystem: false,
            Palladin.Core.Security.Permission.VaultManage,
            initiallyPublishedAt);
        deletedRoleDispatch.MarkPublished(2, initiallyPublishedAt);
        var memberDispatch = OrganizationMemberRoleSetDispatch.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            [Guid.NewGuid()],
            1,
            1,
            isActive: true,
            initiallyPublishedAt);
        memberDispatch.MarkPublished(1, initiallyPublishedAt);
        await using (var seedScope = apiFactory.Services.CreateAsyncScope())
        {
            var writeContext = seedScope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();
            await writeContext.OrganizationRoleVaultAccessDispatches.ExecuteDeleteAsync(
                TestContext.Current.CancellationToken);
            await writeContext.OrganizationMemberRoleSetDispatches.ExecuteDeleteAsync(
                TestContext.Current.CancellationToken);
            writeContext.OrganizationRoleVaultAccessDispatches.AddRange(roleDispatch, deletedRoleDispatch);
            writeContext.OrganizationMemberRoleSetDispatches.Add(memberDispatch);
            await writeContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        jobClock.Advance(Duration.FromMinutes(16));
        var publisher = new RecordingEventPublisher();

        // When
        await using (var jobScope = apiFactory.Services.CreateAsyncScope())
        {
            var job = new DispatchVaultAccessReplicasJob(
                jobScope.ServiceProvider.GetRequiredService<IdentityDomainWriteContext>(),
                [publisher],
                Options.Create(new DispatchVaultAccessReplicasJobOptions
                {
                    BatchSize = 10,
                    RepairIntervalMinutes = 15,
                }),
                jobClock,
                NullLogger<DispatchVaultAccessReplicasJob>.Instance);
            await job.ExecuteAsync(TestContext.Current.CancellationToken);
            await job.ExecuteAsync(TestContext.Current.CancellationToken);
        }

        // Then
        publisher.Events.Count.ShouldBe(3);
        publisher.Events.ShouldContain(x => x is Palladin.Module.Identity.Contracts.Events.OrganizationRoleUpsertedEvent);
        publisher.Events.ShouldContain(x => x is Palladin.Module.Identity.Contracts.Events.OrganizationRoleDeletedEvent);
        publisher.Events.ShouldContain(x => x is Palladin.Module.Identity.Contracts.Events.OrganizationMemberRolesUpsertedEvent);
        await using var assertScope = apiFactory.Services.CreateAsyncScope();
        var readContext = assertScope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        (await readContext.OrganizationRoleVaultAccessDispatches.SingleAsync(
            x => x.OrganizationId == roleDispatch.OrganizationId && x.RoleId == roleDispatch.RoleId,
            TestContext.Current.CancellationToken)).PublishedAt.ShouldBe(jobClock.GetCurrentInstant());
        (await readContext.OrganizationRoleVaultAccessDispatches.SingleAsync(
            x => x.OrganizationId == deletedRoleDispatch.OrganizationId
                 && x.RoleId == deletedRoleDispatch.RoleId,
            TestContext.Current.CancellationToken)).PublishedAt.ShouldBe(jobClock.GetCurrentInstant());
        (await readContext.OrganizationMemberRoleSetDispatches.SingleAsync(
            x => x.OrganizationId == memberDispatch.OrganizationId && x.UserId == memberDispatch.UserId,
            TestContext.Current.CancellationToken)).PublishedAt.ShouldBe(jobClock.GetCurrentInstant());
    }

    [Fact]
    public async Task When_IdentityRepairBatchIsBounded_Then_OldestPublishedSnapshotMakesProgressFirst()
    {
        // Given: the older source row has the newer repair watermark, so ordering by NextAttemptAt
        // would repeatedly starve the snapshot that has waited longest since publication.
        var jobClock = new FakeClock(apiFactory.FakeClock.GetCurrentInstant());
        var now = jobClock.GetCurrentInstant();
        var olderSourceNewerPublish = OrganizationRoleVaultAccessDispatch.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            EntityChange.Created,
            isDeleted: false,
            isSystem: false,
            Palladin.Core.Security.Permission.VaultManage,
            now - Duration.FromHours(2));
        olderSourceNewerPublish.MarkPublished(1, now - Duration.FromMinutes(16));
        var newerSourceOlderPublish = OrganizationRoleVaultAccessDispatch.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            EntityChange.Created,
            isDeleted: false,
            isSystem: false,
            Palladin.Core.Security.Permission.VaultManage,
            now - Duration.FromHours(1));
        newerSourceOlderPublish.MarkPublished(1, now - Duration.FromMinutes(30));
        await using (var seedScope = apiFactory.Services.CreateAsyncScope())
        {
            var writeContext = seedScope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();
            await writeContext.OrganizationRoleVaultAccessDispatches.ExecuteDeleteAsync(
                TestContext.Current.CancellationToken);
            await writeContext.OrganizationMemberRoleSetDispatches.ExecuteDeleteAsync(
                TestContext.Current.CancellationToken);
            writeContext.OrganizationRoleVaultAccessDispatches.AddRange(
                olderSourceNewerPublish,
                newerSourceOlderPublish);
            await writeContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var publisher = new RecordingEventPublisher();

        // When
        await using (var jobScope = apiFactory.Services.CreateAsyncScope())
        {
            var job = new DispatchVaultAccessReplicasJob(
                jobScope.ServiceProvider.GetRequiredService<IdentityDomainWriteContext>(),
                [publisher],
                Options.Create(new DispatchVaultAccessReplicasJobOptions
                {
                    BatchSize = 1,
                    RepairIntervalMinutes = 15,
                }),
                jobClock,
                NullLogger<DispatchVaultAccessReplicasJob>.Instance);
            await job.ExecuteAsync(TestContext.Current.CancellationToken);
        }

        // Then
        publisher.Events.Count.ShouldBe(1);
        ((Palladin.Module.Identity.Contracts.Events.OrganizationRoleUpsertedEvent)publisher.Events[0])
            .RoleId.ShouldBe(newerSourceOlderPublish.RoleId);
    }

    [Fact]
    public async Task When_VaultDispatchBatchContainsMultipleRows_Then_EveryPublishedRevisionIsPersisted()
    {
        // Given
        var jobClock = new FakeClock(apiFactory.FakeClock.GetCurrentInstant());
        var now = jobClock.GetCurrentInstant();
        var dispatches = Enumerable.Range(0, 2)
            .Select(_ => RoleVaultAccessPolicyDispatch.Create(
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                1,
                Guid.NewGuid(),
                [Guid.NewGuid()],
                now))
            .ToArray();
        await using (var seedScope = apiFactory.Services.CreateAsyncScope())
        {
            var writeContext = seedScope.ServiceProvider.GetRequiredService<VaultDbWriteContext>();
            await writeContext.RoleVaultAccessPolicyDispatches.ExecuteDeleteAsync(
                TestContext.Current.CancellationToken);
            writeContext.RoleVaultAccessPolicyDispatches.AddRange(dispatches);
            await writeContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var publisher = new RecordingEventPublisher();

        // When
        await using (var jobScope = apiFactory.Services.CreateAsyncScope())
        {
            var job = new DispatchRoleVaultAccessPoliciesJob(
                jobScope.ServiceProvider.GetRequiredService<VaultDomainWriteContext>(),
                [publisher],
                Options.Create(new DispatchRoleVaultAccessPoliciesJobOptions { BatchSize = 10 }),
                jobClock,
                NullLogger<DispatchRoleVaultAccessPoliciesJob>.Instance);
            await job.ExecuteAsync(TestContext.Current.CancellationToken);
        }

        // Then
        await using var assertScope = apiFactory.Services.CreateAsyncScope();
        var readContext = assertScope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var roleIds = dispatches.Select(x => x.RoleId).ToArray();
        (await readContext.RoleVaultAccessPolicyDispatches
                .Where(x => roleIds.Contains(x.RoleId))
                .Select(x => x.PublishedRevision)
                .ToArrayAsync(TestContext.Current.CancellationToken))
            .ShouldAllBe(revision => revision == 1);
        publisher.Events.Count.ShouldBe(2);
    }

    [Fact]
    public async Task When_VaultBrokerRecoversAfterMoreThanTenRecordedFailures_Then_DispatchStillPublishes()
    {
        // Given
        var jobClock = new FakeClock(apiFactory.FakeClock.GetCurrentInstant());
        var dispatch = RoleVaultAccessPolicyDispatch.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            Guid.NewGuid(),
            [Guid.NewGuid()],
            jobClock.GetCurrentInstant());
        await using (var seedScope = apiFactory.Services.CreateAsyncScope())
        {
            var writeContext = seedScope.ServiceProvider.GetRequiredService<VaultDbWriteContext>();
            await writeContext.RoleVaultAccessPolicyDispatches.ExecuteDeleteAsync(
                TestContext.Current.CancellationToken);
            writeContext.RoleVaultAccessPolicyDispatches.Add(dispatch);
            await writeContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var publisher = new RecordingEventPublisher(failuresAfterRecording: 11);

        // When
        await using (var jobScope = apiFactory.Services.CreateAsyncScope())
        {
            var job = new DispatchRoleVaultAccessPoliciesJob(
                jobScope.ServiceProvider.GetRequiredService<VaultDomainWriteContext>(),
                [publisher],
                Options.Create(new DispatchRoleVaultAccessPoliciesJobOptions { BatchSize = 1 }),
                jobClock,
                NullLogger<DispatchRoleVaultAccessPoliciesJob>.Instance);
            for (var attempt = 0; attempt < 12; attempt++)
            {
                await job.ExecuteAsync(TestContext.Current.CancellationToken);
                jobClock.AdvanceSeconds(301);
            }
        }

        // Then
        await using var assertScope = apiFactory.Services.CreateAsyncScope();
        var readContext = assertScope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var persisted = await readContext.RoleVaultAccessPolicyDispatches.SingleAsync(
            x => x.OrganizationId == dispatch.OrganizationId && x.RoleId == dispatch.RoleId,
            TestContext.Current.CancellationToken);
        persisted.PublishAttempts.ShouldBe(11);
        persisted.PublishedRevision.ShouldBe(1UL);
        persisted.LastErrorCode.ShouldBeNull();
        publisher.Events.Count.ShouldBe(12);
    }

    private sealed class RecordingEventPublisher(int failuresAfterRecording = 0) : IEventPublisher
    {
        private int _failuresRemaining = failuresAfterRecording;

        public List<IEvent> Events { get; } = [];

        public Task PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken)
            where TEvent : IEvent
        {
            Events.Add(@event);
            if (_failuresRemaining > 0)
            {
                _failuresRemaining--;
                throw new InvalidOperationException("Simulated crash after broker accepted the event.");
            }

            return Task.CompletedTask;
        }
    }
}
