using MassTransit;
using Microsoft.Extensions.Options;
using NodaTime;
using NSubstitute;
using Palladin.Module.Identity.Infrastructure.Sharing;
using Palladin.Module.Vault.Contracts.Commands;

namespace Palladin.Tests.Unit.Modules.Identity;

public sealed class EntrySharingRevocationTests
{
    [Theory]
    [InlineData("organization")]
    [InlineData("member")]
    [InlineData("version")]
    public async Task When_TheAcknowledgementDoesNotCoverTheRequestedAuthority_Then_TheChangeFailsClosed(string mismatch)
    {
        // Given
        var organization = Guid.NewGuid();
        var member = Guid.NewGuid();
        var response = Substitute.For<Response<MemberEntrySharingRevoked>>();
        response.Message.Returns(new MemberEntrySharingRevoked(
            mismatch == "organization" ? Guid.NewGuid() : organization,
            mismatch == "member" ? Guid.NewGuid() : member,
            mismatch == "version" ? 3u : 4u));
        var client = Substitute.For<IRequestClient<RevokeMemberEntrySharingCommand>>();
        client.GetResponse<MemberEntrySharingRevoked>(Arg.Any<RevokeMemberEntrySharingCommand>(),
            Arg.Any<CancellationToken>(), Arg.Any<RequestTimeout>()).Returns(response);
        var revocation = new EntrySharingRevocation(client, Options.Create(new EntrySharingRevocationOptions()));

        // When
        var action = () => revocation.RevokeMemberAsync(organization, member, 4,
            Instant.FromUtc(2026, 9, 20, 12, 0), TestContext.Current.CancellationToken);

        // Then
        await Should.ThrowAsync<EntrySharingRevocationUnavailableException>(action);
    }

    [Theory]
    [InlineData(4u)]
    [InlineData(5u)]
    public async Task When_TheCommittedRevocationCoversTheRequestedVersion_Then_TheAcknowledgementIsAccepted(uint version)
    {
        // Given
        var organization = Guid.NewGuid();
        var member = Guid.NewGuid();
        var response = Substitute.For<Response<MemberEntrySharingRevoked>>();
        response.Message.Returns(new MemberEntrySharingRevoked(organization, member, version));
        var client = Substitute.For<IRequestClient<RevokeMemberEntrySharingCommand>>();
        client.GetResponse<MemberEntrySharingRevoked>(Arg.Any<RevokeMemberEntrySharingCommand>(),
            Arg.Any<CancellationToken>(), Arg.Any<RequestTimeout>()).Returns(response);
        var revocation = new EntrySharingRevocation(client,
            Options.Create(new EntrySharingRevocationOptions { RequestTimeoutSeconds = 2 }));
        var now = Instant.FromUtc(2026, 9, 20, 12, 0);

        // When
        await revocation.RevokeMemberAsync(organization, member, 4, now, TestContext.Current.CancellationToken);

        // Then
        await client.Received(1).GetResponse<MemberEntrySharingRevoked>(
            new RevokeMemberEntrySharingCommand(organization, member, 4, now),
            TestContext.Current.CancellationToken, Arg.Is<RequestTimeout>(x => x.Value == TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task When_NoAcknowledgementArrives_Then_TheCallerGetsARetryableConflict()
    {
        // Given
        var client = Substitute.For<IRequestClient<RevokeMemberEntrySharingCommand>>();
        client.GetResponse<MemberEntrySharingRevoked>(Arg.Any<RevokeMemberEntrySharingCommand>(),
                Arg.Any<CancellationToken>(), Arg.Any<RequestTimeout>())
            .Returns(Task.FromException<Response<MemberEntrySharingRevoked>>(new RequestTimeoutException("synthetic timeout")));
        var revocation = new EntrySharingRevocation(client, Options.Create(new EntrySharingRevocationOptions()));

        // When
        var action = () => revocation.RevokeMemberAsync(Guid.NewGuid(), Guid.NewGuid(), 1,
            Instant.FromUtc(2026, 9, 20, 12, 0), TestContext.Current.CancellationToken);

        // Then
        await Should.ThrowAsync<EntrySharingRevocationUnavailableException>(action);
    }
}
