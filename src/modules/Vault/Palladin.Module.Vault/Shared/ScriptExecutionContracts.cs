using FluentValidation;
using JetBrains.Annotations;

namespace Palladin.Module.Vault.Shared;

[PublicAPI]
public sealed record ScriptExecutionScopeContract(
    Guid EntryId,
    string EntryRevision,
    bool IsScript);

[PublicAPI]
public sealed record ScriptExecutionPackageContract(
    ushort ContractVersion,
    Guid OrganizationId,
    Guid VaultId,
    Guid GrantId,
    Guid AgentId,
    uint AgentAccessEpoch,
    Guid ScriptEntryId,
    string ScriptRevision,
    string PackageRevision,
    uint RecipientAgentKeyVersion,
    string RecipientAgentKeyFingerprint,
    uint VaultSigningKeyVersion,
    string VaultSigningKeyFingerprint,
    string ManifestDigest,
    string EncodedPackageCiphertext,
    string ProducerSignature,
    IReadOnlyList<ScriptExecutionScopeContract> Scopes);

internal sealed class ScriptExecutionPackageContractValidator
    : AbstractValidator<ScriptExecutionPackageContract>
{
    private const int MaximumEncodedPackageCharacters = ((2_097_152 + 2) / 3) * 4;

    internal ScriptExecutionPackageContractValidator()
    {
        RuleFor(x => x.ContractVersion).Equal((ushort)1);
        RuleFor(x => x.OrganizationId).NotEmpty();
        RuleFor(x => x.VaultId).NotEmpty();
        RuleFor(x => x.GrantId).NotEmpty();
        RuleFor(x => x.AgentId).NotEmpty();
        RuleFor(x => x.AgentAccessEpoch).GreaterThan(0u);
        RuleFor(x => x.ScriptEntryId).NotEmpty();
        RuleFor(x => x.ScriptRevision).NotEmpty();
        RuleFor(x => x.PackageRevision).NotEmpty();
        RuleFor(x => x.RecipientAgentKeyVersion).GreaterThan(0u);
        RuleFor(x => x.RecipientAgentKeyFingerprint).NotEmpty().MaximumLength(128);
        RuleFor(x => x.VaultSigningKeyVersion).GreaterThan(0u);
        RuleFor(x => x.VaultSigningKeyFingerprint).NotEmpty().MaximumLength(128);
        RuleFor(x => x.ManifestDigest).NotEmpty().MaximumLength(128);
        RuleFor(x => x.EncodedPackageCiphertext)
            .NotEmpty()
            .MaximumLength(MaximumEncodedPackageCharacters);
        RuleFor(x => x.ProducerSignature).NotEmpty().MaximumLength(128);
        RuleFor(x => x.Scopes).NotEmpty().Must(scopes => scopes.Count <= 65);
        RuleFor(x => x.Scopes)
            .Must(scopes => scopes.Select(scope => scope.EntryId).Distinct().Count() == scopes.Count)
            .WithMessage("Script execution scopes must contain unique Entry identifiers.");
        RuleFor(x => x.Scopes)
            .Must(scopes => scopes.Select(scope => scope.EntryId).SequenceEqual(
                scopes.Select(scope => scope.EntryId)
                    .OrderBy(id => id.ToString("D"), StringComparer.Ordinal)))
            .WithMessage("Script execution scopes must use canonical Entry identifier order.");
        RuleFor(x => x.Scopes)
            .Must((contract, scopes) => scopes.Count(scope => scope.IsScript) == 1
                && scopes.Single(scope => scope.IsScript).EntryId == contract.ScriptEntryId)
            .WithMessage("Script execution scopes must identify exactly one parent Script.");
        RuleForEach(x => x.Scopes).ChildRules(scope =>
        {
            scope.RuleFor(x => x.EntryId).NotEmpty();
            scope.RuleFor(x => x.EntryRevision).NotEmpty();
        });
    }
}
