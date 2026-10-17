using Palladin.Core.Types.Exceptions;

namespace Palladin.Module.Vault.Domain;

internal sealed class EntryShareUnavailableException()
    : ConflictException("The shared entry is unavailable.");
