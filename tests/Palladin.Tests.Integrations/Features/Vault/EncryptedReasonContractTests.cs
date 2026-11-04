using Palladin.Tests.Integrations.Shared.Fakers;
using Microsoft.AspNetCore.WebUtilities;
using Palladin.Core.Types;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Shared;
using Palladin.Tests.Integrations.Shared;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Vault;

public sealed class EncryptedReasonContractTests
{
    [Fact]
    public void EncryptedReason_WithNullNestedStructure_FailsValidationWithoutThrowing()
    {
        var signing = AgentRequestSigning.Generate();
        var fingerprint = WebEncoders.Base64UrlEncode(VaultTrustAnchorFaker.AgentMessageFingerprint(Enumerable.Repeat((byte)0x31, Palladin.Core.Cryptography.KeyWrapperSuites.Current.PublicKeyBytes).ToArray()));
        var contract = GrantEnvelopeTestData.EncryptedReason(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), signing, fingerprint);
        var malformed = contract with
        {
            Descriptor = contract.Descriptor with { Scope = null!, Binding = null! },
            WrappedReasonDek = contract.WrappedReasonDek with { Descriptor = null! },
        };

        var result = new EncryptedReasonEnvelopeContractValidator().Validate(malformed);

        result.IsValid.ShouldBeFalse();
        Should.NotThrow(() => result.Errors.Select(x => x.PropertyName).ToArray());
    }

    [Fact]
    public void EncryptedReasonMapper_WithNullNestedStructure_FailsAsDomainValidation()
    {
        var signing = AgentRequestSigning.Generate();
        var fingerprint = WebEncoders.Base64UrlEncode(VaultTrustAnchorFaker.AgentMessageFingerprint(Enumerable.Repeat((byte)0x31, Palladin.Core.Cryptography.KeyWrapperSuites.Current.PublicKeyBytes).ToArray()));
        var contract = GrantEnvelopeTestData.EncryptedReason(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), signing, fingerprint);
        var malformed = contract with
        {
            WrappedReasonDek = contract.WrappedReasonDek with { Descriptor = null! },
        };

        Should.Throw<Palladin.Core.Types.Exceptions.DomainException>(() => EncryptedReasonValidator.ToDomain(
            malformed, signing.PublicKeyBase64, WebEncoders.Base64UrlDecode(fingerprint), VaultTrustAnchorFaker.AgentMessageKeySuiteId));
    }

    [Fact]
    public void WrapperFingerprintDifferentFromParentBinding_FailsClosedBeforeSignatureVerification()
    {
        var signing = AgentRequestSigning.Generate();
        var recipientKey = Enumerable.Repeat((byte)0x31, Palladin.Core.Cryptography.KeyWrapperSuites.Current.PublicKeyBytes).ToArray();
        var fingerprint = WebEncoders.Base64UrlEncode(
            VaultTrustAnchorFaker.AgentMessageFingerprint(recipientKey));
        var contract = GrantEnvelopeTestData.EncryptedReason(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), signing, fingerprint);
        var changed = contract with
        {
            WrappedReasonDek = contract.WrappedReasonDek with
            {
                Descriptor = contract.WrappedReasonDek.Descriptor with
                {
                    RecipientFingerprint = WebEncoders.Base64UrlEncode(
                        Enumerable.Repeat((byte)0xEE, 32).ToArray()),
                },
            },
        };

        Assert.Throws<Palladin.Core.Types.Exceptions.DomainException>(() => EncryptedReasonValidator.ToDomain(
            changed, signing.PublicKeyBase64, WebEncoders.Base64UrlDecode(fingerprint), VaultTrustAnchorFaker.AgentMessageKeySuiteId));
    }

    [Fact]
    public void WrapperMemberKeyGenerationDifferentFromParent_FailsClosedBeforeSignatureVerification()
    {
        var signing = AgentRequestSigning.Generate();
        var fingerprint = WebEncoders.Base64UrlEncode(VaultTrustAnchorFaker.AgentMessageFingerprint(Enumerable.Repeat((byte)0x31, Palladin.Core.Cryptography.KeyWrapperSuites.Current.PublicKeyBytes).ToArray()));
        var contract = GrantEnvelopeTestData.EncryptedReason(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), signing, fingerprint);
        var changed = contract with
        {
            WrappedReasonDek = contract.WrappedReasonDek with
            {
                Descriptor = contract.WrappedReasonDek.Descriptor with { MemberKeyGeneration = 2 },
            },
        };

        Should.Throw<Palladin.Core.Types.Exceptions.DomainException>(() => EncryptedReasonValidator.ToDomain(
            changed, signing.PublicKeyBase64, WebEncoders.Base64UrlDecode(fingerprint), VaultTrustAnchorFaker.AgentMessageKeySuiteId));
    }

    [Fact]
    public void CanonicalEncryptedReasonSignature_IsAcceptedExactly()
    {
        var signing = AgentRequestSigning.Generate();
        var recipientKey = Enumerable.Repeat((byte)0x31, Palladin.Core.Cryptography.KeyWrapperSuites.Current.PublicKeyBytes).ToArray();
        var fingerprint = WebEncoders.Base64UrlEncode(
            VaultTrustAnchorFaker.AgentMessageFingerprint(recipientKey));
        var contract = GrantEnvelopeTestData.EncryptedReason(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), signing, fingerprint);

        var result = EncryptedReasonValidator.ToDomain(
            contract, signing.PublicKeyBase64, WebEncoders.Base64UrlDecode(fingerprint), VaultTrustAnchorFaker.AgentMessageKeySuiteId);

        Assert.Equal(1u, result.MemberKeyGeneration);
        Assert.Equal(WebEncoders.Base64UrlDecode(fingerprint), result.RecipientAgentMessageKeyFingerprint);
    }
}
