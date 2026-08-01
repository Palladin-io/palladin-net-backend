namespace Palladin.Core.Types.Exceptions;

public sealed class PublicAssetHostnameConflictException()
    : ConflictException("A hostname is already assigned to another catalog asset.");
