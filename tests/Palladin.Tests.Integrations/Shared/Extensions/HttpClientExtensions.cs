using Palladin.Core.Api;

namespace Palladin.Tests.Integrations.Shared.Extensions;

public static class HttpClientExtensions
{
    public static void AddUserIdHeader(this HttpClient httpClient, string userId)
    {
        if (httpClient.DefaultRequestHeaders.Contains(Headers.User))
        {
            httpClient.DefaultRequestHeaders.Remove(Headers.User);
        }

        httpClient.DefaultRequestHeaders.Add(Headers.User, userId);
    }
}
