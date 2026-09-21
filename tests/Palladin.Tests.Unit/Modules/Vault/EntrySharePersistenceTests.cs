using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using NodaTime;
using Palladin.Core.Types;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Persistence;

namespace Palladin.Tests.Unit.Modules.Vault;

public sealed class EntrySharePersistenceTests
{
    [Fact]
    public void When_JournalJobsSelectTheirBatches_Then_PendingAndPublishedRowsHaveSeparateOrderedIndexes()
    {
        // Given
        using var context = CreateContext();

        // When
        var indexes = context.Model.FindEntityType(typeof(EntryShareActivity))!.GetIndexes().ToArray();

        // Then
        indexes.Single(x => x.GetFilter() == "\"PublishedAt\" IS NULL").Properties.Select(x => x.Name)
            .ShouldBe(["OccurredAt", "ShareId", "Sequence"]);
        indexes.Single(x => x.GetFilter() == "\"PublishedAt\" IS NOT NULL").Properties.Select(x => x.Name)
            .ShouldBe(["PublishedAt", "ShareId", "Sequence"]);
    }

    [Fact]
    public void When_SourceAuthorityIsChecked_Then_RevocationAndEqualTimestampPurgeAreConcurrencyFenced()
    {
        // Given
        using var context = CreateContext();

        // When
        var sender = context.Model.FindEntityType(typeof(EntryShareSenderAuthority))!;
        var organization = context.Model.FindEntityType(typeof(VaultOrganizationLifecycle))!;
        var source = context.Model.FindEntityType(typeof(VaultEntry))!;
        var challenge = context.Model.FindEntityType(typeof(EntryShareCreationChallenge))!;

        // Then
        sender.FindProperty(nameof(EntryShareSenderAuthority.MutationVersion))!.IsConcurrencyToken.ShouldBeTrue();
        organization.FindProperty(nameof(VaultOrganizationLifecycle.MutationVersion))!.IsConcurrencyToken.ShouldBeTrue();
        source.FindProperty(nameof(VaultEntry.CurrentRevision))!.IsConcurrencyToken.ShouldBeTrue();
        source.FindProperty(nameof(VaultEntry.IsPurging))!.IsConcurrencyToken.ShouldBeTrue();
        challenge.FindProperty(nameof(EntryShareCreationChallenge.MutationVersion))!.IsConcurrencyToken.ShouldBeTrue();
        challenge.FindPrimaryKey()!.Properties.Select(x => x.Name).ShouldBe([
            nameof(EntryShareCreationChallenge.OrganizationId), nameof(EntryShareCreationChallenge.VaultId),
            nameof(EntryShareCreationChallenge.EntryId), nameof(EntryShareCreationChallenge.RequestedBy)]);
    }

    [Fact]
    public void When_TheModelIsBuilt_Then_ShareAndSessionWritesAreConcurrencyFenced()
    {
        // Given
        using var context = CreateContext();

        // When
        var share = context.Model.FindEntityType(typeof(EntryShare))!;
        var session = context.Model.FindEntityType(typeof(EntryShareSession))!;

        // Then
        share.FindProperty(nameof(EntryShare.MutationVersion))!.IsConcurrencyToken.ShouldBeTrue();
        session.FindProperty(nameof(EntryShareSession.MutationVersion))!.IsConcurrencyToken.ShouldBeTrue();
        session.FindPrimaryKey()!.Properties.Select(x => x.Name)
            .ShouldBe([nameof(EntryShareSession.ShareId), nameof(EntryShareSession.Id)]);
    }

    [Fact]
    public void When_TheSourceIsPurged_Then_OnlyCiphertextAndSessionsCascadeNotPendingAudit()
    {
        // Given
        using var context = CreateContext();

        // When
        var share = context.Model.FindEntityType(typeof(EntryShare))!;
        var session = context.Model.FindEntityType(typeof(EntryShareSession))!;
        var activity = context.Model.FindEntityType(typeof(EntryShareActivity))!;

        // Then
        share.GetForeignKeys().Single().DeleteBehavior.ShouldBe(DeleteBehavior.Cascade);
        share.GetForeignKeys().Single().PrincipalEntityType.ClrType.ShouldBe(typeof(VaultEntry));
        session.GetForeignKeys().Single().DeleteBehavior.ShouldBe(DeleteBehavior.Cascade);
        activity.GetForeignKeys().ShouldBeEmpty();
        activity.FindPrimaryKey()!.Properties.Select(x => x.Name)
            .ShouldBe([nameof(EntryShareActivity.ShareId), nameof(EntryShareActivity.Sequence)]);
    }

    [Fact]
    public void When_SharingActivityIsStagedTwice_Then_EachOccurrenceIsAddedOnlyOnce()
    {
        // Given
        using var context = CreateContext();
        var domain = new VaultDomainWriteContext(context, []);
        var now = Instant.FromUtc(2026, 9, 20, 12, 0);
        var share = EntryShare.Create(Guid.NewGuid(),
            new EntryScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), new EntryRevision(1),
            Guid.NewGuid(), now, now + Duration.FromHours(1), 1,
            EntryShareRecipientMode.AnyoneWithLink, null, EntryShareProtection.None, null,
            new byte[32], new byte[24], new byte[16], true, 1, now);
        domain.Add(share);

        // When
        domain.StageEntryShareActivities(share);
        domain.StageEntryShareActivities(share);

        // Then
        context.ChangeTracker.Entries<EntryShareActivity>().ShouldHaveSingleItem()
            .State.ShouldBe(EntityState.Added);
        share.Activities.ShouldBeEmpty();
        share.FetchEvents().ShouldHaveSingleItem();
    }

    [Fact]
    public void When_ModelColumnsAreInspected_Then_ActivityHasOnlyStructuralData()
    {
        // Given
        using var context = CreateContext();

        // When
        var properties = context.Model.FindEntityType(typeof(EntryShareActivity))!.GetProperties().ToList();

        // Then
        properties.ShouldNotContain(x => x.ClrType == typeof(string) || x.ClrType == typeof(byte[]));
        context.Model.FindEntityType(typeof(EntryShare))!.FindProperty(nameof(EntryShare.Ciphertext))!
            .GetMaxLength().ShouldBe(EntryShare.MaximumCiphertextBytes);
    }

    private static VaultDbWriteContext CreateContext() => new(
        new DbContextOptionsBuilder<VaultDbWriteContext>()
            .UseNpgsql("Host=localhost;Database=unused_model_only", postgres => postgres.UseNodaTime())
            .Options);
}
