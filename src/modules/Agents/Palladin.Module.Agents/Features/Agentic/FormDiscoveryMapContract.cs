using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Palladin.Module.Agents.Domain;

namespace Palladin.Module.Agents.Features;

internal static partial class FormDiscoveryMapContract
{
    internal const int MaximumDefinitionBytes = 65_536;

    private static readonly HashSet<string> Providers = new(StringComparer.Ordinal)
    {
        "agent-browser",
        "extension",
        "generic",
        "playwright",
    };

    internal static bool TryNormalizeDomain(string? value, out string domain)
    {
        domain = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return DomainPattern().IsMatch(domain);
    }

    internal static bool TryNormalizeProvider(string? value, out string provider)
    {
        provider = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return Providers.Contains(provider);
    }

    internal static bool IsSafe(JsonElement root, string domain, string loginUrl)
    {
        if (!TryNormalizeDomain(domain, out var normalizedDomain)
            || !ValidLoginUrl(loginUrl, normalizedDomain)
            || Encoding.UTF8.GetByteCount(root.GetRawText()) > MaximumDefinitionBytes
            || root.ValueKind != JsonValueKind.Object
            || !Only(root, "version", "form", "cookieOverlays")
            || !root.TryGetProperty("version", out var version)
            || !VersionOne(version)
            || !root.TryGetProperty("form", out var form)
            || !ValidForm(form))
        {
            return false;
        }

        return !root.TryGetProperty("cookieOverlays", out var overlays) || ValidOverlays(overlays);
    }

    internal static bool FingerprintMatches(
        JsonElement root,
        string domain,
        string loginUrl,
        string fingerprint)
    {
        if (!IsSafe(root, domain, loginUrl)
            || !root.TryGetProperty("form", out var form)
            || !Uri.TryCreate(loginUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            writer.WriteStartObject();
            writer.WriteString("domain", domain);
            writer.WriteString("loginUrl", uri.AbsolutePath);
            writer.WritePropertyName("form");
            WriteCanonicalForm(writer, form);
            writer.WriteEndObject();
        }

        var actual = Convert.ToHexStringLower(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
        return string.Equals(actual, fingerprint, StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteCanonicalForm(Utf8JsonWriter writer, JsonElement form)
    {
        writer.WriteStartObject();
        writer.WriteNumber("version", form.GetProperty("version").GetInt32());
        writer.WriteStartArray("steps");
        foreach (var step in form.GetProperty("steps").EnumerateArray())
        {
            writer.WriteStartObject();
            writer.WriteStartArray("fields");
            foreach (var field in step.GetProperty("fields").EnumerateArray())
            {
                writer.WriteStartObject();
                writer.WriteString("entryFieldId", field.GetProperty("entryFieldId").GetString());
                writer.WriteString("selector", field.GetProperty("selector").GetString());
                writer.WriteString("control", field.GetProperty("control").GetString());
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            var submit = step.GetProperty("submit");
            writer.WriteStartObject("submit");
            writer.WriteString("action", submit.GetProperty("action").GetString());
            writer.WriteString("selector", submit.GetProperty("selector").GetString());
            writer.WriteEndObject();
            if (step.TryGetProperty("waitFor", out var waitFor))
            {
                writer.WriteStartObject("waitFor");
                writer.WriteString("selector", waitFor.GetProperty("selector").GetString());
                if (waitFor.TryGetProperty("timeoutMs", out var timeout))
                {
                    writer.WriteNumber("timeoutMs", timeout.GetInt32());
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    internal static async Task<FormDiscoveryMap?> FindVerifiedAsync(
        IQueryable<FormDiscoveryMap> maps,
        Guid organizationId,
        string domain,
        string provider,
        CancellationToken cancellationToken)
    {
        var candidates = maps
            .Where(map => map.Domain == domain
                && map.Provider == provider
                && map.Status == FormDiscoveryMapStatus.Verified
                && ((map.Scope == FormDiscoveryMapScope.Organization && map.OrganizationId == organizationId)
                    || (map.Scope == FormDiscoveryMapScope.System && map.OrganizationId == null)))
            .OrderByDescending(map => map.Scope)
            .ThenByDescending(map => map.MapVersion)
            .ThenByDescending(map => map.UpdatedAt);

        await foreach (var map in candidates.AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            if (IsPublishable(map))
            {
                return map;
            }
        }

        return null;
    }

    private static bool IsPublishable(FormDiscoveryMap map)
    {
        if (map.MapVersion < 1 || !FingerprintPattern().IsMatch(map.Fingerprint))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(map.DefinitionJson, new JsonDocumentOptions
            {
                MaxDepth = 32,
            });
            return FingerprintMatches(
                document.RootElement,
                map.Domain,
                map.LoginUrl,
                map.Fingerprint);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool ValidLoginUrl(string value, string domain)
    {
        if (value.Length > 2_048
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.IdnHost, domain, StringComparison.Ordinal)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        return uri.IsDefaultPort || uri.Port == 443;
    }

    private static bool ValidForm(JsonElement form)
    {
        if (form.ValueKind != JsonValueKind.Object
            || !Only(form, "version", "steps")
            || !form.TryGetProperty("version", out var version)
            || !VersionOne(version)
            || !form.TryGetProperty("steps", out var steps)
            || steps.ValueKind != JsonValueKind.Array
            || steps.GetArrayLength() is < 1 or > 8)
        {
            return false;
        }

        var fieldCount = 0;
        var stepIndex = 0;
        foreach (var step in steps.EnumerateArray())
        {
            if (!ValidStep(step, stepIndex, steps.GetArrayLength(), ref fieldCount))
            {
                return false;
            }

            stepIndex++;
        }

        return true;
    }

    private static bool ValidStep(JsonElement step, int stepIndex, int stepCount, ref int fieldCount)
    {
        if (step.ValueKind != JsonValueKind.Object
            || !Only(step, "fields", "submit", "waitFor")
            || !step.TryGetProperty("fields", out var fields)
            || fields.ValueKind != JsonValueKind.Array
            || fields.GetArrayLength() < 1
            || !step.TryGetProperty("submit", out var submit)
            || submit.ValueKind != JsonValueKind.Object
            || !Only(submit, "action", "selector")
            || !Selector(submit, "selector")
            || !submit.TryGetProperty("action", out var action)
            || action.ValueKind != JsonValueKind.String
            || action.GetString() is not ("click" or "press-enter"))
        {
            return false;
        }

        var fieldIds = new HashSet<string>(StringComparer.Ordinal);
        var fieldSelectors = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in fields.EnumerateArray())
        {
            fieldCount++;
            if (fieldCount > 16
                || field.ValueKind != JsonValueKind.Object
                || !Only(field, "entryFieldId", "selector", "control")
                || !Selector(field, "selector")
                || !field.TryGetProperty("entryFieldId", out var id)
                || id.ValueKind != JsonValueKind.String
                || !fieldIds.Add(id.GetString()!)
                || !field.TryGetProperty("control", out var control)
                || control.ValueKind != JsonValueKind.String
                || !ValidLoginField(id.GetString()!, control.GetString()!))
            {
                return false;
            }

            fieldSelectors.Add(field.GetProperty("selector").GetString()!);
        }

        if (action.GetString() == "press-enter"
            && !fieldSelectors.Contains(submit.GetProperty("selector").GetString()!))
        {
            return false;
        }

        if (!step.TryGetProperty("waitFor", out var wait))
        {
            return stepIndex == stepCount - 1;
        }

        return wait.ValueKind == JsonValueKind.Object
            && Only(wait, "selector", "timeoutMs")
            && Selector(wait, "selector")
            && (!wait.TryGetProperty("timeoutMs", out var timeout)
                || (timeout.ValueKind == JsonValueKind.Number
                    && timeout.TryGetInt32(out var timeoutMs)
                    && timeoutMs is >= 100 and <= 60_000));
    }

    private static bool ValidOverlays(JsonElement overlays)
    {
        if (overlays.ValueKind != JsonValueKind.Array || overlays.GetArrayLength() > 4)
        {
            return false;
        }

        foreach (var overlay in overlays.EnumerateArray())
        {
            if (overlay.ValueKind != JsonValueKind.Object
                || !Only(overlay, "selectors", "dismiss", "disappears", "frame")
                || !overlay.TryGetProperty("selectors", out var selectors)
                || selectors.ValueKind != JsonValueKind.Array
                || selectors.GetArrayLength() is < 1 or > 8
                || !selectors.EnumerateArray().All(selector =>
                    selector.ValueKind == JsonValueKind.String && SelectorText(selector.GetString()))
                || !overlay.TryGetProperty("dismiss", out var dismiss)
                || dismiss.ValueKind != JsonValueKind.Object
                || !Only(dismiss, "selector", "action")
                || !Selector(dismiss, "selector")
                || !dismiss.TryGetProperty("action", out var action)
                || action.ValueKind != JsonValueKind.String
                || action.GetString() != "click")
            {
                return false;
            }

            if (overlay.TryGetProperty("disappears", out var disappears)
                && (disappears.ValueKind != JsonValueKind.String || !SelectorText(disappears.GetString())))
            {
                return false;
            }

            if (overlay.TryGetProperty("frame", out var frame)
                && (frame.ValueKind != JsonValueKind.String || frame.GetString() is not ("top" or "same-origin")))
            {
                return false;
            }
        }

        return true;
    }

    private static bool Selector(JsonElement value, string name) =>
        value.TryGetProperty(name, out var item)
        && item.ValueKind == JsonValueKind.String
        && SelectorText(item.GetString());

    private static bool SelectorText(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 1_024
        && value == value.Trim()
        && !value.Contains('\0');

    private static bool VersionOne(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number)
        && number == 1;

    private static bool ValidLoginField(string fieldId, string control) =>
        (fieldId == "credential.username" && control is "email" or "tel" or "text" or "username")
        || (fieldId == "credential.password" && control == "password");

    private static bool Only(JsonElement value, params string[] keys) =>
        value.EnumerateObject().All(property => keys.Contains(property.Name, StringComparer.Ordinal));

    [GeneratedRegex("^(?=.{1,253}$)(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\\.)+[a-z]{2,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex DomainPattern();

    [GeneratedRegex("^[a-f0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex FingerprintPattern();
}
