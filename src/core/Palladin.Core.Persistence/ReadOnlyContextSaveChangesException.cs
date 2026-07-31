namespace Palladin.Core.Persistence;

public class ReadOnlyContextSaveChangesException(string contextName) : InvalidOperationException(
    $"Cannot save changes to read-only database context '{contextName}'. Use the corresponding write context for write operations.");
