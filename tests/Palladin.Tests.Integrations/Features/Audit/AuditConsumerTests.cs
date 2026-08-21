using Palladin.Core.Types;
using Palladin.Module.Agents.Contracts.Events;
using Palladin.Module.Agents.Triggers;
using Palladin.Module.Audit.Features;
using Palladin.Module.Audit.Domain;
using Palladin.Module.Audit.Contracts.Commands;
using Palladin.Module.Audit.Contracts.ValueObjects;
using Palladin.Module.Audit.Infrastructure.Persistence;
using Palladin.Module.Identity.Contracts.Events;
using Palladin.Module.Identity.Triggers;
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
using NodaTime;
using NSubstitute;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Audit;

// The owning module (Vault / Agents / Identity) translates its OWN domain event into Audit's
// AppendAuditLogCommand — Audit never subscribes to those events. These tests run the full path: the
// owning trigger captures the command it would publish, then Audit's real AppendAuditLogConsumer writes
// the row. Assertions verify opaque references and permitted forensic attribution are persisted.
[Collection<ApiFactoryCollection>]
public sealed class AuditConsumerTests(ApiFactory apiFactory) : TestBase
{
    private async Task RunAsync<TEvent>(Func<IPublishEndpoint, IConsumer<TEvent>> triggerFactory, TEvent @event)
        where TEvent : class
    {
        AppendAuditLogCommand? captured = null;
        var publishEndpoint = Substitute.For<IPublishEndpoint>();
        publishEndpoint.When(p => p.Publish(Arg.Any<AppendAuditLogCommand>(), Arg.Any<CancellationToken>()))
            .Do(call => captured = call.Arg<AppendAuditLogCommand>());

        await triggerFactory(publishEndpoint).Consume(apiFactory.MockConsumeContext(@event));

        if (captured is not null)
        {
            await apiFactory.ConsumeAsync<AppendAuditLogConsumer, AppendAuditLogCommand>(captured);
        }
    }

    private async Task<AuditLogEntry?> FindAsync(string eventType, Guid organizationId)
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<AuditDbReadContext>();
        return await readContext.AuditLogEntries
            .FirstOrDefaultAsync(e => e.EventType == eventType && e.OrganizationId == organizationId);
    }

    [Fact]
    public async Task When_GrantCreatedConsumed_Then_WritesOpaqueEntryReference()
    {
        // Given
        var orgId = Guid.NewGuid();
        var creator = Guid.NewGuid();
        var entryId = Guid.NewGuid();
        var evt = new GrantCreatedEvent(Guid.NewGuid(), Guid.NewGuid(), orgId, Guid.NewGuid(), entryId,
            GrantType.Granular, GrantStatus.Active, "uses", null, 5, GrantMethods.Get, creator,
            "agent", "entry", "vault", "actor", apiFactory.FakeClock.GetCurrentInstant());

        // When
        await RunAsync(p => new OnGrantCreatedAudit(p), evt);

        // Then
        var entry = await FindAsync(AuditEventType.GrantCreated, orgId);
        entry.ShouldNotBeNull();
        entry.ActorType.ShouldBe(AuditActorType.User);
        entry.UserId.ShouldBe(creator);
        entry.EntryId.ShouldBe(entryId);
        entry.Result.ShouldBe(AuditResult.Succeeded);
        entry.AgentName.ShouldBe("agent");
        entry.ActorName.ShouldBe("actor");
    }

    [Fact]
    public async Task When_GrantApprovedConsumed_Then_WritesUserActorEntry()
    {
        // Given
        var orgId = Guid.NewGuid();
        var approver = Guid.NewGuid();
        var evt = new GrantApprovedEvent(Guid.NewGuid(), Guid.NewGuid(), orgId, Guid.NewGuid(), Guid.NewGuid(), approver,
            GrantType.Granular, "agent", "entry", "vault", "actor",
            "time", apiFactory.FakeClock.GetCurrentInstant().Plus(Duration.FromHours(1)), null, GrantMethods.Get,
            apiFactory.FakeClock.GetCurrentInstant());

        // When
        await RunAsync(p => new OnGrantApprovedAudit(p), evt);

        // Then
        var entry = await FindAsync(AuditEventType.GrantApproved, orgId);
        entry.ShouldNotBeNull();
        entry.ActorType.ShouldBe(AuditActorType.User);
        entry.UserId.ShouldBe(approver);
    }

    [Fact]
    public async Task When_CredentialAccessedConsumed_Then_WritesAgentActorEntry()
    {
        // Given
        var orgId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var evt = new CredentialAccessedEvent(Guid.NewGuid(), Guid.NewGuid(), orgId, agentId, Guid.NewGuid(),
            GrantType.Granular, "agent", "entry", "vault", null, 3, GrantMethods.Get, apiFactory.FakeClock.GetCurrentInstant());

        // When
        await RunAsync(p => new OnCredentialAccessedAudit(p), evt);

        // Then
        var entry = await FindAsync(AuditEventType.CredentialAccessed, orgId);
        entry.ShouldNotBeNull();
        entry.ActorType.ShouldBe(AuditActorType.Agent);
        entry.AgentId.ShouldBe(agentId);
    }

    [Fact]
    public async Task When_GrantExpiredConsumed_Then_ResolvesOrgFromVaultAndWritesSystemEntry()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var evt = new GrantExpiredEvent(Guid.NewGuid(), vault.Id, organization.Id, Guid.NewGuid(), Guid.NewGuid(),
            "entry", GrantType.Granular, 3600, apiFactory.FakeClock.GetCurrentInstant());

        // When
        await RunAsync(p => new OnGrantExpiredAudit(p), evt);

        // Then
        var entry = await FindAsync(AuditEventType.GrantExpired, organization.Id);
        entry.ShouldNotBeNull();
        entry.ActorType.ShouldBe(AuditActorType.System);
        entry.VaultId.ShouldBe(vault.Id);
    }

    [Fact]
    public async Task When_GrantRevokedBySystemConsumed_Then_ActorIsSystem()
    {
        // Given
        var orgId = Guid.NewGuid();
        var evt = new GrantRevokedEvent(Guid.NewGuid(), Guid.NewGuid(), orgId, null, null, GrantType.Full, null, true,
            "agent", null, "vault", null, 120, apiFactory.FakeClock.GetCurrentInstant());

        // When
        await RunAsync(p => new OnGrantRevokedAudit(p), evt);

        // Then
        var entry = await FindAsync(AuditEventType.GrantRevoked, orgId);
        entry.ShouldNotBeNull();
        entry.ActorType.ShouldBe(AuditActorType.System);
        entry.UserId.ShouldBeNull();
    }

    [Fact]
    public async Task When_AgentUpsertedActive_Then_NoEntry()
    {
        // Given
        var orgId = Guid.NewGuid();
        var now = apiFactory.FakeClock.GetCurrentInstant();
        var evt = new AgentUpsertedEvent(Guid.NewGuid(), orgId, AgentStatus.Active, "pk", 1, "signing-pk", "Claude", "ci", null, null,
            1, now, now);

        // When
        await RunAsync(p => new OnAgentUpsertedAudit(p), evt);

        // Then
        (await FindAsync(AuditEventType.AgentEnrolled, orgId)).ShouldBeNull();
    }

    [Fact]
    public async Task When_AgentReactivatedConsumed_Then_WritesUserActorEntryAttributedToOperator()
    {
        // Given
        var orgId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var reactivatedBy = Guid.NewGuid();
        var evt = new AgentReactivatedEvent(agentId, orgId, reactivatedBy, "Operator", "Claude Agent",
            apiFactory.FakeClock.GetCurrentInstant());

        // When
        await RunAsync(p => new OnAgentReactivatedAudit(p), evt);

        // Then
        var entry = await FindAsync(AuditEventType.AgentReactivated, orgId);
        entry.ShouldNotBeNull();
        entry.ActorType.ShouldBe(AuditActorType.User);
        entry.UserId.ShouldBe(reactivatedBy);
        entry.AgentId.ShouldBe(agentId);
    }

    [Fact]
    public async Task When_SameEventConsumedTwice_Then_SingleEntry()
    {
        // Given
        var orgId = Guid.NewGuid();
        var evt = new GrantDeniedEvent(Guid.NewGuid(), Guid.NewGuid(), orgId, Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), "agent", "entry", "vault", "actor", GrantMethods.Get, apiFactory.FakeClock.GetCurrentInstant());

        // When
        await RunAsync(p => new OnGrantDeniedAudit(p), evt);
        await RunAsync(p => new OnGrantDeniedAudit(p), evt);

        // Then
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<AuditDbReadContext>();
        var count = await readContext.AuditLogEntries.CountAsync(
            e => e.EventType == AuditEventType.GrantDenied && e.OrganizationId == orgId);
        count.ShouldBe(1);
    }

    [Fact]
    public async Task When_EventWithNullFieldsConsumedTwice_Then_SingleEntry()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var evt = new GrantExpiredEvent(Guid.NewGuid(), vault.Id, organization.Id, null, null,
            null, GrantType.Granular, 3600, apiFactory.FakeClock.GetCurrentInstant());

        // When
        await RunAsync(p => new OnGrantExpiredAudit(p), evt);
        await RunAsync(p => new OnGrantExpiredAudit(p), evt);

        // Then
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<AuditDbReadContext>();
        var count = await readContext.AuditLogEntries.CountAsync(
            e => e.EventType == AuditEventType.GrantExpired && e.VaultId == vault.Id);
        count.ShouldBe(1);
    }

    [Fact]
    public async Task When_CredentialAccessedConsumed_Then_MetadataHasNoSecrets()
    {
        // Given
        var orgId = Guid.NewGuid();
        var evt = new CredentialAccessedEvent(Guid.NewGuid(), Guid.NewGuid(), orgId, Guid.NewGuid(), Guid.NewGuid(),
            GrantType.Granular, "agent", "entry", "vault", null, 1, GrantMethods.Get, apiFactory.FakeClock.GetCurrentInstant());

        // When
        await RunAsync(p => new OnCredentialAccessedAudit(p), evt);

        // Then
        var entry = await FindAsync(AuditEventType.CredentialAccessed, orgId);
        entry.ShouldNotBeNull();
        var blob = string.Join("|", entry.Metadata.Keys).ToLowerInvariant();
        blob.ShouldNotContain("reencryptedblob");
        blob.ShouldNotContain("agentwrappeddek");
        blob.ShouldNotContain("nonce");
    }

    [Fact]
    public async Task When_VaultUpdatedConsumed_Then_WritesOrgScopedUserEntry()
    {
        // Given
        var orgId = Guid.NewGuid();
        var vaultId = Guid.NewGuid();
        var updatedBy = Guid.NewGuid();
        var evt = new VaultUpsertedEvent(vaultId, orgId, updatedBy, "Vera Updater", false,
            EntityChange.Updated, apiFactory.FakeClock.GetCurrentInstant());

        // When
        await RunAsync(p => new OnVaultUpsertedAudit(p), evt);

        // Then
        var entry = await FindAsync(AuditEventType.VaultUpdated, orgId);
        entry.ShouldNotBeNull();
        entry.ActorType.ShouldBe(AuditActorType.User);
        entry.UserId.ShouldBe(updatedBy);
        entry.ActorName.ShouldBe("Vera Updater");
        entry.VaultId.ShouldBe(vaultId);
        entry.Metadata.ShouldBeEmpty();
    }

    [Fact]
    public async Task When_VaultDeletedConsumed_Then_WritesOnlyStructuralMetadata()
    {
        // Given
        var orgId = Guid.NewGuid();
        var vaultId = Guid.NewGuid();
        var evt = new VaultDeletedEvent(vaultId, Guid.NewGuid(), orgId, "Dan Deleter", [Guid.NewGuid()],
            12, 24, apiFactory.FakeClock.GetCurrentInstant());

        // When
        await RunAsync(p => new OnVaultDeletedAudit(p), evt);

        // Then
        var entry = await FindAsync(AuditEventType.VaultDeleted, orgId);
        entry.ShouldNotBeNull();
        entry.VaultId.ShouldBe(vaultId);
        entry.Metadata.Keys.ShouldNotContain("name");
        entry.Metadata["memberCount"].ShouldBe("1");
    }

    [Fact]
    public async Task When_OrganizationCreatedConsumed_Then_WritesEntryWithActorName()
    {
        // Given
        var orgId = Guid.NewGuid();
        var createdBy = Guid.NewGuid();
        var evt = new OrganizationCreatedEvent(orgId, "Acme Inc", createdBy, "Alice",
            apiFactory.FakeClock.GetCurrentInstant());

        // When
        await RunAsync(p => new OnOrganizationCreatedAudit(p), evt);

        // Then
        var entry = await FindAsync(AuditEventType.OrganizationCreated, orgId);
        entry.ShouldNotBeNull();
        entry.ActorType.ShouldBe(AuditActorType.User);
        entry.UserId.ShouldBe(createdBy);
        entry.ActorName.ShouldBe("Alice");
        entry.Metadata["name"].ShouldBe("Acme Inc");
    }

    [Fact]
    public async Task When_OrganizationUpdatedConsumed_Then_WritesEntryWithActorName()
    {
        // Given
        var orgId = Guid.NewGuid();
        var updatedBy = Guid.NewGuid();
        var evt = new OrganizationUpdatedEvent(orgId, "Renamed", updatedBy, "Bob", ["name"],
            apiFactory.FakeClock.GetCurrentInstant());

        // When
        await RunAsync(p => new OnOrganizationUpdatedAudit(p), evt);

        // Then
        var entry = await FindAsync(AuditEventType.OrganizationUpdated, orgId);
        entry.ShouldNotBeNull();
        entry.ActorName.ShouldBe("Bob");
        entry.Metadata["name"].ShouldBe("Renamed");
    }

    [Fact]
    public async Task When_UserSignedUpConsumed_Then_WritesEntryAttributedToTheUser()
    {
        // Given
        var orgId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var evt = new UserSignedUpEvent(userId, orgId, "Carol", "Google", "web",
            apiFactory.FakeClock.GetCurrentInstant());

        // When
        await RunAsync(p => new OnUserSignedUpAudit(p), evt);

        // Then
        var entry = await FindAsync(AuditEventType.UserSignedUp, orgId);
        entry.ShouldNotBeNull();
        entry.UserId.ShouldBe(userId);
        entry.ActorName.ShouldBe("Carol");
        entry.Metadata["provider"].ShouldBe("Google");
    }

    [Fact]
    public async Task When_AccountRecoveryCompletedConsumed_Then_WritesEntryWithNoSecrets()
    {
        // Given
        var orgId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var evt = new AccountRecoveryCompletedEvent(userId, orgId, "Dave",
            apiFactory.FakeClock.GetCurrentInstant());

        // When
        await RunAsync(p => new OnAccountRecoveryCompletedAudit(p), evt);

        // Then
        var entry = await FindAsync(AuditEventType.AccountRecoveryCompleted, orgId);
        entry.ShouldNotBeNull();
        entry.UserId.ShouldBe(userId);
        entry.ActorName.ShouldBe("Dave");
        entry.Metadata.ShouldBeEmpty();
    }

    [Fact]
    public async Task When_AccountSetupCompletedConsumed_Then_WritesEntry()
    {
        // Given
        var orgId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var evt = new AccountSetupCompletedEvent(userId, orgId, "Erin",
            apiFactory.FakeClock.GetCurrentInstant());

        // When
        await RunAsync(p => new OnAccountSetupCompletedAudit(p), evt);

        // Then
        var entry = await FindAsync(AuditEventType.AccountSetupCompleted, orgId);
        entry.ShouldNotBeNull();
        entry.UserId.ShouldBe(userId);
        entry.ActorName.ShouldBe("Erin");
    }

    [Fact]
    public async Task When_ApiKeyCreatedConsumed_Then_WritesMetadataWithoutFullKey()
    {
        // Given
        var orgId = Guid.NewGuid();
        var keyId = Guid.NewGuid();
        var createdBy = Guid.NewGuid();
        var evt = new ApiKeyCreatedEvent(keyId, orgId, "CI Deploy", "aB3x", createdBy, "Frank",
            apiFactory.FakeClock.GetCurrentInstant());

        // When
        await RunAsync(p => new OnApiKeyCreatedAudit(p), evt);

        // Then
        var entry = await FindAsync(AuditEventType.ApiKeyCreated, orgId);
        entry.ShouldNotBeNull();
        entry.UserId.ShouldBe(createdBy);
        entry.ActorName.ShouldBe("Frank");
        entry.Metadata["keyId"].ShouldBe(keyId.ToString());
        entry.Metadata["keyName"].ShouldBe("CI Deploy");
        entry.Metadata["keySuffix"].ShouldBe("aB3x");
        string.Join("|", entry.Metadata.Values).ShouldNotContain("pl_");
    }

    [Fact]
    public async Task When_ApiKeyRevokedConsumed_Then_WritesEntry()
    {
        // Given
        var orgId = Guid.NewGuid();
        var evt = new ApiKeyRevokedEvent(Guid.NewGuid(), orgId, "Old Key", "zZ9q", Guid.NewGuid(), "Grace",
            apiFactory.FakeClock.GetCurrentInstant());

        // When
        await RunAsync(p => new OnApiKeyRevokedAudit(p), evt);

        // Then
        var entry = await FindAsync(AuditEventType.ApiKeyRevoked, orgId);
        entry.ShouldNotBeNull();
        entry.ActorName.ShouldBe("Grace");
        entry.Metadata["keySuffix"].ShouldBe("zZ9q");
    }

    [Fact]
    public async Task When_ApiKeyDeletedConsumed_Then_WritesEntry()
    {
        // Given
        var orgId = Guid.NewGuid();
        var evt = new ApiKeyDeletedEvent(Guid.NewGuid(), orgId, "Dead Key", "wW1e", Guid.NewGuid(), "Heidi",
            apiFactory.FakeClock.GetCurrentInstant());

        // When
        await RunAsync(p => new OnApiKeyDeletedAudit(p), evt);

        // Then
        var entry = await FindAsync(AuditEventType.ApiKeyDeleted, orgId);
        entry.ShouldNotBeNull();
        entry.ActorName.ShouldBe("Heidi");
        entry.Metadata["keyName"].ShouldBe("Dead Key");
    }

    [Fact]
    public async Task When_EntryCreatedConsumed_Then_HasUserIdAndActorName()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var evt = new EntryUpsertedEvent(organization.Id, vault.Id, Guid.NewGuid(), user.Id,
            EntityChange.Created, 1, apiFactory.FakeClock.GetCurrentInstant());

        // When
        await RunAsync(p => new OnEntryUpsertedAudit(p), evt);

        // Then
        var entry = await FindAsync(AuditEventType.EntryCreated, organization.Id);
        entry.ShouldNotBeNull();
        entry.ActorType.ShouldBe(AuditActorType.User);
        entry.UserId.ShouldBe(user.Id);
        entry.ActorName.ShouldBeNull();
        entry.Result.ShouldBe(AuditResult.Succeeded);
        entry.Metadata["revision"].ShouldBe("1");
    }

    [Fact]
    public async Task When_AgentDeactivatedConsumed_Then_HasUserIdAndActorName()
    {
        // Given
        var orgId = Guid.NewGuid();
        var deactivatedBy = Guid.NewGuid();
        var evt = new AgentDeactivatedEvent(Guid.NewGuid(), orgId, deactivatedBy, "Bob Admin", "Claude Agent",
            1, apiFactory.FakeClock.GetCurrentInstant());

        // When
        await RunAsync(p => new OnAgentDeactivatedAudit(p), evt);

        // Then
        var entry = await FindAsync(AuditEventType.AgentBlocked, orgId);
        entry.ShouldNotBeNull();
        entry.UserId.ShouldBe(deactivatedBy);
        entry.ActorName.ShouldBe("Bob Admin");
        entry.AgentName.ShouldBe("Claude Agent");
    }

    [Fact]
    public async Task When_VaultCreatedConsumed_Then_DoesNotRetainEncryptedDisplayMetadata()
    {
        // Given
        var orgId = Guid.NewGuid();
        var createdBy = Guid.NewGuid();
        var evt = new VaultUpsertedEvent(Guid.NewGuid(), orgId, createdBy, "Olivia Owner",
            false, EntityChange.Created, apiFactory.FakeClock.GetCurrentInstant());

        // When
        await RunAsync(p => new OnVaultUpsertedAudit(p), evt);

        // Then
        var entry = await FindAsync(AuditEventType.VaultCreated, orgId);
        entry.ShouldNotBeNull();
        entry.UserId.ShouldBe(createdBy);
        entry.ActorName.ShouldBe("Olivia Owner");
        entry.Metadata.ShouldBeEmpty();
    }
}
