using Polly;
using Polly.Extensions.Http;

namespace Palladin.Core.Http;

public static class RetryPolicies
{
    public static IAsyncPolicy<HttpResponseMessage> GetDefault()
    {
        return HttpPolicyExtensions
            .HandleTransientHttpError()
            .WaitAndRetryAsync(6, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2,
                retryAttempt)));
    }
}
