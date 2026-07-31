using FastEndpoints;
using JetBrains.Annotations;
using System.Reflection;

namespace Palladin.Api.Features.Health;

[PublicAPI]
internal sealed record GetHealthResponse(string Status, string SourceCode, string License);

[PublicAPI]
internal sealed class GetHealthEndpoint : EndpointWithoutRequest<GetHealthResponse>
{
    private const string RepositoryUrl = "https://github.com/Palladin-io/palladin-net-backend";
    private static readonly string SourceCodeUrl = ResolveSourceCodeUrl();

    public override void Configure()
    {
        Get("api/health");
        AllowAnonymous();
        Summary(summary =>
        {
            summary.Summary = "Health check";
            summary.Description = "Returns application health status and the exact corresponding source revision";
        });
        Description(d => d.WithTags("Health"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        await Send.OkAsync(new GetHealthResponse("Healthy", SourceCodeUrl, "AGPL-3.0-only"), ct);
    }

    private static string ResolveSourceCodeUrl()
    {
        var informationalVersion = typeof(GetHealthEndpoint).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        var revision = informationalVersion?.Split('+', 2).ElementAtOrDefault(1);

        return revision is { Length: 40 } && revision.All(Uri.IsHexDigit)
            ? $"{RepositoryUrl}/tree/{revision}"
            : RepositoryUrl;
    }
}
