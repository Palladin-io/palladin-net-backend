using System.Reflection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace Palladin.Module.Notification.Infrastructure.Email.Templating;

// Serves the embedded .liquid partials to Fluid's {% include %} tag. Templates ship as assembly
// resources so the deployed app is self-contained (no content files to copy).
internal sealed class EmbeddedTemplateFileProvider(Assembly assembly, string resourcePrefix) : IFileProvider
{
    public IFileInfo GetFileInfo(string subpath)
    {
        var resourceName = resourcePrefix + subpath.TrimStart('/', '.').Replace('/', '.');
        return assembly.GetManifestResourceInfo(resourceName) is null
            ? new NotFoundFileInfo(subpath)
            : new EmbeddedResourceFile(assembly, resourceName, subpath);
    }

    public IDirectoryContents GetDirectoryContents(string subpath) => NotFoundDirectoryContents.Singleton;

    public IChangeToken Watch(string filter) => NullChangeToken.Singleton;

    private sealed class EmbeddedResourceFile(Assembly assembly, string resourceName, string name) : IFileInfo
    {
        public bool Exists => true;
        public bool IsDirectory => false;
        public long Length => -1;
        public string? PhysicalPath => null;
        public string Name => name;
        public DateTimeOffset LastModified => DateTimeOffset.MinValue;

        public Stream CreateReadStream() =>
            assembly.GetManifestResourceStream(resourceName)
            ?? throw new EmailTemplateNotFoundException(resourceName);
    }
}
