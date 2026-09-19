using Palladin.Core.Types.Exceptions;
using Palladin.Core.Types;

namespace Palladin.Module.Vault.Domain;

internal sealed class GrantEntryScope
{
    internal Guid OrganizationId { get; private set; }
    internal Guid VaultId { get; private set; }
    internal Guid GrantId { get; private set; }
    internal Guid EntryId { get; private set; }
    internal GrantMethods Methods { get; private set; }
    internal GrantDeliveryPolicy DeliveryPolicy { get; private set; }
    internal string FieldIds { get; private set; } = string.Empty;
    internal GrantFieldSelectionMode FieldSelectionMode { get; private set; }
    internal string SelectedFieldIds { get; private set; } = string.Empty;
    internal GrantEntryEnvelope? Envelope { get; private set; }

    private GrantEntryScope() { }

    internal static GrantEntryScope Create(
        EntryScope entry,
        Guid grantId,
        GrantMethods methods,
        GrantDeliveryPolicy deliveryPolicy,
        IEnumerable<string> fieldIds,
        GrantEntryEnvelope envelope)
    {
        entry.Validate();
        if (grantId == Guid.Empty || !methods.IsValidSet() || !deliveryPolicy.IsValid())
        {
            throw new DomainException("Grant scope is invalid.");
        }

        var submittedFields = fieldIds.ToArray();
        if (submittedFields.Any(x => x?.Contains('\n', StringComparison.Ordinal) == true
                                     || x?.Contains('\r', StringComparison.Ordinal) == true))
        {
            throw new DomainException("Grant field identifiers cannot contain line delimiters.");
        }

        var canonicalFields = submittedFields
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var serializedFields = string.Join('\n', canonicalFields);
        if (canonicalFields.Length == 0 || canonicalFields.Any(x => x.Length > 128)
            || serializedFields.Length > 32_768)
        {
            throw new DomainException("Grant scope must contain valid field identifiers.");
        }

        envelope.ValidateScope(entry, grantId);
        return new GrantEntryScope
        {
            OrganizationId = entry.OrganizationId,
            VaultId = entry.VaultId,
            GrantId = grantId,
            EntryId = entry.EntryId,
            Methods = methods,
            DeliveryPolicy = deliveryPolicy,
            FieldIds = serializedFields,
            SelectedFieldIds = serializedFields,
            Envelope = envelope,
        };
    }

    internal void SetFieldSelectionMode(GrantFieldSelectionMode mode)
    {
        if (!Enum.IsDefined(mode) || Envelope is null || Envelope.GrantEnvelopeRevision != 1)
        {
            throw new DomainException("Grant field selection can only be set at approval.");
        }

        FieldSelectionMode = mode;
        SelectedFieldIds = mode == GrantFieldSelectionMode.Selected ? FieldIds : string.Empty;
    }

    internal void DeleteEnvelope() => Envelope = null;

    internal void Refresh(GrantEntryEnvelope envelope)
    {
        envelope.ValidateScope(new EntryScope(OrganizationId, VaultId, EntryId), GrantId);
        if (Envelope is null
            || envelope.GrantEnvelopeRevision != Envelope.GrantEnvelopeRevision + 1
            || envelope.GrantKeyVersion != Envelope.GrantKeyVersion + 1
            || envelope.EntryRevision <= Envelope.EntryRevision)
        {
            throw new DomainException("Grant envelope refresh is stale or skips a revision.");
        }

        Envelope.RefreshFrom(envelope);
    }

    internal void RefreshScope(GrantEntryScope refreshed)
    {
        if (OrganizationId != refreshed.OrganizationId || VaultId != refreshed.VaultId
            || GrantId != refreshed.GrantId || EntryId != refreshed.EntryId || Methods != refreshed.Methods
            || DeliveryPolicy != refreshed.DeliveryPolicy
            || refreshed.Envelope is null)
        {
            throw new DomainException("Grant refresh scope is invalid.");
        }

        if (FieldSelectionMode == GrantFieldSelectionMode.Selected
            && refreshed.FieldIds.Split('\n').Except(SelectedFieldIds.Split('\n'), StringComparer.Ordinal).Any())
        {
            throw new DomainException("Grant refresh exceeds the owner's selected fields.");
        }

        Refresh(refreshed.Envelope);
        FieldIds = refreshed.FieldIds;
    }
}
