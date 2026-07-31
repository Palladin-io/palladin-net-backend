using Amazon;
using Amazon.Runtime;

namespace Palladin.Module.Notification.Infrastructure.Email;

// Single credential/endpoint policy for every AWS SDK client the email channel builds (SES, SQS):
// explicit keys → BasicAWSCredentials; ServiceUrl set (LocalStack) → dummy creds + endpoint;
// otherwise the ambient IAM-role chain against the configured region.
internal static class AwsClientFactory
{
    public static TClient Build<TClient, TConfig>(
        SesOptions options,
        Func<TConfig> configFactory,
        Func<AWSCredentials, TConfig, TClient> withCredentials,
        Func<TConfig, TClient> ambient)
        where TConfig : ClientConfig
    {
        var config = configFactory();
        if (!string.IsNullOrWhiteSpace(options.ServiceUrl))
        {
            config.ServiceURL = options.ServiceUrl;
        }
        else
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region);
        }

        var credentials = ResolveCredentials(options);
        return credentials is not null ? withCredentials(credentials, config) : ambient(config);
    }

    private static AWSCredentials? ResolveCredentials(SesOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.AccessKey) && !string.IsNullOrWhiteSpace(options.SecretKey))
        {
            return new BasicAWSCredentials(options.AccessKey, options.SecretKey);
        }

        // LocalStack accepts any credentials; native AWS uses the ambient IAM-role chain.
        return string.IsNullOrWhiteSpace(options.ServiceUrl) ? null : new BasicAWSCredentials("test", "test");
    }
}
