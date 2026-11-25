using Palladin.Core.Types.Exceptions;

namespace Palladin.Module.Vault.Domain;

internal sealed class MemberKeyEnvelopeChangedException()
    : ConflictException("The Member Vault-key envelope changed. Reload the current envelope and retry.");
