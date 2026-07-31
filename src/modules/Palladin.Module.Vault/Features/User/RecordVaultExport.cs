using Palladin.Core.Events;
using Palladin.Core.Security;
using Palladin.Module.Vault.Contracts.Events;
using Palladin.Module.Vault.Infrastructure.Authorization;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using NodaTime;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record RecordVaultExportRequest : IRequiresVaultMembership
{
    public Guid VaultId { get; init; }
    public string Format { get; init; } = string.Empty;
    public int EntryCount { get; init; }
}

[UsedImplicitly]
internal sealed class RecordVaultExportValidator : Validator<RecordVaultExportRequest>
{
    private static readonly string[] AllowedFormats = ["csv", "json"];

    // A whole-vault export can legitimately exceed the 500-item import batch cap, so this is a sanity
    // upper bound that keeps a bogus client-reported count out of the audit trail — not a batch size.
    private const int MaxExportedEntries = 50_000;

    public RecordVaultExportValidator()
    {
        RuleFor(x => x.VaultId).NotEmpty();
        RuleFor(x => x.Format).Must(f => AllowedFormats.Contains(f, StringComparer.OrdinalIgnoreCase));
        RuleFor(x => x.EntryCount).InclusiveBetween(1, MaxExportedEntries);
    }
}

// Records that a vault member exported entries client-side, for audit + analytics. The export itself
// happens entirely in the browser/app — the server never receives the decrypted entries, so this
// endpoint carries no entry content, only the format and the client-reported count.
[PublicAPI]
internal sealed class RecordVaultExportEndpoint(
    IEnumerable<IEventPublisher> eventPublishers,
    IClock clock) : Endpoint<RecordVaultExportRequest>
{
    public override void Configure()
    {
        Post("api/vaults/{vaultId:guid}/export-audit");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.VaultManage);
        this.RequireEmailVerified();
        this.RequireVaultMembership();
        Summary(summary =>
        {
            summary.Summary = "Record a client-side vault export for audit";
            summary.Description = "Logs that the caller exported this vault's entries on their device. Zero-knowledge: no decrypted content is sent — only the format (csv/json) and how many entries were exported. Emits an audit trail entry and an analytics event. The caller must be a member of the vault.";
        });
        Tags("Vault/Entries");
    }

    public override async Task HandleAsync(RecordVaultExportRequest req, CancellationToken ct)
    {
        var evt = new VaultExportedEvent(
            req.VaultId,
            User.GetUserId()!.Value,
            User.GetDisplayName(),
            req.Format.ToLowerInvariant(),
            req.EntryCount,
            clock.GetCurrentInstant());

        foreach (var publisher in eventPublishers)
        {
            await publisher.PublishAsync(evt, ct);
        }

        await Send.NoContentAsync(ct);
    }
}
