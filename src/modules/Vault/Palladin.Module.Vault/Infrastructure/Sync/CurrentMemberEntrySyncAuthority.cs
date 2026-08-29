using System.Globalization;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Module.Identity.Contracts.ValueObjects;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Shared;

namespace Palladin.Module.Vault.Infrastructure.Sync;

internal sealed record CurrentMemberEntrySyncAuthority(
    Guid PrincipalId,
    Guid OrganizationId,
    uint OrganizationMembershipGeneration,
    Guid VaultId,
    Guid MemberId,
    ulong MemberSequence,
    ulong MinRetainedMemberSequence,
    uint MemberKeyGeneration,
    uint VaultKeyVersion,
    uint MemberRecipientKeyVersion,
    byte[] MemberRecipientKeyFingerprint,
    OrganizationOfflineAccessPolicy OfflineAccessPolicy,
    uint OfflineAccessPolicyVersion,
    MemberVaultKeyEnvelopeContract MemberVaultKey)
{
    internal CurrentMemberEntryAccessContext CreateAccessContext(Instant issuedAt) => new(
        CurrentMemberEntrySyncProtocol.AccessContextVersion,
        PrincipalId,
        OrganizationId,
        OrganizationMembershipGeneration.ToString(CultureInfo.InvariantCulture),
        VaultId,
        MemberId,
        MemberKeyGeneration,
        VaultKeyVersion,
        MemberRecipientKeyVersion,
        WebEncoders.Base64UrlEncode(MemberRecipientKeyFingerprint),
        CurrentMemberEntrySyncProtocol.ToWireValue(OfflineAccessPolicy),
        OfflineAccessPolicyVersion,
        issuedAt,
        issuedAt + CurrentMemberEntrySyncProtocol.ToLeaseDuration(OfflineAccessPolicy));

    internal CurrentMemberEntrySyncCursorContext CreateCursorContext() => new(
        PrincipalId,
        OrganizationId,
        OrganizationMembershipGeneration,
        VaultId,
        MemberId,
        MemberKeyGeneration,
        VaultKeyVersion,
        OfflineAccessPolicyVersion);

    internal bool HasSameBinding(CurrentMemberEntrySyncAuthority other) =>
        PrincipalId == other.PrincipalId
        && OrganizationId == other.OrganizationId
        && OrganizationMembershipGeneration == other.OrganizationMembershipGeneration
        && VaultId == other.VaultId
        && MemberId == other.MemberId
        && MemberSequence == other.MemberSequence
        && MinRetainedMemberSequence == other.MinRetainedMemberSequence
        && MemberKeyGeneration == other.MemberKeyGeneration
        && VaultKeyVersion == other.VaultKeyVersion
        && MemberRecipientKeyVersion == other.MemberRecipientKeyVersion
        && MemberRecipientKeyFingerprint.AsSpan().SequenceEqual(other.MemberRecipientKeyFingerprint)
        && OfflineAccessPolicy == other.OfflineAccessPolicy
        && OfflineAccessPolicyVersion == other.OfflineAccessPolicyVersion
        && MemberVaultKey == other.MemberVaultKey;

    internal bool HasSameSecurityBinding(CurrentMemberEntrySyncAuthority other) =>
        PrincipalId == other.PrincipalId
        && OrganizationId == other.OrganizationId
        && OrganizationMembershipGeneration == other.OrganizationMembershipGeneration
        && VaultId == other.VaultId
        && MemberId == other.MemberId
        && MinRetainedMemberSequence == other.MinRetainedMemberSequence
        && MemberKeyGeneration == other.MemberKeyGeneration
        && VaultKeyVersion == other.VaultKeyVersion
        && MemberRecipientKeyVersion == other.MemberRecipientKeyVersion
        && MemberRecipientKeyFingerprint.AsSpan().SequenceEqual(other.MemberRecipientKeyFingerprint)
        && OfflineAccessPolicy == other.OfflineAccessPolicy
        && OfflineAccessPolicyVersion == other.OfflineAccessPolicyVersion
        && MemberVaultKey == other.MemberVaultKey;

    internal static async Task<CurrentMemberEntrySyncAuthority?> AcquireAsync(
        Guid principalId,
        Guid organizationId,
        uint organizationMembershipGeneration,
        Guid vaultId,
        OrganizationOfflineAccessAuthority organizationAuthority,
        VaultDomainReadContext readContext,
        CancellationToken cancellationToken)
    {
        if (organizationAuthority.OrganizationMembershipGeneration != organizationMembershipGeneration
            || organizationAuthority.OfflineAccessPolicyVersion
                < CurrentMemberEntrySyncProtocol.InitialOfflinePolicyVersion)
        {
            return null;
        }

        var currentVault = await (
                from vault in readContext.Vaults
                join member in readContext.VaultMembers
                    on new { vault.OrganizationId, VaultId = vault.Id }
                    equals new { member.OrganizationId, member.VaultId }
                join memberVaultKey in readContext.VaultMemberKeyEnvelopes
                    on new
                    {
                        vault.OrganizationId,
                        VaultId = vault.Id,
                        MemberId = member.UserId,
                        vault.MemberKeyGeneration,
                    }
                    equals new
                    {
                        memberVaultKey.OrganizationId,
                        memberVaultKey.VaultId,
                        memberVaultKey.MemberId,
                        memberVaultKey.MemberKeyGeneration,
                    }
                join memberRecipientKey in readContext.MemberKeyDirectory
                    on member.UserId equals memberRecipientKey.UserId
                where vault.OrganizationId == organizationId
                      && vault.Id == vaultId
                      && member.UserId == principalId
                select new { Vault = vault, MemberVaultKey = memberVaultKey, MemberRecipientKey = memberRecipientKey })
            .SingleOrDefaultAsync(cancellationToken);
        if (currentVault is null
            || currentVault.Vault.ProtocolVersion != VaultProtocol.CurrentVersion
            || currentVault.MemberVaultKey.VaultKeyVersion != currentVault.Vault.CurrentVaultKeyVersion
            || currentVault.MemberVaultKey.RecipientKeyVersion != currentVault.MemberRecipientKey.KeyVersion
            || !currentVault.MemberVaultKey.RecipientKeyFingerprint.AsSpan()
                .SequenceEqual(currentVault.MemberRecipientKey.Fingerprint))
        {
            return null;
        }

        return new CurrentMemberEntrySyncAuthority(
            principalId,
            organizationId,
            organizationAuthority.OrganizationMembershipGeneration,
            vaultId,
            principalId,
            currentVault.Vault.MemberSequence.Value,
            currentVault.Vault.MinRetainedMemberSequence.Value,
            currentVault.Vault.MemberKeyGeneration.Value,
            currentVault.Vault.CurrentVaultKeyVersion.Value,
            currentVault.MemberRecipientKey.KeyVersion.Value,
            currentVault.MemberRecipientKey.Fingerprint.ToArray(),
            organizationAuthority.OfflineAccessPolicy,
            organizationAuthority.OfflineAccessPolicyVersion,
            VaultEnvelopeContractMapper.ToContract(currentVault.MemberVaultKey.GetWrappedVaultKey()));
    }
}
