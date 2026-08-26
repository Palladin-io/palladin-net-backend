using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Encodings.Web;
using Fluid;
using JetBrains.Annotations;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Palladin.Module.Notification.Infrastructure.Email.Templating;

// Fluid (Liquid for .NET) renders the templates. Chosen over Razor because it auto HTML-encodes
// every output value by default (mandatory escaping of interpolated data) and runs sandboxed —
// templates cannot execute arbitrary code, unlike Razor which compiles C# at runtime.
[UsedImplicitly]
internal sealed class FluidEmailTemplateRenderer : IEmailTemplateRenderer
{
    private static readonly FluidParser Parser = new();
    private static readonly HashSet<string> SupportedLanguages = ["en", "pl"];

    // Templates are immutable embedded resources — parse each once and reuse the compiled template.
    private static readonly ConcurrentDictionary<string, IFluidTemplate> ParsedTemplates = new();

    private readonly EmailBrandingOptions _branding;
    private readonly IClock _clock;
    private readonly Assembly _assembly;
    private readonly string _resourcePrefix;
    private readonly TemplateOptions _templateOptions;

    public FluidEmailTemplateRenderer(IOptions<EmailBrandingOptions> branding, IClock clock)
    {
        _branding = branding.Value;
        _clock = clock;
        _assembly = typeof(FluidEmailTemplateRenderer).Assembly;
        _resourcePrefix = typeof(FluidEmailTemplateRenderer).Namespace + ".Templates.";
        _templateOptions = new TemplateOptions
        {
            FileProvider = new EmbeddedTemplateFileProvider(_assembly, _resourcePrefix),
        };
    }

    public RenderedEmail Render(string templateName, string language, IReadOnlyDictionary<string, object?> model)
    {
        var baseName = ResolveBaseName(templateName);
        var lang = ResolveLanguage(language);
        var context = BuildContext(model);

        var subject = Render($"{baseName}.{lang}.subject", context, NullEncoder.Default);
        var html = Render($"{baseName}.{lang}.html", context, HtmlEncoder.Default);
        var text = Render($"{baseName}.{lang}.text", context, NullEncoder.Default);

        return new RenderedEmail(SanitizeSubject(subject), html, text);
    }

    // Names map 1:1 to .liquid resource base names (see EmailTemplates constants). The format guard
    // keeps a malformed name from probing arbitrary manifest resources; unknown-but-well-formed names
    // surface as EmailTemplateNotFoundException at resource lookup.
    private static string ResolveBaseName(string templateName)
    {
        var name = templateName.Trim().ToLowerInvariant();
        if (name.Length == 0 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
        {
            throw new EmailTemplateNotFoundException(templateName);
        }

        return name;
    }

    private static string ResolveLanguage(string language)
    {
        var code = language.Trim().ToLowerInvariant();
        if (code.Length > 2)
        {
            code = code[..2];
        }

        return SupportedLanguages.Contains(code) ? code : "en";
    }

    private TemplateContext BuildContext(IReadOnlyDictionary<string, object?> model)
    {
        var context = new TemplateContext(_templateOptions);
        context.SetValue("appName", _branding.AppName);
        context.SetValue("baseUrl", _branding.BaseUrl);
        context.SetValue("supportEmail", _branding.SupportEmail);
        context.SetValue("mobileAppUrl", _branding.MobileAppUrl);
        context.SetValue("browserExtensionUrl", _branding.BrowserExtensionUrl);
        context.SetValue("year", _clock.GetCurrentInstant().InUtc().Year);
        if (!string.IsNullOrEmpty(_branding.LogoUrl))
        {
            context.SetValue("logoUrl", _branding.LogoUrl);
        }

        foreach (var (key, value) in model)
        {
            context.SetValue(key, value);
        }

        return context;
    }

    private string Render(string templateName, TemplateContext context, TextEncoder encoder)
    {
        var resourceName = $"{_resourcePrefix}{templateName}.liquid";
        var template = ParsedTemplates.GetOrAdd(resourceName, ParseTemplate);
        return template.Render(context, encoder);
    }

    private IFluidTemplate ParseTemplate(string resourceName)
    {
        var source = ReadResource(resourceName);
        if (!Parser.TryParse(source, out var template, out var error))
        {
            throw new InvalidOperationException($"Failed to parse email template '{resourceName}': {error}");
        }

        return template;
    }

    private string ReadResource(string resourceName)
    {
        using var stream = _assembly.GetManifestResourceStream(resourceName)
                           ?? throw new EmailTemplateNotFoundException(resourceName);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // Subjects are plain-text mail headers — collapse any line breaks so an interpolated value can
    // never split the header.
    private static string SanitizeSubject(string subject) => subject.ReplaceLineEndings(" ").Trim();
}
