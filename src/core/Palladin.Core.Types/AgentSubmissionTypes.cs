namespace Palladin.Core.Types;

public enum AgentSubmissionKind : ushort
{
    New = 1,
    Update = 2,
}

public enum AgentSubmissionEntryType : ushort
{
    Key = 0,
    Credential = 1,
}

public enum AgentSubmissionState
{
    Pending = 1,
    Approved = 2,
    Trashed = 3,
    Purged = 4,
}

public enum AgentSubmissionAction
{
    Submitted = 1,
    Rewrapped = 2,
    Used = 3,
    Approved = 4,
    Denied = 5,
    Restored = 6,
    Purged = 7,
    RewrapSourceServed = 8,
}
