using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record EntryShareSessionRequest
{
    public Guid ShareId { get; init; }
    public Guid SessionId { get; init; }
    public string SessionToken { get; init; } = string.Empty;
    public override string ToString() => nameof(EntryShareSessionRequest);
}

[UsedImplicitly]
internal sealed class EntryShareSessionValidator : Validator<EntryShareSessionRequest>
{
    public EntryShareSessionValidator()
    {
        RuleFor(x => x.ShareId).NotEmpty();
        RuleFor(x => x.SessionId).NotEmpty();
        RuleFor(x => x.SessionToken).NotEmpty().Length(43);
    }
}
