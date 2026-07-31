namespace Palladin.Core.Types.Exceptions;

// Base for domain conflicts (incompatible state transitions, etc.) mapped to HTTP 409 by the global
// exception handler. Modules derive concrete exceptions with specific messages.
public abstract class ConflictException(string message) : Exception(message);
