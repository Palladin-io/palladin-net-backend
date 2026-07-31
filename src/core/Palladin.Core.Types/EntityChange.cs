using JetBrains.Annotations;

namespace Palladin.Core.Types;

// Classifies a replication/upsert integration event as the first write (Created) or a subsequent one
// (Updated), so a single Upserted event serves replication, analytics and audit without a separate
// Created/Updated event pair.
[PublicAPI]
public enum EntityChange
{
    Created = 1,
    Updated = 2,
}
