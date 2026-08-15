using NodaTime;
using Palladin.Core.Types;
using Palladin.Core.Types.Exceptions;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Shared;
using Palladin.Tests.Integrations.Shared;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Vault;

public sealed class GrantDomainTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 7, 19, 12, 0);

    [Fact]
    public void GrantEnvelope_WithNullNestedStructure_FailsValidationWithoutThrowing()
    {
        var contract = GrantEnvelopeTestData.Contract(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Convert.ToBase64String(new byte[32]));
        var malformed = contract with
        {
            Descriptor = contract.Descriptor with { Scope = null!, Binding = null! },
            WrappedGrantDek = contract.WrappedGrantDek with { Descriptor = null! },
        };

        var result = new GrantEntryEnvelopeContractValidator().Validate(malformed);

        result.IsValid.ShouldBeFalse();
        Should.NotThrow(() => result.Errors.Select(x => x.PropertyName).ToArray());
    }

    [Fact]
    public void GrantEnvelopeMapper_WithNullNestedStructure_FailsAsDomainValidation()
    {
        var contract = GrantEnvelopeTestData.Contract(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Convert.ToBase64String(new byte[32]));
        var malformed = contract with
        {
            WrappedGrantDek = contract.WrappedGrantDek with { Descriptor = null! },
        };

        Should.Throw<DomainException>(() => GrantEnvelopeContractMapper.ToDomain(
            malformed, GrantMethods.Get, contract.AgentId));
    }

    [Fact]
    public void GrantEntryScope_WithDifferentConcreteAgent_FailsClosed()
    {
        var contract = GrantEnvelopeTestData.Contract(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Convert.ToBase64String(new byte[32]));

        Should.Throw<DomainException>(() => GrantEnvelopeContractMapper.ToDomain(
            contract, GrantMethods.Get, Guid.NewGuid()));
    }

    [Fact]
    public void GrantEntryScope_WithWrapperRecipientFingerprintDifferentFromParent_FailsClosed()
    {
        var contract = GrantEnvelopeTestData.Contract(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Convert.ToBase64String(new byte[32]));
        var changed = contract with
        {
            WrappedGrantDek = contract.WrappedGrantDek with
            {
                Descriptor = contract.WrappedGrantDek.Descriptor with
                {
                    RecipientFingerprint = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(
                        Enumerable.Repeat((byte)0xEE, 32).ToArray()),
                },
            },
        };

        Should.Throw<DomainException>(() => GrantEnvelopeContractMapper.ToDomain(
            changed, GrantMethods.Get, contract.AgentId));
    }

    [Fact]
    public void GrantEntryScope_WithNegativeRemainingUses_FailsAsDomainValidation()
    {
        var contract = GrantEnvelopeTestData.Contract(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Convert.ToBase64String(new byte[32]));
        var changed = contract with
        {
            Descriptor = contract.Descriptor with
            {
                Binding = contract.Descriptor.Binding with { RemainingUses = -1 },
            },
        };

        Should.Throw<DomainException>(() => GrantEnvelopeContractMapper.ToDomain(
            changed, GrantMethods.Get, contract.AgentId));
    }

    [Fact]
    public void GrantEntryScope_WithBothExpiryPolicies_FailsClosed()
    {
        var contract = GrantEnvelopeTestData.Contract(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Convert.ToBase64String(new byte[32]));
        var changed = contract with
        {
            Descriptor = contract.Descriptor with
            {
                Binding = contract.Descriptor.Binding with
                {
                    ExpiresAt = Now + Duration.FromHours(1),
                    RemainingUses = 1,
                },
            },
        };

        new GrantEntryEnvelopeContractValidator().Validate(changed).IsValid.ShouldBeFalse();
        Should.Throw<DomainException>(() => GrantEnvelopeContractMapper.ToDomain(
            changed, GrantMethods.Get, contract.AgentId));
    }

    [Fact]
    public void GrantExpiryPolicy_MatchesLifetimeTimeAndUsesWireContract()
    {
        var agentId = Guid.Parse("55555555-5555-4555-8555-555555555555");
        var cases = new (Instant? ExpiresAt, int? RemainingUses, bool IsValid)[]
        {
            (null, null, true),
            (Now + Duration.FromHours(1), null, true),
            (null, 1, true),
            (Now + Duration.FromHours(1), 1, false),
        };

        foreach (var item in cases)
        {
            var contract = GrantEnvelopeTestData.Contract(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                Guid.NewGuid(), Convert.ToBase64String(new byte[32]), item.ExpiresAt,
                item.RemainingUses, agentId: agentId);

            new GrantEntryEnvelopeContractValidator().Validate(contract).IsValid.ShouldBe(item.IsValid);
            if (item.IsValid)
            {
                Should.NotThrow(() => GrantEnvelopeContractMapper.ToDomain(
                    contract, GrantMethods.Get, agentId));
            }
            else
            {
                Should.Throw<DomainException>(() => GrantEnvelopeContractMapper.ToDomain(
                    contract, GrantMethods.Get, agentId));
            }
        }
    }

    [Fact]
    public void GrantEntryScope_WithDifferentWrapperMemberKeyGeneration_FailsClosed()
    {
        var contract = GrantEnvelopeTestData.Contract(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Convert.ToBase64String(new byte[32]));
        var changed = contract with
        {
            WrappedGrantDek = contract.WrappedGrantDek with
            {
                Descriptor = contract.WrappedGrantDek.Descriptor with { MemberKeyGeneration = 2 },
            },
        };

        Should.Throw<DomainException>(() => GrantEnvelopeContractMapper.ToDomain(
            changed, GrantMethods.Get, contract.AgentId));
    }

    [Fact]
    public void GrantEntryScope_BindsFullTenantAndEntryScope()
    {
        var organizationId = Guid.NewGuid();
        var vaultId = Guid.NewGuid();
        var grantId = Guid.NewGuid();
        var entryId = Guid.NewGuid();

        var scope = GrantEnvelopeTestData.Scope(organizationId, vaultId, grantId, entryId);

        scope.OrganizationId.ShouldBe(organizationId);
        scope.VaultId.ShouldBe(vaultId);
        scope.GrantId.ShouldBe(grantId);
        scope.EntryId.ShouldBe(entryId);
        scope.Envelope.ShouldNotBeNull();
    }

    [Theory]
    [InlineData("username\npassword")]
    [InlineData("username\rpassword")]
    public void GrantEntryScope_RejectsAmbiguousFieldDelimiters(string fieldId)
    {
        var organizationId = Guid.NewGuid();
        var vaultId = Guid.NewGuid();
        var grantId = Guid.NewGuid();
        var entryId = Guid.NewGuid();
        Should.Throw<DomainException>(() =>
            GrantEnvelopeTestData.Contract(
                organizationId,
                vaultId,
                grantId,
                entryId,
                Convert.ToBase64String(new byte[32]),
                fieldIds: [fieldId]));
    }

    [Theory]
    [InlineData("02", "1")]
    [InlineData("1", "01")]
    public void GrantEntryScope_RejectsNonCanonicalRevisionStrings(
        string envelopeRevision,
        string entryRevision)
    {
        var valid = GrantEnvelopeTestData.Contract(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Convert.ToBase64String(new byte[32]));
        var contract = valid with
        {
            Descriptor = valid.Descriptor with
            {
                ResourceRevision = envelopeRevision,
                Binding = valid.Descriptor.Binding with { EntryRevision = entryRevision },
            },
        };

        Should.Throw<DomainException>(() =>
            Palladin.Module.Vault.Infrastructure.Crypto.GrantEnvelopeContractMapper.ToDomain(
                contract, GrantMethods.Get, contract.AgentId));
    }

    [Fact]
    public void GrantEntryScope_RejectsSerializedFieldScopeBeyondPersistenceLimit()
    {
        var fields = Enumerable.Range(0, 300)
            .Select(index => $"{index:D3}-{new string('x', 124)}")
            .ToArray();
        var contract = GrantEnvelopeTestData.Contract(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Convert.ToBase64String(new byte[32]),
            fieldIds: fields);

        Should.Throw<DomainException>(() =>
            Palladin.Module.Vault.Infrastructure.Crypto.GrantEnvelopeContractMapper.ToDomain(
                contract, GrantMethods.Get, contract.AgentId));
    }

    [Fact]
    public void Refresh_RequiresStrictlyMonotonicEnvelopeAndKeyVersions()
    {
        var organizationId = Guid.NewGuid();
        var vaultId = Guid.NewGuid();
        var grantId = Guid.NewGuid();
        var entryId = Guid.NewGuid();
        var scope = GrantEnvelopeTestData.Scope(organizationId, vaultId, grantId, entryId);
        var replay = GrantEnvelopeTestData.Scope(organizationId, vaultId, grantId, entryId).Envelope!;

        Should.Throw<DomainException>(() => scope.Refresh(replay));
    }

    [Fact]
    public void Refresh_AcceptsExactlyNextEnvelopeAndKeyVersions()
    {
        var organizationId = Guid.NewGuid();
        var vaultId = Guid.NewGuid();
        var grantId = Guid.NewGuid();
        var entryId = Guid.NewGuid();
        var scope = GrantEnvelopeTestData.Scope(organizationId, vaultId, grantId, entryId);
        var trackedEnvelope = scope.Envelope;
        var next = GrantEnvelopeTestData.Scope(
            organizationId, vaultId, grantId, entryId,
            entryRevision: 2, envelopeRevision: 2, grantKeyVersion: 2).Envelope!;

        scope.Refresh(next);

        scope.Envelope.ShouldBeSameAs(trackedEnvelope);
        scope.Envelope!.EntryRevision.ShouldBe(2UL);
        scope.Envelope.GrantEnvelopeRevision.ShouldBe(2UL);
        scope.Envelope.GrantKeyVersion.ShouldBe(2U);
    }

    [Fact]
    public void Refresh_RejectsCrossScopeSubstitution()
    {
        var organizationId = Guid.NewGuid();
        var vaultId = Guid.NewGuid();
        var grantId = Guid.NewGuid();
        var entryId = Guid.NewGuid();
        var scope = GrantEnvelopeTestData.Scope(organizationId, vaultId, grantId, entryId);
        var foreign = GrantEnvelopeTestData.Scope(
            organizationId, vaultId, grantId, Guid.NewGuid(),
            entryRevision: 2, envelopeRevision: 2, grantKeyVersion: 2).Envelope!;

        Should.Throw<DomainException>(() => scope.Refresh(foreign));
    }

    [Fact]
    public void RefreshScope_AllowsOwnerClientToReplaceAuthenticatedFieldSet()
    {
        var organizationId = Guid.NewGuid();
        var vaultId = Guid.NewGuid();
        var grantId = Guid.NewGuid();
        var entryId = Guid.NewGuid();
        var scope = GrantEnvelopeTestData.Scope(
            organizationId, vaultId, grantId, entryId, fieldIds: ["password", "totp"]);
        var narrowed = GrantEnvelopeTestData.Scope(
            organizationId, vaultId, grantId, entryId,
            entryRevision: 2, envelopeRevision: 2, grantKeyVersion: 2, fieldIds: ["password"]);

        scope.RefreshScope(narrowed);

        scope.FieldIds.ShouldBe("password");
        var broadened = GrantEnvelopeTestData.Scope(
            organizationId, vaultId, grantId, entryId,
            entryRevision: 3, envelopeRevision: 3, grantKeyVersion: 3, fieldIds: ["password", "notes"]);
        scope.RefreshScope(broadened);
        scope.FieldIds.ShouldBe("notes\npassword");
    }

    [Fact]
    public void Revoke_HardDeletesPayloadButKeepsDurableScope()
    {
        var organizationId = Guid.NewGuid();
        var vaultId = Guid.NewGuid();
        var grantId = Guid.NewGuid();
        var entryId = Guid.NewGuid();
        var scope = GrantEnvelopeTestData.Scope(organizationId, vaultId, grantId, entryId);
        var grant = GranularGrant.CreateProactively(
            grantId, vaultId, organizationId, Guid.NewGuid(), "pk", entryId, scope,
            null, 5, "uses", GrantMethods.Get, Guid.NewGuid(),
            new GrantNames("agent", "entry", "vault", "actor"), Now, 1);

        grant.Revoke(Guid.NewGuid(), new GrantNames("agent", "entry", "vault", "actor"), Now);

        grant.Status.ShouldBe(GrantStatus.Revoked);
        grant.GrantEntryScopes.Single().Envelope.ShouldBeNull();
        grant.GrantEntryScopes.Single().EntryId.ShouldBe(entryId);
    }

    [Fact]
    public void FullGrant_CoversOnlyScopesWithLivePayload()
    {
        var organizationId = Guid.NewGuid();
        var vaultId = Guid.NewGuid();
        var grantId = Guid.NewGuid();
        var entryId = Guid.NewGuid();
        var scope = GrantEnvelopeTestData.Scope(organizationId, vaultId, grantId, entryId);
        var grant = FullGrant.CreateProactively(
            grantId, vaultId, organizationId, Guid.NewGuid(), "pk", [scope],
            null, null, "lifetime", GrantMethods.Get, Guid.NewGuid(),
            new GrantNames("agent", null, "vault", "actor"), Now, 1);

        grant.Covers(entryId).ShouldBeTrue();
        grant.Revoke(Guid.NewGuid(), new GrantNames("agent", null, "vault", "actor"), Now);
        grant.Covers(entryId).ShouldBeFalse();
    }
}
