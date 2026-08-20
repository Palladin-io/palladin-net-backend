using NodaTime;
using Palladin.Core.Types;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Contracts.Events;
using Palladin.Tests.Integrations.Shared.Fakers;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Vault;

public sealed class DefaultVaultDomainTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 7, 1, 12, 0);

    [Fact]
    public void CreateDefault_SetsServerOwnedFlagAndEmitsOpaqueLifecycleEvent()
    {
        var vault = VaultFaker.Create(isDefault: true);
        vault.IsDefault.ShouldBeTrue();
        var upserted = vault.FetchEvents().OfType<VaultUpsertedEvent>().ShouldHaveSingleItem();
        upserted.Change.ShouldBe(EntityChange.Created);
        upserted.IsDefault.ShouldBeTrue();
    }

    [Fact]
    public void AllocateEntrySequence_EmitsLatestValueFreeSyncInvalidationForEveryMember()
    {
        var creatorId = Guid.NewGuid();
        var secondMemberId = Guid.NewGuid();
        var vault = VaultFaker.Create(createdBy: creatorId);
        vault.AddMember(secondMemberId, VaultFaker.CreateMemberKey(vault.Scope, secondMemberId), Now);
        vault.FetchEvents();

        var allocated = vault.AllocateSequences(false, creatorId, Now);

        var invalidation = vault.FetchEvents().OfType<VaultSyncInvalidatedEvent>().ShouldHaveSingleItem();
        invalidation.OrganizationId.ShouldBe(vault.OrganizationId);
        invalidation.VaultId.ShouldBe(vault.Id);
        invalidation.MemberSequence.ShouldBe(allocated.MemberSequence.Value);
        invalidation.MutationVersion.ShouldBe(vault.MutationVersion);
        invalidation.MemberUserIds.ShouldBe([creatorId, secondMemberId], ignoreOrder: true);
        invalidation.OccurredAt.ShouldBe(Now);
    }

    [Fact]
    public void Delete_DefaultVault_Throws()
    {
        var vault = VaultFaker.Create(isDefault: true);
        Should.Throw<DefaultVaultUndeletableException>(() => vault.BeginDeletion(Guid.NewGuid(), "actor", Now));
    }

    [Fact]
    public void AddMember_DefaultVault_Throws()
    {
        var vault = VaultFaker.Create(isDefault: true);
        var memberId = Guid.NewGuid();
        var wrappedKey = VaultFaker.CreateMemberKey(vault.Scope, memberId);
        Should.Throw<DefaultVaultNotShareableException>(() => vault.AddMember(memberId, wrappedKey, Now));
    }

    [Fact]
    public void AddMember_NormalVault_PersistsStructuralMemberAndTenantFirstEnvelope()
    {
        var vault = VaultFaker.Create();
        var memberId = Guid.NewGuid();
        var wrappedKey = VaultFaker.CreateMemberKey(vault.Scope, memberId);

        var added = vault.AddMember(memberId, wrappedKey, Now);

        added.Member.UserId.ShouldBe(memberId);
        added.KeyEnvelope.OrganizationId.ShouldBe(vault.OrganizationId);
        added.KeyEnvelope.VaultId.ShouldBe(vault.Id);
        added.KeyEnvelope.MemberId.ShouldBe(memberId);
        vault.VaultMembers.ShouldContain(added.Member);
    }

    [Fact]
    public void ReplaceMetadata_RejectsRevisionRollback()
    {
        var vault = VaultFaker.Create();
        vault.ReplaceMetadata(Guid.NewGuid(), "actor", VaultFaker.CreateMetadata(vault.Scope, 2), Now);
        var stale = VaultFaker.CreateMetadata(vault.Scope, 1);
        Should.Throw<Exception>(() => vault.ReplaceMetadata(Guid.NewGuid(), "actor", stale, Now));
    }
}
