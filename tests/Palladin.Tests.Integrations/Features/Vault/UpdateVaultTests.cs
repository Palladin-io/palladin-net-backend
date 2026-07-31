using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Palladin.Core.Security;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Features;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Fakers;
using Palladin.Tests.Integrations.Shared.Seeders;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Vault;

[Collection<ApiFactoryCollection>]
public sealed class UpdateVaultTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_MemberAdvancesEncryptedMetadataRevision_Then_ReplacesCiphertext()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.VaultManage);
        var replacement = VaultFaker.CreateMetadata(vault.Scope, 2);

        var response = await client.PutAsJsonAsync($"api/vaults/{vault.Id}", new UpdateVaultRequest
        {
            Id = vault.Id,
            MemberVaultMetadata = VaultEnvelopeContractMapper.ToContract(replacement),
        });

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var persisted = await readContext.Vaults.SingleAsync(v => v.Id == vault.Id);
        persisted.MetadataRevision.Value.ShouldBe((ulong)2);
        SuitePayload.Ciphertext(persisted.MemberVaultMetadataEncodedSuitePayload).ShouldBe(replacement.Ciphertext);
    }

    [Fact]
    public async Task When_MemberLacksVaultManage_Then_RejectsUpdate()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.VaultCreate);

        var response = await client.PutAsJsonAsync($"api/vaults/{vault.Id}", new UpdateVaultRequest
        {
            Id = vault.Id,
            MemberVaultMetadata = VaultEnvelopeContractMapper.ToContract(
                VaultFaker.CreateMetadata(vault.Scope, 2)),
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task When_RevisionDoesNotAdvance_Then_RejectsUpdate()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.VaultManage);
        var stale = VaultFaker.CreateMetadata(vault.Scope, 1);

        var response = await client.PutAsJsonAsync($"api/vaults/{vault.Id}", new UpdateVaultRequest
        {
            Id = vault.Id,
            MemberVaultMetadata = VaultEnvelopeContractMapper.ToContract(stale),
        });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task When_ClientSkipsServerOwnedMetadataRevision_Then_RejectsUpdate()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.VaultManage);
        var skipped = VaultFaker.CreateMetadata(vault.Scope, 3);

        var response = await client.PutAsJsonAsync($"api/vaults/{vault.Id}", new UpdateVaultRequest
        {
            Id = vault.Id,
            MemberVaultMetadata = VaultEnvelopeContractMapper.ToContract(skipped),
        });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
