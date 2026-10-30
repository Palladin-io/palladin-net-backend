namespace Palladin.Module.Vault.Infrastructure.Submissions;

internal sealed class AgentSubmissionOptions
{
    internal const string Position = "Modules:Vault:AgentSubmissions";

    public int ApproverChallengeLifetimeSeconds { get; init; } = 300;
    public int MaximumListItems { get; init; } = 500;
}
