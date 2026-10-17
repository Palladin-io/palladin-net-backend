namespace Palladin.Core.Types;

public enum EntryShareRecipientMode
{
    NamedRecipient = 1,
    AnyoneWithLink = 2,
}

public enum EntryShareProtection
{
    None = 0,
    Password = 1,
    Pin = 2,
}

public enum EntryShareActivityKind
{
    Created = 1,
    Delivered = 2,
    Confirmed = 3,
    ProtectionChanged = 4,
    Expired = 5,
    RevokedBySender = 6,
    EndedByRecipient = 7,
    SourceAccessRemoved = 8,
}
