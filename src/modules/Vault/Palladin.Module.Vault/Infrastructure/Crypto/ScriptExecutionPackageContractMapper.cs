using Microsoft.AspNetCore.WebUtilities;
using Palladin.Core.Types.Exceptions;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Shared;

namespace Palladin.Module.Vault.Infrastructure.Crypto;

internal static class ScriptExecutionPackageContractMapper
{
    internal static (ScriptExecutionPackage Package, IReadOnlyList<ScriptExecutionScope> Scopes) ToDomain(
        ScriptExecutionPackageContract contract)
    {
        var scriptRevision = VaultEnvelopeContractMapper.ParseUInt64(contract.ScriptRevision);
        var packageRevision = VaultEnvelopeContractMapper.ParseUInt64(contract.PackageRevision);
        var fingerprint = VaultEnvelopeContractMapper.DecodeCanonicalBase64Url(
            contract.RecipientAgentKeyFingerprint);
        var vaultSigningFingerprint = VaultEnvelopeContractMapper.DecodeCanonicalBase64Url(
            contract.VaultSigningKeyFingerprint);
        var manifestDigest = VaultEnvelopeContractMapper.DecodeCanonicalBase64Url(contract.ManifestDigest);
        var ciphertext = VaultEnvelopeContractMapper.DecodeCanonicalBase64Url(contract.EncodedPackageCiphertext);
        var producerSignature = VaultEnvelopeContractMapper.DecodeCanonicalBase64Url(contract.ProducerSignature);
        if (fingerprint.Length != VaultProtocol.FingerprintBytes || manifestDigest.Length != 32
            || vaultSigningFingerprint.Length != VaultProtocol.FingerprintBytes
            || ciphertext.Length is < 16 or > 2_097_152 || producerSignature.Length != 64)
        {
            throw new DomainException("Script execution package encoding is invalid.");
        }

        var scopes = contract.Scopes
            .Select(scope => ScriptExecutionScope.Create(
                contract.OrganizationId,
                contract.VaultId,
                contract.GrantId,
                scope.EntryId,
                VaultEnvelopeContractMapper.ParseUInt64(scope.EntryRevision),
                scope.IsScript))
            .ToArray();
        var package = ScriptExecutionPackage.Create(
            contract.OrganizationId,
            contract.VaultId,
            contract.GrantId,
            contract.AgentId,
            contract.AgentAccessEpoch,
            contract.ScriptEntryId,
            scriptRevision,
            packageRevision,
            contract.ContractVersion,
            contract.RecipientAgentKeyVersion,
            fingerprint,
            contract.VaultSigningKeyVersion,
            vaultSigningFingerprint,
            manifestDigest,
            ciphertext,
            producerSignature);
        return (package, scopes);
    }

    internal static ScriptExecutionPackageContract ToContract(
        ScriptExecutionPackage package,
        IReadOnlyCollection<ScriptExecutionScope> scopes) =>
        new(
            package.ContractVersion,
            package.OrganizationId,
            package.VaultId,
            package.GrantId,
            package.AgentId,
            package.AgentAccessEpoch,
            package.ScriptEntryId,
            package.ScriptRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            package.PackageRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            package.RecipientAgentKeyVersion,
            WebEncoders.Base64UrlEncode(package.RecipientAgentKeyFingerprint),
            package.VaultSigningKeyVersion,
            WebEncoders.Base64UrlEncode(package.VaultSigningKeyFingerprint),
            WebEncoders.Base64UrlEncode(package.ManifestDigest),
            WebEncoders.Base64UrlEncode(package.EncodedPackageCiphertext),
            WebEncoders.Base64UrlEncode(package.ProducerSignature),
            scopes
                .OrderBy(scope => scope.EntryId.ToString("D"), StringComparer.Ordinal)
                .Select(scope => new ScriptExecutionScopeContract(
                    scope.EntryId,
                    scope.EntryRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    scope.IsScript))
                .ToArray());
}
