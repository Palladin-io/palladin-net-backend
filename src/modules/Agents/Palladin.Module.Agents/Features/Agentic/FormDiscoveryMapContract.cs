using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Palladin.Module.Agents.Domain;
using Palladin.Module.Agents.Infrastructure.DiscoveryMaps;

namespace Palladin.Module.Agents.Features;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[PublicAPI]
public sealed record FormDiscoveryMapDefinition(
    int Version,
    FormDiscoveryFormDefinition? Form,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<FormDiscoveryCookieOverlay>? CookieOverlays = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[PublicAPI]
public sealed record FormDiscoveryFormDefinition(
    int Version,
    IReadOnlyList<FormDiscoveryStepDefinition>? Steps);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[PublicAPI]
public sealed record FormDiscoveryStepDefinition(
    IReadOnlyList<FormDiscoveryFieldDefinition>? Fields,
    FormDiscoverySubmitDefinition? Submit,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    FormDiscoveryWaitDefinition? WaitFor = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[PublicAPI]
public sealed record FormDiscoveryFieldDefinition(
    string? EntryFieldId,
    string? Selector,
    string? Control);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[PublicAPI]
public sealed record FormDiscoverySubmitDefinition(
    string? Action,
    string? Selector);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[PublicAPI]
public sealed record FormDiscoveryWaitDefinition(
    string? Selector,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? TimeoutMs = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[PublicAPI]
public sealed record FormDiscoveryCookieOverlay(
    IReadOnlyList<string>? Selectors,
    FormDiscoveryOverlayDismiss? Dismiss,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Disappears = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Frame = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[PublicAPI]
public sealed record FormDiscoveryOverlayDismiss(
    string? Selector,
    string? Action);

internal sealed record ValidatedFormDiscoveryMap(
    string Domain,
    string LoginUrl,
    string Provider,
    FormDiscoveryMapDefinition Definition,
    string DefinitionJson);

internal sealed record PublishableFormDiscoveryMap(
    FormDiscoveryMap Map,
    FormDiscoveryMapDefinition Definition);

internal sealed partial class FormDiscoveryMapContract
{
    private readonly FormDiscoveryMapOptions _options;
    private readonly JsonSerializerOptions _serializerOptions;

    public FormDiscoveryMapContract(IOptions<FormDiscoveryMapOptions> configured)
    {
        _options = configured.Value;
        _serializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            MaxDepth = _options.MaximumJsonDepth,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
    }

    internal bool TryValidate(
        string? domainValue,
        string? loginUrlValue,
        string? providerValue,
        FormDiscoveryMapDefinition? definition,
        out ValidatedFormDiscoveryMap validated)
    {
        validated = null!;
        if (!TryNormalizeDomain(domainValue, out var domain)
            || !TryNormalizeProvider(providerValue, out var provider)
            || !TryNormalizeLoginUrl(loginUrlValue, domain, out var loginUrl)
            || definition is null
            || !ValidDefinition(definition))
        {
            return false;
        }

        var normalizedDefinition = definition.CookieOverlays is { Count: 0 }
            ? definition with { CookieOverlays = null }
            : definition;
        var definitionJson = JsonSerializer.Serialize(normalizedDefinition, _serializerOptions);
        if (Encoding.UTF8.GetByteCount(definitionJson) > _options.MaximumDefinitionBytes)
        {
            return false;
        }

        validated = new ValidatedFormDiscoveryMap(
            domain,
            loginUrl,
            provider,
            normalizedDefinition,
            definitionJson);
        return true;
    }

    internal bool TryDeserializeDefinition(string definitionJson, out FormDiscoveryMapDefinition definition)
    {
        definition = null!;
        if (Encoding.UTF8.GetByteCount(definitionJson) > _options.MaximumDefinitionBytes)
        {
            return false;
        }

        try
        {
            definition = JsonSerializer.Deserialize<FormDiscoveryMapDefinition>(
                definitionJson,
                _serializerOptions)!;
            return definition is not null && ValidDefinition(definition);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal bool FingerprintMatches(ValidatedFormDiscoveryMap map, string? fingerprint) =>
        FingerprintPattern().IsMatch(fingerprint ?? string.Empty)
        && string.Equals(
            ComputeFingerprint(map),
            fingerprint,
            StringComparison.OrdinalIgnoreCase);

    internal string ComputeFingerprint(ValidatedFormDiscoveryMap map)
    {
        var payload = new FormDiscoveryMapFingerprint(
            map.Domain,
            new Uri(map.LoginUrl).AbsolutePath,
            map.Provider,
            map.Definition);
        return Convert.ToHexStringLower(
            SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(payload, _serializerOptions)));
    }

    internal async Task<PublishableFormDiscoveryMap?> FindVerifiedAsync(
        IQueryable<FormDiscoveryMap> maps,
        string domain,
        string provider,
        CancellationToken cancellationToken)
    {
        var candidates = maps
            .Where(map => map.Domain == domain
                && map.Provider == provider
                && map.Status == FormDiscoveryMapStatus.Verified)
            .OrderByDescending(map => map.MapVersion)
            .ThenByDescending(map => map.UpdatedAt)
            .Take(_options.MaximumLookupRevisions);

        await foreach (var map in candidates.AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            if (TryPublish(map, out var publication))
            {
                return publication;
            }
        }

        return null;
    }

    internal bool TryNormalizeDomain(string? value, out string domain)
    {
        domain = string.Empty;
        var candidate = value?.Trim();
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        try
        {
            domain = new IdnMapping().GetAscii(candidate).ToLowerInvariant();
            return DomainPattern().IsMatch(domain);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    internal bool TryNormalizeProvider(string? value, out string provider)
    {
        provider = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return provider.Length <= _options.MaximumProviderLength
            && ProviderPattern().IsMatch(provider);
    }

    private bool TryNormalizeLoginUrl(string? value, string domain, out string loginUrl)
    {
        loginUrl = string.Empty;
        if (string.IsNullOrWhiteSpace(value)
            || value != value.Trim()
            || Encoding.UTF8.GetByteCount(value) > _options.MaximumLoginUrlBytes
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.IdnHost, domain, StringComparison.Ordinal)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || !uri.IsDefaultPort)
        {
            return false;
        }

        loginUrl = uri.AbsoluteUri;
        return Encoding.UTF8.GetByteCount(loginUrl) <= _options.MaximumLoginUrlBytes;
    }

    private bool TryPublish(FormDiscoveryMap map, out PublishableFormDiscoveryMap publication)
    {
        publication = null!;
        if (map.MapVersion < 1
            || !FingerprintPattern().IsMatch(map.Fingerprint)
            || !TryDeserializeDefinition(map.DefinitionJson, out var definition)
            || !TryValidate(map.Domain, map.LoginUrl, map.Provider, definition, out var validated)
            || map.Domain != validated.Domain
            || map.LoginUrl != validated.LoginUrl
            || map.Provider != validated.Provider
            || !FingerprintMatches(validated, map.Fingerprint))
        {
            return false;
        }

        publication = new PublishableFormDiscoveryMap(map, definition);
        return true;
    }

    private bool ValidDefinition(FormDiscoveryMapDefinition definition) =>
        definition.Version == 1
        && definition.Form is not null
        && ValidForm(definition.Form)
        && (definition.CookieOverlays is null || ValidOverlays(definition.CookieOverlays));

    private bool ValidForm(FormDiscoveryFormDefinition form)
    {
        if (form.Version != 1
            || form.Steps is null
            || form.Steps.Count is < 1
            || form.Steps.Count > _options.MaximumSteps)
        {
            return false;
        }

        var fieldCount = 0;
        for (var stepIndex = 0; stepIndex < form.Steps.Count; stepIndex++)
        {
            if (!ValidStep(form.Steps[stepIndex], stepIndex, form.Steps.Count, ref fieldCount))
            {
                return false;
            }
        }

        return true;
    }

    private bool ValidStep(
        FormDiscoveryStepDefinition? step,
        int stepIndex,
        int stepCount,
        ref int fieldCount)
    {
        if (step?.Fields is null
            || step.Fields.Count < 1
            || step.Submit is null
            || !SelectorText(step.Submit.Selector)
            || step.Submit.Action is not ("click" or "press-enter"))
        {
            return false;
        }

        var fieldIds = new HashSet<string>(StringComparer.Ordinal);
        var fieldSelectors = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in step.Fields)
        {
            fieldCount++;
            if (fieldCount > _options.MaximumFields
                || field is null
                || !SelectorText(field.Selector)
                || field.EntryFieldId is null
                || field.EntryFieldId.Length > _options.MaximumFieldIdLength
                || !FieldIdPattern().IsMatch(field.EntryFieldId)
                || !fieldIds.Add(field.EntryFieldId)
                || field.Control is null
                || !ValidControl(field.Control))
            {
                return false;
            }

            fieldSelectors.Add(field.Selector!);
        }

        if (step.Submit.Action == "press-enter"
            && !fieldSelectors.Contains(step.Submit.Selector!))
        {
            return false;
        }

        if (step.WaitFor is null)
        {
            return stepIndex == stepCount - 1;
        }

        return SelectorText(step.WaitFor.Selector)
            && (step.WaitFor.TimeoutMs is null
                || step.WaitFor.TimeoutMs >= _options.MinimumWaitTimeoutMilliseconds
                && step.WaitFor.TimeoutMs <= _options.MaximumWaitTimeoutMilliseconds);
    }

    private bool ValidOverlays(IReadOnlyList<FormDiscoveryCookieOverlay> overlays)
    {
        if (overlays.Count > _options.MaximumCookieOverlays)
        {
            return false;
        }

        foreach (var overlay in overlays)
        {
            if (overlay?.Selectors is null
                || overlay.Selectors.Count is < 1
                || overlay.Selectors.Count > _options.MaximumSelectorsPerOverlay
                || overlay.Selectors.Any(selector => !SelectorText(selector))
                || overlay.Dismiss is null
                || !SelectorText(overlay.Dismiss.Selector)
                || overlay.Dismiss.Action != "click"
                || overlay.Disappears is not null && !SelectorText(overlay.Disappears)
                || overlay.Frame is not null and not ("top" or "same-origin"))
            {
                return false;
            }
        }

        return true;
    }

    private bool SelectorText(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && Encoding.UTF8.GetByteCount(value) <= _options.MaximumSelectorBytes
        && value == value.Trim()
        && !value.Contains('\0');

    private static bool ValidControl(string control) =>
        control is "username" or "password" or "text" or "email" or "tel" or "otp";

    private sealed record FormDiscoveryMapFingerprint(
        string Domain,
        string LoginUrl,
        string Provider,
        FormDiscoveryMapDefinition Map);

    [GeneratedRegex("^(?=.{1,253}$)(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\\.)+[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])$", RegexOptions.CultureInvariant)]
    private static partial Regex DomainPattern();

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex ProviderPattern();

    [GeneratedRegex("^[A-Za-z0-9._:-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex FieldIdPattern();

    [GeneratedRegex("^[a-f0-9]{64}$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex FingerprintPattern();
}
