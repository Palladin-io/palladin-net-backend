using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Palladin.Module.Agents.Domain;
using Palladin.Module.Agents.Features;
using Palladin.Module.Agents.Infrastructure.Persistence;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Seeders;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Agents;

[Collection<ApiFactoryCollection>]
public sealed class AgentPairingPersistenceTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_PairingApprovalAndApiKeyRevocationShareSnapshot_Then_OneCommitConflicts()
    {
        // Given
        var organizationId = Guid.NewGuid();
        var (apiKey, _) = await apiFactory.Services.SeedApiKeyAsync(organizationId);
        await using var approvalScope = apiFactory.Services.CreateAsyncScope();
        await using var revocationScope = apiFactory.Services.CreateAsyncScope();
        var approvalContext = approvalScope.ServiceProvider.GetRequiredService<AgentsDomainWriteContext>();
        var revocationContext = revocationScope.ServiceProvider.GetRequiredService<AgentsDomainWriteContext>();
        var approvalKey = await approvalContext.ApiKeys.SingleAsync(
            x => x.Id == apiKey.Id,
            TestContext.Current.CancellationToken);
        var revocationKey = await revocationContext.ApiKeys.SingleAsync(
            x => x.Id == apiKey.Id,
            TestContext.Current.CancellationToken);
        approvalKey.TryFenceAgentPairingApproval().ShouldBeTrue();
        revocationKey.Revoke(Guid.NewGuid(), "Operator", apiFactory.FakeClock.GetCurrentInstant());

        // When
        await revocationContext.CommitAsync(TestContext.Current.CancellationToken);
        var staleApproval = () => approvalContext.CommitAsync(TestContext.Current.CancellationToken);

        // Then
        await Should.ThrowAsync<DbUpdateConcurrencyException>(staleApproval);
        await using var verifyScope = apiFactory.Services.CreateAsyncScope();
        var readContext = verifyScope.ServiceProvider.GetRequiredService<AgentsDbReadContext>();
        (await readContext.ApiKeys.SingleAsync(
            x => x.Id == apiKey.Id,
            TestContext.Current.CancellationToken)).Status.ShouldBe(ApiKeyStatus.Revoked);
    }

    [Fact]
    public async Task When_TwoOrganizationsClaimSameSnapshot_Then_OptimisticFenceRejectsStaleClaim()
    {
        // Given
        var pairingId = Guid.NewGuid();
        var now = apiFactory.FakeClock.GetCurrentInstant();
        await using (var seedScope = apiFactory.Services.CreateAsyncScope())
        {
            var seedContext = seedScope.ServiceProvider.GetRequiredService<AgentsDomainWriteContext>();
            seedContext.Add(CreatePairing(pairingId, now));
            await seedContext.CommitAsync(TestContext.Current.CancellationToken);
        }

        await using var firstScope = apiFactory.Services.CreateAsyncScope();
        await using var secondScope = apiFactory.Services.CreateAsyncScope();
        var firstContext = firstScope.ServiceProvider.GetRequiredService<AgentsDomainWriteContext>();
        var secondContext = secondScope.ServiceProvider.GetRequiredService<AgentsDomainWriteContext>();
        var first = await firstContext.AgentPairingRequests.SingleAsync(
            x => x.Id == pairingId,
            TestContext.Current.CancellationToken);
        var second = await secondContext.AgentPairingRequests.SingleAsync(
            x => x.Id == pairingId,
            TestContext.Current.CancellationToken);
        var firstOrganizationId = Guid.NewGuid();
        first.TryClaim(firstOrganizationId, now).ShouldBeTrue();
        second.TryClaim(Guid.NewGuid(), now).ShouldBeTrue();

        // When
        await firstContext.CommitAsync(TestContext.Current.CancellationToken);
        var staleCommit = () => secondContext.CommitAsync(TestContext.Current.CancellationToken);

        // Then
        await Should.ThrowAsync<DbUpdateConcurrencyException>(staleCommit);
        await using var verifyScope = apiFactory.Services.CreateAsyncScope();
        var readContext = verifyScope.ServiceProvider.GetRequiredService<AgentsDbReadContext>();
        (await readContext.AgentPairingRequests.SingleAsync(
            x => x.Id == pairingId,
            TestContext.Current.CancellationToken)).OrganizationId.ShouldBe(firstOrganizationId);
    }

    [Fact]
    public async Task When_CleanupRuns_Then_ExpiredReservationIsReleasedAndTerminalMaterialIsBounded()
    {
        // Given
        var originalNow = apiFactory.FakeClock.GetCurrentInstant();
        var pairingId = Guid.NewGuid();
        try
        {
            await using (var seedScope = apiFactory.Services.CreateAsyncScope())
            {
                var seedContext = seedScope.ServiceProvider.GetRequiredService<AgentsDomainWriteContext>();
                var pairing = CreatePairing(pairingId, originalNow);
                var organizationId = Guid.NewGuid();
                pairing.TryClaim(organizationId, originalNow).ShouldBeTrue();
                pairing.TryReserveDisplayName(organizationId, "Amber Fox", originalNow)
                    .ShouldBeTrue();
                seedContext.Add(pairing);
                await seedContext.CommitAsync(TestContext.Current.CancellationToken);
            }
            apiFactory.FakeClock.AdvanceMinutes(5);

            // When
            await ExecuteCleanupAsync();

            // Then
            await using (var retainedScope = apiFactory.Services.CreateAsyncScope())
            {
                var readContext = retainedScope.ServiceProvider.GetRequiredService<AgentsDbReadContext>();
                var retained = await readContext.AgentPairingRequests.SingleAsync(
                    x => x.Id == pairingId,
                    TestContext.Current.CancellationToken);
                retained.Status.ShouldBe(AgentPairingStatus.Expired);
                retained.ReservedDisplayName.ShouldBeNull();
                retained.ReservedDisplayNameKey.ShouldBeNull();
            }

            apiFactory.FakeClock.AdvanceMinutes(16);
            await ExecuteCleanupAsync();
            await using var deletedScope = apiFactory.Services.CreateAsyncScope();
            var deletedReadContext = deletedScope.ServiceProvider.GetRequiredService<AgentsDbReadContext>();
            (await deletedReadContext.AgentPairingRequests.CountAsync(
                x => x.Id == pairingId,
                TestContext.Current.CancellationToken)).ShouldBe(0);
        }
        finally
        {
            apiFactory.FakeClock.Reset(originalNow);
        }
    }

    [Fact]
    public async Task When_ApprovedPairingPassesOriginalDeadline_Then_CleanupRetainsEnvelopeForGracePeriod()
    {
        // Given
        var originalNow = apiFactory.FakeClock.GetCurrentInstant();
        var pairingId = Guid.NewGuid();
        try
        {
            await using (var seedScope = apiFactory.Services.CreateAsyncScope())
            {
                var seedContext = seedScope.ServiceProvider.GetRequiredService<AgentsDomainWriteContext>();
                var pairing = CreatePairing(pairingId, originalNow);
                var organizationId = Guid.NewGuid();
                pairing.TryClaim(organizationId, originalNow).ShouldBeTrue();
                pairing.TryReserveDisplayName(organizationId, "Quiet Otter", originalNow).ShouldBeTrue();
                pairing.TryApprove(
                    organizationId,
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    "Quiet Otter",
                    "custom/runtime",
                    new ApiKeyCredentialEnvelope("test-suite", "public", "nonce", "ciphertext"),
                    originalNow).ShouldBeTrue();
                seedContext.Add(pairing);
                await seedContext.CommitAsync(TestContext.Current.CancellationToken);
            }
            apiFactory.FakeClock.AdvanceMinutes(5);

            // When
            await ExecuteCleanupAsync();

            // Then
            await using (var retainedScope = apiFactory.Services.CreateAsyncScope())
            {
                var readContext = retainedScope.ServiceProvider.GetRequiredService<AgentsDbReadContext>();
                var retained = await readContext.AgentPairingRequests.SingleAsync(
                    x => x.Id == pairingId,
                    TestContext.Current.CancellationToken);
                retained.Status.ShouldBe(AgentPairingStatus.Approved);
                retained.CredentialCiphertext.ShouldBe("ciphertext");
            }

            apiFactory.FakeClock.AdvanceMinutes(11);
            await ExecuteCleanupAsync();
            await using var deletedScope = apiFactory.Services.CreateAsyncScope();
            var deletedReadContext = deletedScope.ServiceProvider.GetRequiredService<AgentsDbReadContext>();
            (await deletedReadContext.AgentPairingRequests.CountAsync(
                x => x.Id == pairingId,
                TestContext.Current.CancellationToken)).ShouldBe(0);
        }
        finally
        {
            apiFactory.FakeClock.Reset(originalNow);
        }
    }

    private async Task ExecuteCleanupAsync()
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var job = scope.ServiceProvider.GetRequiredService<CleanupAgentPairingsJob>();
        await job.ExecuteAsync(TestContext.Current.CancellationToken);
    }

    private static AgentPairingRequest CreatePairing(Guid pairingId, NodaTime.Instant now) =>
        AgentPairingRequest.Create(
            pairingId,
            Convert.ToBase64String(new byte[32]),
            Convert.ToBase64String(Enumerable.Repeat((byte)1, 32).ToArray()),
            null,
            null,
            now,
            now + NodaTime.Duration.FromMinutes(4));
}
