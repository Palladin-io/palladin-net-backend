using System.Net;
using System.Net.Http.Json;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Palladin.Core.Events;
using Palladin.Core.Security;
using Palladin.Module.Agents.Contracts.Events;
using Palladin.Module.Identity.Contracts.Events;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Contracts.Commands;
using Palladin.Module.Vault.Contracts.Events;
using Palladin.Module.Vault.Features;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Shared;
using Palladin.Module.Vault.Triggers;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Fakers;
using Palladin.Tests.Integrations.Shared.Mocks;
using Palladin.Tests.Integrations.Shared.Seeders;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Vault;

[Collection<ApiFactoryCollection>]
public sealed class VaultPrincipalDeprovisioningTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_CompletedRequestIsRedelivered_Then_CompletionEventIsRepublished()
    {
        // Given
        var now = apiFactory.FakeClock.GetCurrentInstant();
        var operation = VaultPrincipalDeprovisioning.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            VaultPrincipalType.Agent,
            Guid.NewGuid(),
            Guid.NewGuid(),
            now);
        operation.Complete(now);
        operation.FetchEvents();
        var publisher = Substitute.For<IEventPublisher>();

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<VaultDbWriteContext>();
        writeContext.VaultPrincipalDeprovisionings.Add(operation);
        await writeContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var coordinator = new VaultPrincipalDeprovisioningCoordinator(
            new VaultDomainWriteContext(writeContext, [publisher]),
            apiFactory.GuidProvider);

        // When
        await coordinator.StartAsync(
            operation.Id,
            operation.OrganizationId,
            VaultPrincipalType.Agent,
            operation.PrincipalId,
            operation.RequestedBy,
            operation.RequestedAt,
            TestContext.Current.CancellationToken);

        // Then
        await publisher.Received(1).PublishAsync(
            Arg.Is<AgentDeactivationCompletedEvent>(message =>
                message.RequestId == operation.Id
                && message.UpdatedAt == now),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public void When_CompletedDeliveryIsResumed_Then_CompletionEventIsRecreated()
    {
        var now = apiFactory.FakeClock.GetCurrentInstant();
        var operation = VaultPrincipalDeprovisioning.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            VaultPrincipalType.OrganizationMember,
            Guid.NewGuid(),
            Guid.NewGuid(),
            now);
        operation.Complete(now);
        operation.FetchEvents().ShouldHaveSingleItem();

        operation.ResumeCompletion();

        var completion = operation.FetchEvents().ShouldHaveSingleItem()
            .ShouldBeOfType<OrganizationMemberRemovalCompletedEvent>();
        completion.RequestId.ShouldBe(operation.Id);
        completion.CompletedAt.ShouldBe(now);
    }

    [Fact]
    public async Task When_RemovingMemberFromSharedVault_Then_DurableRotationExcludesThatMember()
    {
        // Given
        var (removedMember, organization, _) = await apiFactory.Services.SeedUserAsync();
        var remainingMember = await apiFactory.Services.SeedAdditionalOrganizationMemberAsync(organization.Id);
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, removedMember.Id);
        await AddVaultMemberAsync(organization.Id, vault.Id, remainingMember.Id);
        var requestId = Guid.NewGuid();
        var requested = new OrganizationMemberRemovalRequestedEvent(
            requestId,
            organization.Id,
            removedMember.Id,
            remainingMember.Id,
            apiFactory.FakeClock.GetCurrentInstant());

        // When
        await ConsumeAsync(requested);
        await ConsumeAsync(requested);

        // Then
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var operation = await readContext.VaultPrincipalDeprovisionings.SingleAsync(x => x.Id == requestId);
        operation.Status.ShouldBe(VaultPrincipalDeprovisioningStatus.WaitingForRotation);
        operation.CurrentVaultId.ShouldBe(vault.Id);
        var rotation = await readContext.VaultKeyRotations.SingleAsync(x => x.DeprovisioningId == requestId);
        rotation.ExcludedMemberId.ShouldBe(removedMember.Id);
        rotation.Cause.ShouldBe(VaultKeyRotationCause.MemberRemovalRequested);
        rotation.Scope.ShouldBe(
            VaultKeyRotationScope.VaultKey
            | VaultKeyRotationScope.Vdk
            | VaultKeyRotationScope.AgentMessage
            | VaultKeyRotationScope.ManifestSigning);
    }

    [Fact]
    public async Task When_RemovingLastVaultMember_Then_OperationBlocksWithoutCreatingRotation()
    {
        // Given
        var (member, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, member.Id);
        var requestId = Guid.NewGuid();

        // When
        await ConsumeAsync(new OrganizationMemberRemovalRequestedEvent(
            requestId,
            organization.Id,
            member.Id,
            Guid.NewGuid(),
            apiFactory.FakeClock.GetCurrentInstant()));

        // Then
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var operation = await readContext.VaultPrincipalDeprovisionings.SingleAsync(x => x.Id == requestId);
        operation.Status.ShouldBe(VaultPrincipalDeprovisioningStatus.BlockedLastMember);
        operation.CurrentVaultId.ShouldBe(vault.Id);
        (await readContext.VaultKeyRotations.AnyAsync(x => x.DeprovisioningId == requestId)).ShouldBeFalse();
    }

    [Fact]
    public async Task When_RemovedMemberClaimsOwnRotation_Then_RequestFailsClosed()
    {
        // Given
        var (removedMember, organization, _) = await apiFactory.Services.SeedUserAsync();
        var remainingMember = await apiFactory.Services.SeedAdditionalOrganizationMemberAsync(organization.Id);
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, removedMember.Id);
        await AddVaultMemberAsync(organization.Id, vault.Id, remainingMember.Id);
        var requestId = Guid.NewGuid();
        await ConsumeAsync(new OrganizationMemberRemovalRequestedEvent(
            requestId,
            organization.Id,
            removedMember.Id,
            remainingMember.Id,
            apiFactory.FakeClock.GetCurrentInstant()));
        Guid rotationId;
        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            rotationId = await scope.ServiceProvider.GetRequiredService<VaultDbReadContext>()
                .VaultKeyRotations.Where(x => x.DeprovisioningId == requestId)
                .Select(x => x.Id)
                .SingleAsync();
        }
        var client = apiFactory.CreateAuthenticatedClient(removedMember, Permission.VaultManage);

        // When
        var response = await client.POSTAsync<
            ClaimVaultKeyRotationEndpoint,
            ClaimVaultKeyRotationRequest>(new ClaimVaultKeyRotationRequest
            {
                VaultId = vault.Id,
                RotationId = rotationId,
            });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task When_MemberRemovalRotationCommits_Then_AccessIsRemovedAndSagaCompletes()
    {
        // Given
        var (removedMember, organization, _) = await apiFactory.Services.SeedUserAsync();
        var remainingMember = await apiFactory.Services.SeedAdditionalOrganizationMemberAsync(organization.Id);
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, removedMember.Id);
        await AddVaultMemberAsync(organization.Id, vault.Id, remainingMember.Id);
        await SeedMemberDirectoryAsync(remainingMember.Id);
        var requestId = Guid.NewGuid();
        await ConsumeAsync(new OrganizationMemberRemovalRequestedEvent(
            requestId,
            organization.Id,
            removedMember.Id,
            remainingMember.Id,
            apiFactory.FakeClock.GetCurrentInstant()));
        Guid rotationId;
        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            rotationId = await scope.ServiceProvider.GetRequiredService<VaultDbReadContext>()
                .VaultKeyRotations.Where(x => x.DeprovisioningId == requestId)
                .Select(x => x.Id)
                .SingleAsync();
        }
        var client = apiFactory.CreateAuthenticatedClient(remainingMember, Permission.VaultManage);
        var (_, claim) = await client.POSTAsync<
            ClaimVaultKeyRotationEndpoint,
            ClaimVaultKeyRotationRequest,
            ClaimVaultKeyRotationResponse>(new ClaimVaultKeyRotationRequest
            {
                VaultId = vault.Id,
                RotationId = rotationId,
            });
        var prepareResponse = await client.PutAsJsonAsync(
            $"api/vaults/{vault.Id}/key-rotations/{rotationId}/batch",
            new PrepareVaultKeyRotationBatchRequest
            {
                VaultId = vault.Id,
                RotationId = rotationId,
                FencingToken = claim!.FencingToken,
                MemberVaultMetadata = VaultEnvelopeContractMapper.ToContract(
                    VaultFaker.CreateMetadata(vault.Scope, 2, new MemberKeyGeneration(2), new VaultKeyVersion(2))),
                MemberVaultKeys = [VaultEnvelopeContractMapper.ToContract(
                    VaultFaker.CreateMemberKey(
                        vault.Scope,
                        remainingMember.Id,
                        new MemberKeyGeneration(2),
                        new VaultKeyVersion(2)))],
                DiscoveryKey = VaultContractFaker.CreateDiscoveryKey(
                    vault.OrganizationId, vault.Id, 2, 2, 2, 2, 2, 2),
                VaultPrivateKeys = VaultContractFaker.CreatePrivateKeys(
                    vault.OrganizationId, vault.Id, 2, 2, 2, 2, 2, 2),
                VaultAgentMessagePublicKey = VaultContractFaker.CreateAgentMessagePublicKey(),
                VaultManifestSigningPublicKey = VaultContractFaker.CreateManifestSigningPublicKey(),
            });
        prepareResponse.StatusCode.ShouldBe(HttpStatusCode.OK, await prepareResponse.Content.ReadAsStringAsync());

        // When
        var commitResponse = await client.PostAsJsonAsync(
            $"api/vaults/{vault.Id}/key-rotations/{rotationId}/commit",
            new CommitVaultKeyRotationRequest
            {
                VaultId = vault.Id,
                RotationId = rotationId,
                FencingToken = claim.FencingToken,
            });

        // Then
        commitResponse.StatusCode.ShouldBe(HttpStatusCode.OK, await commitResponse.Content.ReadAsStringAsync());
        await using var verificationScope = apiFactory.Services.CreateAsyncScope();
        var readContext = verificationScope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await readContext.VaultMembers.AnyAsync(x =>
            x.OrganizationId == organization.Id && x.VaultId == vault.Id && x.UserId == removedMember.Id))
            .ShouldBeFalse();
        (await readContext.VaultMembers.AnyAsync(x =>
            x.OrganizationId == organization.Id && x.VaultId == vault.Id && x.UserId == remainingMember.Id))
            .ShouldBeTrue();
        var operation = await readContext.VaultPrincipalDeprovisionings.SingleAsync(x => x.Id == requestId);
        operation.Status.ShouldBe(VaultPrincipalDeprovisioningStatus.Completed);
        operation.CompletedVaultCount.ShouldBe(1u);
    }

    [Fact]
    public async Task When_AgentDeactivationRotationCommits_Then_VdkEnvelopeIsRemoved()
    {
        // Given
        var (member, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, member.Id);
        await SeedMemberDirectoryAsync(member.Id);
        var agentId = Guid.NewGuid();
        var fixture = AgentDiscoveryProvisioningContractFaker.Create(organization.Id, vault.Id, agentId);
        await apiFactory.Services.SeedVaultAgentAsync(
            organization.Id,
            id: agentId,
            publicKey: fixture.X25519PublicKey,
            signingPublicKey: fixture.RequestSigning.PublicKeyBase64);
        await apiFactory.Services.SeedAgentDiscoveryProvisioningAsync(fixture.Request, member.Id);
        var requestId = Guid.NewGuid();
        await ConsumeAsync(new AgentDeactivationRequestedEvent(
            requestId,
            agentId,
            organization.Id,
            member.Id,
            apiFactory.FakeClock.GetCurrentInstant()));
        Guid rotationId;
        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            rotationId = await scope.ServiceProvider.GetRequiredService<VaultDbReadContext>()
                .VaultKeyRotations.Where(x => x.DeprovisioningId == requestId)
                .Select(x => x.Id)
                .SingleAsync();
        }
        var client = apiFactory.CreateAuthenticatedClient(member, Permission.VaultManage);
        var (_, claim) = await client.POSTAsync<
            ClaimVaultKeyRotationEndpoint,
            ClaimVaultKeyRotationRequest,
            ClaimVaultKeyRotationResponse>(new ClaimVaultKeyRotationRequest
            {
                VaultId = vault.Id,
                RotationId = rotationId,
            });
        var discoveryKey = VaultContractFaker.CreateDiscoveryKey(
            organization.Id, vault.Id, 2, 1, 1, 2, 1, 1);
        var prepareResponse = await client.PutAsJsonAsync(
            $"api/vaults/{vault.Id}/key-rotations/{rotationId}/batch",
            new PrepareVaultKeyRotationBatchRequest
            {
                VaultId = vault.Id,
                RotationId = rotationId,
                FencingToken = claim!.FencingToken,
                DiscoveryKey = discoveryKey,
            });
        prepareResponse.StatusCode.ShouldBe(HttpStatusCode.OK, await prepareResponse.Content.ReadAsStringAsync());
        // When
        var commitResponse = await client.PostAsJsonAsync(
            $"api/vaults/{vault.Id}/key-rotations/{rotationId}/commit",
            new CommitVaultKeyRotationRequest
            {
                VaultId = vault.Id,
                RotationId = rotationId,
                FencingToken = claim!.FencingToken,
            });

        // Then
        commitResponse.StatusCode.ShouldBe(HttpStatusCode.OK, await commitResponse.Content.ReadAsStringAsync());
        await using var verificationScope = apiFactory.Services.CreateAsyncScope();
        var readContext = verificationScope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await readContext.AgentVaultDiscoveryEnvelopes.AnyAsync(x =>
            x.OrganizationId == organization.Id && x.VaultId == vault.Id && x.AgentId == agentId))
            .ShouldBeFalse();
        (await readContext.VaultPrincipalDeprovisionings.SingleAsync(x => x.Id == requestId))
            .Status.ShouldBe(VaultPrincipalDeprovisioningStatus.Completed);
    }

    [Fact]
    public async Task When_MultiVaultRemovalIsInterrupted_Then_CommittedVaultStaysRemovedAndNextVaultResumes()
    {
        // Given
        var (removedMember, organization, _) = await apiFactory.Services.SeedUserAsync();
        var remainingMember = await apiFactory.Services.SeedAdditionalOrganizationMemberAsync(organization.Id);
        var firstVault = await apiFactory.Services.SeedVaultAsync(organization.Id, removedMember.Id);
        var secondVault = await apiFactory.Services.SeedVaultAsync(organization.Id, removedMember.Id);
        await AddVaultMemberAsync(organization.Id, firstVault.Id, remainingMember.Id);
        await AddVaultMemberAsync(organization.Id, secondVault.Id, remainingMember.Id);
        await SeedMemberDirectoryAsync(remainingMember.Id);
        var requestId = Guid.NewGuid();
        await ConsumeAsync(new OrganizationMemberRemovalRequestedEvent(
            requestId,
            organization.Id,
            removedMember.Id,
            remainingMember.Id,
            apiFactory.FakeClock.GetCurrentInstant()));
        var vaults = new[] { firstVault, secondVault }.ToDictionary(x => x.Id);
        Guid currentVaultId;
        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            currentVaultId = (await scope.ServiceProvider.GetRequiredService<VaultDbReadContext>()
                .VaultPrincipalDeprovisionings.SingleAsync(x => x.Id == requestId)).CurrentVaultId!.Value;
        }
        var client = apiFactory.CreateAuthenticatedClient(remainingMember, Permission.VaultManage);

        // When
        await CommitMemberRemovalRotationAsync(client, vaults[currentVaultId], remainingMember.Id, requestId);

        // Then
        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
            var operation = await readContext.VaultPrincipalDeprovisionings.SingleAsync(x => x.Id == requestId);
            operation.Status.ShouldBe(VaultPrincipalDeprovisioningStatus.WaitingForRotation);
            operation.CompletedVaultCount.ShouldBe(1u);
            operation.CurrentVaultId.ShouldNotBe(currentVaultId);
            (await readContext.VaultMembers.AnyAsync(x =>
                x.VaultId == currentVaultId && x.UserId == removedMember.Id)).ShouldBeFalse();
            (await readContext.VaultMembers.AnyAsync(x =>
                x.VaultId == operation.CurrentVaultId && x.UserId == removedMember.Id)).ShouldBeTrue();
        }

        await CommitMemberRemovalRotationAsync(
            client,
            vaults.Values.Single(x => x.Id != currentVaultId),
            remainingMember.Id,
            requestId);
        await using var verificationScope = apiFactory.Services.CreateAsyncScope();
        var completed = await verificationScope.ServiceProvider.GetRequiredService<VaultDbReadContext>()
            .VaultPrincipalDeprovisionings.SingleAsync(x => x.Id == requestId);
        completed.Status.ShouldBe(VaultPrincipalDeprovisioningStatus.Completed);
        completed.CompletedVaultCount.ShouldBe(2u);
    }

    private async Task ConsumeAsync(OrganizationMemberRemovalRequestedEvent message)
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var consumer = ActivatorUtilities.CreateInstance<OnOrganizationMemberRemovalRequested>(scope.ServiceProvider);
        await consumer.Consume(apiFactory.MockConsumeContext(message));
    }

    private async Task ConsumeAsync(AgentDeactivationRequestedEvent message)
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var consumer = ActivatorUtilities.CreateInstance<OnAgentDeactivationRequested>(scope.ServiceProvider);
        await consumer.Consume(apiFactory.MockConsumeContext(message));
    }

    private async Task AddVaultMemberAsync(Guid organizationId, Guid vaultId, Guid userId)
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<VaultDbWriteContext>();
        var vault = await writeContext.Vaults
            .Include(x => x.VaultMembers)
            .Include(x => x.VaultMemberKeyEnvelopes)
            .SingleAsync(x => x.OrganizationId == organizationId && x.Id == vaultId);
        vault.AddMember(userId, VaultFaker.CreateMemberKey(vault.Scope, userId), apiFactory.FakeClock.GetCurrentInstant());
        await writeContext.SaveChangesAsync();
    }

    private async Task SeedMemberDirectoryAsync(Guid userId)
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<VaultDbWriteContext>();
        writeContext.MemberKeyDirectory.Add(MemberKeyDirectoryEntry.Create(
            userId,
            new MemberRecipientKeyVersion(1),
            Enumerable.Range(0, VaultProtocol.FingerprintBytes).Select(x => (byte)x).ToArray(),
            new byte[VaultProtocol.FingerprintBytes],
            apiFactory.FakeClock.GetCurrentInstant()));
        await writeContext.SaveChangesAsync();
    }

    private async Task CommitMemberRemovalRotationAsync(
        HttpClient client,
        Palladin.Module.Vault.Domain.Vault vault,
        Guid remainingMemberId,
        Guid requestId)
    {
        Guid rotationId;
        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            rotationId = await scope.ServiceProvider.GetRequiredService<VaultDbReadContext>()
                .VaultKeyRotations.Where(x => x.DeprovisioningId == requestId && x.VaultId == vault.Id)
                .Select(x => x.Id)
                .SingleAsync();
        }
        var (_, claim) = await client.POSTAsync<
            ClaimVaultKeyRotationEndpoint,
            ClaimVaultKeyRotationRequest,
            ClaimVaultKeyRotationResponse>(new ClaimVaultKeyRotationRequest
            {
                VaultId = vault.Id,
                RotationId = rotationId,
            });
        var prepare = await client.PutAsJsonAsync(
            $"api/vaults/{vault.Id}/key-rotations/{rotationId}/batch",
            new PrepareVaultKeyRotationBatchRequest
            {
                VaultId = vault.Id,
                RotationId = rotationId,
                FencingToken = claim!.FencingToken,
                MemberVaultMetadata = VaultEnvelopeContractMapper.ToContract(
                    VaultFaker.CreateMetadata(vault.Scope, 2, new MemberKeyGeneration(2), new VaultKeyVersion(2))),
                MemberVaultKeys = [VaultEnvelopeContractMapper.ToContract(
                    VaultFaker.CreateMemberKey(
                        vault.Scope,
                        remainingMemberId,
                        new MemberKeyGeneration(2),
                        new VaultKeyVersion(2)))],
                DiscoveryKey = VaultContractFaker.CreateDiscoveryKey(
                    vault.OrganizationId, vault.Id, 2, 2, 2, 2, 2, 2),
                VaultPrivateKeys = VaultContractFaker.CreatePrivateKeys(
                    vault.OrganizationId, vault.Id, 2, 2, 2, 2, 2, 2),
                VaultAgentMessagePublicKey = VaultContractFaker.CreateAgentMessagePublicKey(),
                VaultManifestSigningPublicKey = VaultContractFaker.CreateManifestSigningPublicKey(),
            });
        prepare.StatusCode.ShouldBe(HttpStatusCode.OK, await prepare.Content.ReadAsStringAsync());
        var commit = await client.PostAsJsonAsync(
            $"api/vaults/{vault.Id}/key-rotations/{rotationId}/commit",
            new CommitVaultKeyRotationRequest
            {
                VaultId = vault.Id,
                RotationId = rotationId,
                FencingToken = claim.FencingToken,
            });
        commit.StatusCode.ShouldBe(HttpStatusCode.OK, await commit.Content.ReadAsStringAsync());
    }
}
