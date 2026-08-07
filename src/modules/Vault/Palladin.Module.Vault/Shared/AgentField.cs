using FluentValidation;
using JetBrains.Annotations;

namespace Palladin.Module.Vault.Shared;

// A custom field the owner explicitly marked as visible to the organization's agents.
// Plaintext by design — it joins Label/Description/UrlDomain as discovery metadata, never a secret.
// The encrypted blob remains the source of truth; clients mirror flagged fields here on save.
[PublicAPI]
public sealed record AgentField(string Label, string Value);

internal static class AgentFieldRules
{
    public const int MaxFields = 20;
    public const int MaxLabelLength = 200;
    public const int MaxValueLength = 2000;

    public static void ValidateAgentFields<T>(
        this IRuleBuilderInitialCollection<T, AgentField> rule) =>
        rule.ChildRules(field =>
        {
            field.RuleFor(f => f.Label).NotEmpty().MaximumLength(MaxLabelLength);
            field.RuleFor(f => f.Value).NotEmpty().MaximumLength(MaxValueLength);
        });
}
