using System.Text.Json;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Core.Guid;
using Palladin.Module.Agents.Domain;
using Palladin.Module.Agents.Infrastructure.AgentAuth;
using Palladin.Module.Agents.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Palladin.Core.Security;

namespace Palladin.Module.Agents.Features;

[PublicAPI]
public sealed record SubmitFormDiscoveryMapRequest(
    string Domain,
    string LoginUrl,
    string Provider,
    string Fingerprint,
    JsonElement Map);

[PublicAPI]
public sealed record SubmitFormDiscoveryMapResponse(Guid MapId, int MapVersion, string Status, Instant CreatedAt);

[UsedImplicitly]
internal sealed class SubmitFormDiscoveryMapValidator : Validator<SubmitFormDiscoveryMapRequest>
{
    public SubmitFormDiscoveryMapValidator()
    {
        RuleFor(x => x.Domain).NotEmpty().MaximumLength(253).Matches("^[a-z0-9.-]+$");
        RuleFor(x => x.LoginUrl).NotEmpty().MaximumLength(2048).Must(x => Uri.TryCreate(x, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps);
        RuleFor(x => x.Provider).NotEmpty().MaximumLength(64).Matches("^[a-z0-9-]+$");
        RuleFor(x => x.Fingerprint).Matches("^[a-fA-F0-9]{64}$");
        RuleFor(x => x.Map).Must(x => x.ValueKind == JsonValueKind.Object).Must(x => x.GetRawText().Length <= 65536);
    }
}

internal sealed class SubmitFormDiscoveryMapEndpoint(
    AgentsDomainReadContext readContext,
    AgentsDomainWriteContext writeContext,
    IGuidProvider guidProvider,
    IClock clock) : Endpoint<SubmitFormDiscoveryMapRequest, SubmitFormDiscoveryMapResponse>
{
    public override void Configure()
    {
        Post("api/agent/form-discovery-maps");
        AuthSchemes(AgentAuthenticationOptions.SchemeName);
        Tags("Agents/DiscoveryMaps");
    }

    public override async Task HandleAsync(SubmitFormDiscoveryMapRequest req, CancellationToken ct)
    {
        var agentId = User.GetAgentId();
        if (agentId is null) { await Send.UnauthorizedAsync(ct); return; }
        var agent = await readContext.Agents.Where(x => x.Id == agentId).Select(x => new { x.OrganizationId }).FirstOrDefaultAsync(ct);
        if (agent is null) { await Send.UnauthorizedAsync(ct); return; }
        if (!MapSafety.IsSafe(req.Map, req.Domain)) { AddError(r => r.Map, "The map contains unsupported or unsafe fields."); await Send.ErrorsAsync(400, ct); return; }

        var domain = req.Domain.ToLowerInvariant();
        var now = clock.GetCurrentInstant();
        await using var transaction = await writeContext.BeginTransactionAsync(ct);
        var mapVersion = await writeContext.LockAndLoadNextFormDiscoveryMapVersionAsync(
            agent.OrganizationId,
            domain,
            req.Provider,
            ct);
        var map = FormDiscoveryMap.CreateCandidate(guidProvider.Generate(), agent.OrganizationId, agentId.Value,
            domain, req.LoginUrl, req.Provider, req.Fingerprint.ToLowerInvariant(), req.Map.GetRawText(), mapVersion, now);
        writeContext.Add(map);
        await writeContext.CommitAsync(transaction, ct);
        await Send.OkAsync(new SubmitFormDiscoveryMapResponse(map.Id, map.MapVersion, "candidate", now), ct);
    }
}

[PublicAPI]
public sealed record GetFormDiscoveryMapResponse(Guid MapId, int MapVersion, string Domain, string LoginUrl, string Provider, string Fingerprint, JsonElement Map, Instant UpdatedAt);

internal sealed class GetFormDiscoveryMapEndpoint(AgentsDomainReadContext readContext) : EndpointWithoutRequest<GetFormDiscoveryMapResponse>
{
    public override void Configure()
    {
        Get("api/agent/form-discovery-maps/{domain}");
        AuthSchemes(AgentAuthenticationOptions.SchemeName);
        Tags("Agents/DiscoveryMaps");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var agentId = User.GetAgentId();
        if (agentId is null) { await Send.UnauthorizedAsync(ct); return; }
        var domain = (Route<string>("domain") ?? string.Empty).Trim().ToLowerInvariant();
        var agent = await readContext.Agents.Where(x => x.Id == agentId).Select(x => new { x.OrganizationId }).FirstOrDefaultAsync(ct);
        if (agent is null) { await Send.UnauthorizedAsync(ct); return; }
        var map = await readContext.FormDiscoveryMaps.Where(x => x.OrganizationId == agent.OrganizationId && x.Domain == domain && x.Status == FormDiscoveryMapStatus.Verified)
            .OrderByDescending(x => x.UpdatedAt).Select(x => new { x.Id, x.MapVersion, x.Domain, x.LoginUrl, x.Provider, x.Fingerprint, x.DefinitionJson, x.UpdatedAt }).FirstOrDefaultAsync(ct);
        if (map is null) { await Send.NotFoundAsync(ct); return; }
        using var document = JsonDocument.Parse(map.DefinitionJson);
        await Send.OkAsync(new GetFormDiscoveryMapResponse(map.Id, map.MapVersion, map.Domain, map.LoginUrl, map.Provider, map.Fingerprint, document.RootElement.Clone(), map.UpdatedAt), ct);
    }
}

internal static class MapSafety
{
    private static readonly HashSet<string> Forbidden = new(StringComparer.OrdinalIgnoreCase) { "javascript", "script", "secret", "value", "cookieValue", "token" };
    private static readonly HashSet<string> Controls = new(StringComparer.Ordinal) { "username", "password", "text", "email", "tel", "otp" };
    internal static bool IsSafe(JsonElement root, string _)
    {
        if (!root.TryGetProperty("version", out var version) || !VersionOne(version)) return false;
        if (!root.TryGetProperty("form", out var form) || !ValidForm(form)) return false;
        if (root.TryGetProperty("cookieOverlays", out var overlays) && !ValidOverlays(overlays)) return false;
        return Walk(root, 0);
    }
    private static bool ValidForm(JsonElement form)
    {
        if (form.ValueKind != JsonValueKind.Object || !Only(form, "version", "steps")
            || !form.TryGetProperty("version", out var version) || !VersionOne(version)
            || !form.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array
            || steps.GetArrayLength() is < 1 or > 8) return false;
        foreach (var step in steps.EnumerateArray())
        {
            if (step.ValueKind != JsonValueKind.Object || !Only(step, "fields", "submit", "waitFor")
                || !step.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Array
                || fields.GetArrayLength() is < 1 or > 16 || !step.TryGetProperty("submit", out var submit)
                || submit.ValueKind != JsonValueKind.Object || !Only(submit, "action", "selector")
                || !Selector(submit, "selector") || !submit.TryGetProperty("action", out var action)
                || action.ValueKind != JsonValueKind.String || action.GetString() is not ("click" or "press-enter")) return false;
            foreach (var field in fields.EnumerateArray())
                if (field.ValueKind != JsonValueKind.Object || !Only(field, "entryFieldId", "selector", "control")
                    || !Selector(field, "selector") || !field.TryGetProperty("entryFieldId", out var id)
                    || id.ValueKind != JsonValueKind.String || id.GetString()!.Length is < 1 or > 128
                    || !field.TryGetProperty("control", out var control) || control.ValueKind != JsonValueKind.String
                    || !Controls.Contains(control.GetString()!)) return false;
            if (step.TryGetProperty("waitFor", out var wait) && (wait.ValueKind != JsonValueKind.Object
                || !Only(wait, "selector", "timeoutMs") || !Selector(wait, "selector"))) return false;
        }
        return true;
    }
    private static bool ValidOverlays(JsonElement overlays)
    {
        if (overlays.ValueKind != JsonValueKind.Array || overlays.GetArrayLength() > 4) return false;
        foreach (var overlay in overlays.EnumerateArray())
        {
            if (overlay.ValueKind != JsonValueKind.Object || !Only(overlay, "selectors", "dismiss", "disappears", "frame")
                || !overlay.TryGetProperty("selectors", out var selectors) || selectors.ValueKind != JsonValueKind.Array
                || selectors.GetArrayLength() is < 1 or > 8 || !selectors.EnumerateArray().All(x => x.ValueKind == JsonValueKind.String && SelectorText(x.GetString()))
                || !overlay.TryGetProperty("dismiss", out var dismiss) || dismiss.ValueKind != JsonValueKind.Object
                || !Only(dismiss, "selector", "action") || !Selector(dismiss, "selector")
                || !dismiss.TryGetProperty("action", out var action) || action.GetString() != "click") return false;
        }
        return true;
    }
    private static bool Selector(JsonElement value, string name) => value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String && SelectorText(item.GetString());
    private static bool SelectorText(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 1024 && !value.Contains('\0');
    private static bool VersionOne(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number == 1;
    private static bool Only(JsonElement value, params string[] keys) => value.EnumerateObject().All(x => keys.Contains(x.Name, StringComparer.Ordinal));
    private static bool Walk(JsonElement value, int depth)
    {
        if (depth > 12) return false;
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject())
            { if (Forbidden.Contains(property.Name)) return false; if (!Walk(property.Value, depth + 1)) return false; }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) if (!Walk(item, depth + 1)) return false;
        else if (value.ValueKind == JsonValueKind.String && value.GetString()?.Length > 4096) return false;
        return true;
    }
}

internal sealed class VerifyFormDiscoveryMapEndpoint(
    AgentsDomainWriteContext writeContext,
    IClock clock) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Put("api/form-discovery-maps/{mapId:guid}/verify");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.AgentManage);
        this.RequireEmailVerified();
        Tags("Agents/DiscoveryMaps");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId();
        var mapId = Route<Guid>("mapId");
        if (organizationId is null) { await Send.UnauthorizedAsync(ct); return; }
        var map = await writeContext.FormDiscoveryMaps.FirstOrDefaultAsync(x => x.Id == mapId && x.OrganizationId == organizationId, ct);
        if (map is null) { await Send.NotFoundAsync(ct); return; }
        map.MarkVerified(clock.GetCurrentInstant());
        await writeContext.CommitAsync(ct);
        await Send.NoContentAsync(ct);
    }
}
