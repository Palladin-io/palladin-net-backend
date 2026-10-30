using Palladin.Core.Types.Exceptions;

namespace Palladin.Module.Vault.Domain;

internal sealed class AgentSubmissionConflictException(string code)
    : ConflictException($"Agent submission conflict: {code}.")
{
    internal string Code { get; } = code;
}
