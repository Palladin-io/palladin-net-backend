using Palladin.Core.Types;
using Palladin.Core.Types.Exceptions;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Tests.Integrations.Shared;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Vault;

public sealed class GrantMethodsTests
{
    [Fact]
    public void DurableScope_PreservesExactApprovedMethods()
    {
        var scope = GrantEnvelopeTestData.Scope(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            GrantMethods.Get | GrantMethods.Exec);

        scope.Methods.ShouldBe(GrantMethods.Get | GrantMethods.Exec);
    }

    [Fact]
    public void Mapper_RejectsInvalidMethodSet()
    {
        var organizationId = Guid.NewGuid();
        var vaultId = Guid.NewGuid();
        var grantId = Guid.NewGuid();
        var entryId = Guid.NewGuid();
        var contract = GrantEnvelopeTestData.Contract(
            organizationId, vaultId, grantId, entryId, Convert.ToBase64String(new byte[32]));

        Should.Throw<DomainException>(() =>
            GrantEnvelopeContractMapper.ToDomain(contract, (GrantMethods)128, contract.AgentId));
    }

    [Fact]
    public void Scope_PreservesCanonicalFieldIdentifiers()
    {
        var contract = GrantEnvelopeTestData.Contract(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Convert.ToBase64String(new byte[32]), fieldIds: ["password", "username"]);

        var scope = GrantEnvelopeContractMapper.ToDomain(contract, GrantMethods.Get, contract.AgentId);

        scope.FieldIds.ShouldBe("password\nusername");
    }
}
