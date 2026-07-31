using System.Text;
using Palladin.Core.Ai.SemanticKernel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Exporter;
using OpenTelemetry.Trace;

namespace Palladin.Core.Ai.EngineeringPlatform.Langfuse;

internal static class LangfuseModule
{
    internal static IServiceCollection AddLangfuseModule(this IServiceCollection services, IConfiguration configuration)
    {
        var langfuseSection = configuration.GetSection(LangfuseOptions.Position);
        var langfuseOptions = new LangfuseOptions();
        langfuseSection.Bind(langfuseOptions);

        var langfuseBasicCredentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{langfuseOptions.Pk}:{langfuseOptions.Sk}"));

        return services
            .Configure<LangfuseOptions>(langfuseSection)
            .AddHttpClient<IEngineeringPlatformClient, LangfuseClient>(cfg =>
            {
                cfg.BaseAddress = new Uri(langfuseOptions.ApiUrl);
                cfg.DefaultRequestHeaders.Add("Authorization", $"Basic {langfuseBasicCredentials}");
            })
            .AddStandardResilienceHandler()
            .Services
            .AddOpenTelemetry()
            .WithTracing(cfg =>
            {
                cfg.SetSampler<AlwaysOnSampler>();
                cfg.AddSource(SemanticKernelTracing.Source);
                cfg.AddConsoleExporter();
                cfg.AddOtlpExporter((exporterOptions) =>
                {
                    exporterOptions.Endpoint = new Uri(langfuseOptions.OTelUrl);
                    exporterOptions.Headers = $"Authorization=Basic {langfuseBasicCredentials}";
                    exporterOptions.Protocol = OtlpExportProtocol.HttpProtobuf;
                });
            }).Services;
    }
}
