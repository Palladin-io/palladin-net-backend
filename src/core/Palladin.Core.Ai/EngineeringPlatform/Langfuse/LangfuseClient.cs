using JetBrains.Annotations;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Palladin.Core.Ai.EngineeringPlatform.Langfuse;

[UsedImplicitly]
internal sealed partial class LangfuseClient(
    IOptions<LangfuseOptions> langfuseOptions,
    IMemoryCache cache,
    HttpClient httpClient) : IEngineeringPlatformClient;
