using FluentValidation;
using JetBrains.Annotations;
using NodaTime;
using Palladin.Core.Types;
using System.Text.Json.Serialization;

namespace Palladin.Module.Vault.Shared;

[PublicAPI]
public sealed record ReasonEnvelopeBindingContract(
    string WrapperSuiteId,
    uint RecipientKeyVersion,
    string RecipientKeyFingerprint,
    ushort RequestedMethods);

[PublicAPI]
public sealed record GrantEnvelopeBindingContract(
    string EntryRevision,
    string WrapperSuiteId,
    uint RecipientKeyVersion,
    string RecipientKeyFingerprint,
    ushort ApprovedMethods,
    string FieldSetCommitment,
    Instant? ExpiresAt,
    int? RemainingUses);

[PublicAPI]
public sealed record EncryptedReasonEnvelopeContract(
    EnvelopeDescriptorContract<ReasonEnvelopeBindingContract> Descriptor,
    string EncodedSuitePayload,
    X25519WrappedKeyContract WrappedReasonDek,
    string AgentSignature)
{
    [JsonIgnore] public Guid OrganizationId => Descriptor.Scope.OrganizationId;
    [JsonIgnore] public Guid VaultId => Descriptor.Scope.VaultId;
    [JsonIgnore] public Guid EntryId => Descriptor.Scope.EntryId!.Value;
    [JsonIgnore] public Guid GrantRequestId => Descriptor.Scope.GrantOrRequestId!.Value;
    [JsonIgnore] public Guid AgentId => Descriptor.Scope.AgentId!.Value;
    [JsonIgnore] public string RequestRevision => Descriptor.ResourceRevision;
    [JsonIgnore] public uint ReasonKeyVersion => Descriptor.KeyVersion;
    [JsonIgnore] public uint MemberKeyGeneration => Descriptor.MemberKeyGeneration!.Value;
    [JsonIgnore] public uint AgentMessageKeyVersion => Descriptor.Binding.RecipientKeyVersion;
    [JsonIgnore] public string RecipientAgentMessageKeyFingerprint => Descriptor.Binding.RecipientKeyFingerprint;
    [JsonIgnore] public ushort RequestedMethods => Descriptor.Binding.RequestedMethods;
}

internal sealed class EncryptedReasonEnvelopeContractValidator : AbstractValidator<EncryptedReasonEnvelopeContract>
{
    private const int MaximumPayloadCharacters = ((4_096 + 24 + 2) / 3) * 4;

    public EncryptedReasonEnvelopeContractValidator()
    {
        RuleFor(x => x.Descriptor).NotNull()
            .SetValidator(new EnvelopeDescriptorContractValidator<ReasonEnvelopeBindingContract>(
                new ReasonEnvelopeBindingContractValidator())!);
        RuleFor(x => x.EncodedSuitePayload).NotEmpty().MaximumLength(MaximumPayloadCharacters);
        RuleFor(x => x.WrappedReasonDek).NotNull()
            .SetValidator(new X25519WrappedKeyContractValidator()!);
        RuleFor(x => x.AgentSignature).NotEmpty().Length(86);
        RuleFor(x => x.Descriptor!.Purpose).Equal(EnvelopePurposeContract.EncryptedReason)
            .When(x => x.Descriptor is not null);
        RuleFor(x => x.Descriptor!.Scope.EntryId).NotEmpty()
            .When(x => x.Descriptor?.Scope is not null);
        RuleFor(x => x.Descriptor!.Scope.GrantOrRequestId).NotEmpty()
            .When(x => x.Descriptor?.Scope is not null);
        RuleFor(x => x.Descriptor!.Scope.AgentId).NotEmpty()
            .When(x => x.Descriptor?.Scope is not null);
        RuleFor(x => x.Descriptor!.MemberKeyGeneration).NotNull().GreaterThan(0u)
            .When(x => x.Descriptor is not null);
        RuleFor(x => x.Descriptor!.Binding!.RequestedMethods)
            .Must(value => ((GrantMethods)value).IsValidSet())
            .When(x => x.Descriptor?.Binding is not null);
    }
}

[PublicAPI]
public sealed record GrantEntryEnvelopeContract(
    EnvelopeDescriptorContract<GrantEnvelopeBindingContract> Descriptor,
    string EncodedSuitePayload,
    X25519WrappedKeyContract WrappedGrantDek,
    string[] FieldIds)
{
    [JsonIgnore] public Guid OrganizationId => Descriptor.Scope.OrganizationId;
    [JsonIgnore] public Guid VaultId => Descriptor.Scope.VaultId;
    [JsonIgnore] public Guid EntryId => Descriptor.Scope.EntryId!.Value;
    [JsonIgnore] public Guid GrantId => Descriptor.Scope.GrantOrRequestId!.Value;
    [JsonIgnore] public Guid AgentId => Descriptor.Scope.AgentId!.Value;
    [JsonIgnore] public string GrantEnvelopeRevision => Descriptor.ResourceRevision;
    [JsonIgnore] public string EntryRevision => Descriptor.Binding.EntryRevision;
    [JsonIgnore] public uint GrantKeyVersion => Descriptor.KeyVersion;
    [JsonIgnore] public uint MemberKeyGeneration => Descriptor.MemberKeyGeneration!.Value;
    [JsonIgnore] public uint RecipientAgentKeyVersion => Descriptor.Binding.RecipientKeyVersion;
    [JsonIgnore] public ushort ApprovedMethods => Descriptor.Binding.ApprovedMethods;
    [JsonIgnore] public Instant? ExpiresAt => Descriptor.Binding.ExpiresAt;
    [JsonIgnore] public int? RemainingUses => Descriptor.Binding.RemainingUses;
}

internal sealed class GrantEntryEnvelopeContractValidator : AbstractValidator<GrantEntryEnvelopeContract>
{
    private const int MaximumPayloadCharacters = ((262_144 + 24 + 2) / 3) * 4;

    public GrantEntryEnvelopeContractValidator()
    {
        RuleFor(x => x.Descriptor).NotNull()
            .SetValidator(new EnvelopeDescriptorContractValidator<GrantEnvelopeBindingContract>(
                new GrantEnvelopeBindingContractValidator())!);
        RuleFor(x => x.EncodedSuitePayload).NotEmpty().MaximumLength(MaximumPayloadCharacters);
        RuleFor(x => x.WrappedGrantDek).NotNull()
            .SetValidator(new X25519WrappedKeyContractValidator()!);
        RuleFor(x => x.FieldIds).NotEmpty();
        RuleFor(x => x.Descriptor!.Purpose).Equal(EnvelopePurposeContract.GrantPayload)
            .When(x => x.Descriptor is not null);
        RuleFor(x => x.Descriptor!.Scope.EntryId).NotEmpty()
            .When(x => x.Descriptor?.Scope is not null);
        RuleFor(x => x.Descriptor!.Scope.GrantOrRequestId).NotEmpty()
            .When(x => x.Descriptor?.Scope is not null);
        RuleFor(x => x.Descriptor!.Scope.AgentId).NotEmpty()
            .When(x => x.Descriptor?.Scope is not null);
        RuleFor(x => x.Descriptor!.MemberKeyGeneration).NotNull().GreaterThan(0u)
            .When(x => x.Descriptor is not null);
        RuleFor(x => x.Descriptor!.Binding!.RemainingUses)
            .GreaterThan(0)
            .When(x => x.Descriptor?.Binding?.RemainingUses is not null);
        RuleFor(x => x.Descriptor!.Binding!)
            .Must(binding => !binding.ExpiresAt.HasValue || !binding.RemainingUses.HasValue)
            .When(x => x.Descriptor?.Binding is not null);
    }
}

internal sealed class EnvelopeDescriptorContractValidator<TBinding>
    : AbstractValidator<EnvelopeDescriptorContract<TBinding>>
{
    internal EnvelopeDescriptorContractValidator(IValidator<TBinding> bindingValidator)
    {
        RuleFor(x => x.Scope).NotNull().SetValidator(new EnvelopeScopeContractValidator()!);
        RuleFor(x => x.Binding).NotNull().SetValidator(bindingValidator!);
    }
}

internal sealed class EnvelopeScopeContractValidator : AbstractValidator<EnvelopeScopeContract>
{
    internal EnvelopeScopeContractValidator()
    {
        RuleFor(x => x.OrganizationId).NotEmpty();
        RuleFor(x => x.VaultId).NotEmpty();
    }
}

internal sealed class ReasonEnvelopeBindingContractValidator : AbstractValidator<ReasonEnvelopeBindingContract>
{
}

internal sealed class GrantEnvelopeBindingContractValidator : AbstractValidator<GrantEnvelopeBindingContract>
{
}

internal sealed class X25519WrappedKeyContractValidator : AbstractValidator<X25519WrappedKeyContract>
{
    internal X25519WrappedKeyContractValidator()
    {
        RuleFor(x => x.Descriptor).NotNull()
            .SetValidator(new X25519WrapperDescriptorContractValidator()!);
        RuleFor(x => x.EncodedSealedKeyPackage).NotEmpty();
    }
}

internal sealed class X25519WrapperDescriptorContractValidator
    : AbstractValidator<X25519WrapperDescriptorContract>
{
    internal X25519WrapperDescriptorContractValidator()
    {
        RuleFor(x => x.Scope).NotNull().SetValidator(new EnvelopeScopeContractValidator()!);
    }
}
