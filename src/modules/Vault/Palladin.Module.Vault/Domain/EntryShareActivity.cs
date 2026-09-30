using NodaTime;
using Palladin.Core.Types;
using Palladin.Module.Vault.Contracts.Events;

namespace Palladin.Module.Vault.Domain;

internal sealed class EntryShareActivity
{
    public Guid ShareId { get; private set; }
    public long Sequence { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid VaultId { get; private set; }
    public Guid EntryId { get; private set; }
    public Guid SenderId { get; private set; }
    public EntryShareActivityKind Kind { get; private set; }
    public bool NotifySender { get; private set; }
    public Instant OccurredAt { get; private set; }
    public Instant? PublishedAt { get; private set; }

    private EntryShareActivity() { }

    internal static EntryShareActivity Create(
        EntryShare share, EntryShareActivityKind kind, Instant now, bool firstConfirmation) => new()
    {
        ShareId = share.Id,
        Sequence = share.ActivitySequence,
        OrganizationId = share.OrganizationId,
        VaultId = share.VaultId,
        EntryId = share.EntryId,
        SenderId = share.CreatedBy,
        Kind = kind,
        NotifySender = kind == EntryShareActivityKind.Confirmed && firstConfirmation && share.NotifyOnFirstReceipt,
        OccurredAt = now,
    };

    internal EntryShareActivityEvent ToEvent() => new(
        OrganizationId, VaultId, EntryId, ShareId, SenderId, Sequence, Kind, NotifySender, OccurredAt);

    internal void MarkPublished(Instant now) => PublishedAt ??= now;
}
