using Palladin.Module.Vault.Infrastructure.Sharing;
using Shouldly;

namespace Palladin.Tests.Unit.Modules.Vault;

public sealed class EntryShareActivityIdentityTests
{
    [Fact]
    public void When_AnOccurrenceIsReconstructed_Then_TheStableIdentityBindsShareAndSequence()
    {
        // Given
        var firstShare = Guid.Parse("d7103df0-9816-4fdb-9ca6-508c5422a7d6");
        var secondShare = Guid.Parse("ef30df73-fc41-43f6-a34a-ec4fe7101373");

        // When
        var occurrence = EntryShareActivityIdentity.For(firstShare, 1);

        // Then
        occurrence.ShouldBe(Guid.Parse("d5e93646-6444-8f32-b48b-4db13383683d"));
        EntryShareActivityIdentity.For(firstShare, 1).ShouldBe(occurrence);
        EntryShareActivityIdentity.For(firstShare, 2).ShouldNotBe(occurrence);
        EntryShareActivityIdentity.For(secondShare, 1).ShouldNotBe(occurrence);
        Should.Throw<ArgumentOutOfRangeException>(() => EntryShareActivityIdentity.For(firstShare, 0));
    }
}
