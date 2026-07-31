namespace Palladin.Core.Types.Exceptions;

public sealed class DomainException(string message) : Exception(message);
