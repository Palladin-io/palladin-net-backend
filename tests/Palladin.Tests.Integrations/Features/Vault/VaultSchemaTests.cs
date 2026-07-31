using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Tests.Integrations.Shared;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Vault;

[Collection<ApiFactoryCollection>]
public sealed class VaultSchemaTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task CanonicalVaultModel_IsTenantFirstAndContainsNoPlaintextDisplayColumns()
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<VaultDbWriteContext>();
        var vault = context.Model.FindEntityType(typeof(Palladin.Module.Vault.Domain.Vault));
        vault.ShouldNotBeNull();

        vault.FindPrimaryKey()!.Properties.Select(x => x.Name)
            .ShouldBe([nameof(Palladin.Module.Vault.Domain.Vault.OrganizationId), nameof(Palladin.Module.Vault.Domain.Vault.Id)]);
        vault.GetProperties().Select(x => x.Name)
            .ShouldNotContain(name => new[] { "Name", "Description", "Icon", "Color" }.Contains(name));

        context.Model.GetEntityTypes().ShouldNotContain(entity =>
            entity.ClrType.Name == "InjectFailureReport");

        var failureReport = context.Model.FindEntityType(typeof(CredentialFailureReport));
        failureReport.ShouldNotBeNull();
        failureReport.GetProperties().Select(x => x.Name)
            .ShouldNotContain(name => new[] { "Note", "Reason", "Domain", "PageOrigin", "ControlsJson" }.Contains(name));
    }

    [Fact]
    public async Task MemberKeyEnvelope_HasTenantFirstGenerationIdentity()
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<VaultDbWriteContext>();
        var envelope = context.Model.FindEntityType(typeof(VaultMemberKeyEnvelope));
        envelope.ShouldNotBeNull();

        envelope.FindPrimaryKey()!.Properties.Select(x => x.Name).ShouldBe([
            nameof(VaultMemberKeyEnvelope.OrganizationId),
            nameof(VaultMemberKeyEnvelope.VaultId),
            nameof(VaultMemberKeyEnvelope.MemberId),
            nameof(VaultMemberKeyEnvelope.MemberKeyGeneration),
        ]);
    }

    [Fact]
    public async Task VaultKeyRotation_IsCascadeOwnedByVault()
    {
        // Given
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<VaultDbWriteContext>();

        // When
        var rotation = context.Model.FindEntityType(typeof(VaultKeyRotation));

        // Then
        var ownership = rotation!.GetForeignKeys().Single(x => x.PrincipalEntityType.ClrType == typeof(Palladin.Module.Vault.Domain.Vault));
        ownership.Properties.Select(x => x.Name).ShouldBe([
            nameof(VaultKeyRotation.OrganizationId),
            nameof(VaultKeyRotation.VaultId),
        ]);
        ownership.DeleteBehavior.ShouldBe(DeleteBehavior.Cascade);
    }

    [Fact]
    public async Task EncryptedPresentationAsset_IsTenantFirstAndLegacyDomainIndexIsAbsent()
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<VaultDbWriteContext>();
        var asset = context.Model.FindEntityType(typeof(EncryptedPresentationAsset));
        asset.ShouldNotBeNull();

        asset.FindPrimaryKey()!.Properties.Select(x => x.Name).ShouldBe([
            nameof(EncryptedPresentationAsset.OrganizationId),
            nameof(EncryptedPresentationAsset.VaultId),
            nameof(EncryptedPresentationAsset.Id),
        ]);
        asset.GetForeignKeys().Single(x => x.PrincipalEntityType.ClrType == typeof(VaultEntry))
            .Properties.Select(x => x.Name).ShouldBe([
                nameof(EncryptedPresentationAsset.OrganizationId),
                nameof(EncryptedPresentationAsset.VaultId),
                nameof(EncryptedPresentationAsset.EntryId),
            ]);
        context.Model.GetEntityTypes().ShouldNotContain(x => x.ClrType.Name == "CachedFavicon");
    }

}
