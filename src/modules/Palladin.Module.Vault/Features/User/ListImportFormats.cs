using Palladin.Core.Security;
using FastEndpoints;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record ImportFormat(string Id, string Name, IReadOnlyList<string> Extensions, string FileType);

[PublicAPI]
public sealed record ListImportFormatsResponse(IReadOnlyList<ImportFormat> Formats);

[PublicAPI]
internal sealed class ListImportFormatsEndpoint : EndpointWithoutRequest<ListImportFormatsResponse>
{
    // Single source of truth for the supported import sources. The final list comes from research and may
    // shift slightly — edit here. Ids match the Format field the client sends to the import endpoint.
    private static readonly ImportFormat[] Formats =
    [
        new("generic-csv", "Generic (Chrome, Edge, Brave)", [".csv"], "csv"),
        new("firefox-csv", "Mozilla Firefox", [".csv"], "csv"),
        new("safari-csv", "Safari (iCloud Keychain)", [".csv"], "csv"),
        new("bitwarden-json", "Bitwarden (JSON)", [".json"], "json"),
        new("bitwarden-csv", "Bitwarden (CSV)", [".csv"], "csv"),
        new("lastpass-csv", "LastPass", [".csv"], "csv"),
        new("1password-csv", "1Password (CSV)", [".csv"], "csv"),
        new("1password-1pux", "1Password (1PUX)", [".1pux"], "zip"),
        new("dashlane-zip", "Dashlane (ZIP)", [".zip"], "zip"),
        new("dashlane-csv", "Dashlane (CSV)", [".csv"], "csv"),
        new("keepass-xml", "KeePass (XML)", [".xml"], "xml"),
        new("nordpass-csv", "NordPass", [".csv"], "csv"),
        new("keeper-json", "Keeper", [".json"], "json"),
        new("roboform-csv", "RoboForm", [".csv"], "csv"),
        new("protonpass-json", "Proton Pass", [".json", ".zip"], "json"),
        new("palladin-csv", "Palladin (CSV)", [".csv"], "csv"),
        new("palladin-json", "Palladin (JSON)", [".json"], "json"),
    ];

    private static readonly ListImportFormatsResponse Response = new(Formats);

    public override void Configure()
    {
        Get("api/vaults/import/formats");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.VaultManage);
        this.RequireEmailVerified();
        Summary(summary =>
        {
            summary.Summary = "List supported bulk-import formats";
            summary.Description = "Returns the catalog of password-manager and browser export formats the client can parse before calling the import endpoint. Each entry carries the format id (sent back as the import Format), a display name, the file extensions and the underlying file type.";
        });
        Tags("Vault/Entries");
    }

    public override Task HandleAsync(CancellationToken ct) => Send.OkAsync(Response, ct);
}
