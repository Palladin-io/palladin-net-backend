using System.Collections.Concurrent;
using Palladin.Module.Audit.Infrastructure.Exports;
using Palladin.Module.Vault.Infrastructure.Assets;

namespace Palladin.Tests.Integrations.Shared.Mocks;

internal sealed class InMemoryCdnService : IAuditExportStorage, IEncryptedPresentationAssetStorage, ILegacyPresentationAssetStorage
{
    private readonly ConcurrentDictionary<string, byte[]> _objects = new(StringComparer.Ordinal);

    internal IReadOnlyDictionary<string, byte[]> Objects => _objects;
    internal Func<string, CancellationToken, Task>? AfterCopyAsync { get; set; }

    public Task<string> CreateDownloadUrlAsync(string documentName, CancellationToken cancellationToken) =>
        Task.FromResult($"https://private.test/download/{Uri.EscapeDataString(documentName)}?signature=test");

    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken) =>
        Task.FromResult(_objects.ContainsKey(key));

    public async Task PutAsync(
        Stream inputStream,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await inputStream.CopyToAsync(buffer, cancellationToken);
        _objects[destinationPath] = buffer.ToArray();
        if (AfterCopyAsync is { } afterCopy)
        {
            await afterCopy(destinationPath, cancellationToken);
        }
    }

    public Task UploadAsync(Stream content, string key, CancellationToken cancellationToken) =>
        PutAsync(content, key, cancellationToken);

    public Task DeleteAllVersionsAsync(string folderPath, CancellationToken cancellationToken) =>
        DeletePrefixAsync(folderPath);

    public Task<bool> HasVersionsAsync(string prefix, CancellationToken cancellationToken) =>
        Task.FromResult(_objects.Keys.Any(x => x.StartsWith(prefix, StringComparison.Ordinal)));

    private Task DeletePrefixAsync(string prefix)
    {
        foreach (var key in _objects.Keys.Where(x => x.StartsWith(prefix, StringComparison.Ordinal)))
        {
            _objects.TryRemove(key, out _);
        }

        return Task.CompletedTask;
    }
}
