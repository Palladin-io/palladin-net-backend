namespace Palladin.Module.Identity.Domain;

internal sealed record WaitlistQualification(
    string AudienceType,
    string AgentFramework,
    string? AgentFrameworkOther,
    string CredentialedWorkflow,
    string? CurrentWorkaround,
    bool ReadyWithin30Days,
    string CampaignSource)
{
    internal const int AgentFrameworkOtherMaxLength = 100;
    internal const int CredentialedWorkflowMaxLength = 1000;
    internal const int CurrentWorkaroundMaxLength = 1000;
    internal const int CampaignSourceMaxLength = 32;

    internal static readonly string[] AudienceTypes = ["individual", "team"];
    internal static readonly string[] AgentFrameworks =
        ["claude-code", "codex", "gemini-cli", "openclaw", "playwright", "other"];
    internal static readonly string[] CampaignSources =
        ["direct", "google", "github", "linkedin", "x", "newsletter", "other"];

    internal static WaitlistQualification Create(
        string audienceType,
        string agentFramework,
        string? agentFrameworkOther,
        string credentialedWorkflow,
        string? currentWorkaround,
        bool readyWithin30Days,
        string? campaignSource) => new(
            audienceType.Trim().ToLowerInvariant(),
            agentFramework.Trim().ToLowerInvariant(),
            NullIfWhiteSpace(agentFrameworkOther),
            credentialedWorkflow.Trim(),
            NullIfWhiteSpace(currentWorkaround),
            readyWithin30Days,
            campaignSource?.Trim().ToLowerInvariant() ?? "direct");

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
