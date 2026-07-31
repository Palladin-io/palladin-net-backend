namespace Palladin.Core.Transport;

public interface ITransportContext
{
    Guid? CorrelationId { get; }
    string? SessionId { get; }
    string? Platform { get; }

    public IDictionary<string, string> Headers { get; }

    void Fill(IDictionary<string, string> headers);
    void CopyTo(ITransportContext newTransportContext);
}

internal sealed class TransportContext : ITransportContext
{
    public Guid? CorrelationId =>
        Headers.TryGetValue(CustomHeaders.CorrelationIdHeaderName, out var value) ? new Guid(value) : null;

    public string? SessionId =>
        Headers.TryGetValue(CustomHeaders.SessionIdHeaderName, out var value) ? value : null;

    public string? Platform =>
        Headers.TryGetValue(CustomHeaders.PlatformHeaderName, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    public IDictionary<string, string> Headers { get; private set; } = new Dictionary<string, string>();

    public void Fill(IDictionary<string, string> headers)
    {
        Headers = headers.Where(x => x.Key.StartsWith(CustomHeaders.Prefix))
            .ToDictionary(x => x.Key, x => x.Value);
    }

    public void CopyTo(ITransportContext newTransportContext)
    {
        newTransportContext.Headers.Clear();

        foreach (var header in Headers)
        {
            newTransportContext.Headers.Add(header.Key, header.Value);
        }
    }
}
