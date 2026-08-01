namespace Palladin.Core.Types.Exceptions;

public sealed class PublicAssetStateConflictException()
    : ConflictException("Public asset state changed concurrently.");
