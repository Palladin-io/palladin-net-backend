using System.Diagnostics;
using Palladin.Core.Ai.EngineeringPlatform;
using Palladin.Core.Ai.Tracing;
using Microsoft.SemanticKernel.ChatCompletion;
using OpenAI.Chat;
using ChatMessageContent = Microsoft.SemanticKernel.ChatMessageContent;

namespace Palladin.Core.Ai.SemanticKernel;

public static class SemanticKernelTracing
{
    public const string Source = "SemanticKernel";

    internal static RootActivity Start(string operationName)
    {
        var activitySource = new ActivitySource(Source);
        var activity = activitySource.StartRootActivity(operationName, ActivityKind.Client);
        return activity;
    }

    internal static void Finish(
        string llmProvider,
        RootActivity rootActivity,
        IReadOnlyList<ChatMessageContent> responses,
        ChatHistory history,
        AiSettings? aiSettings = null,
        Prompt? prompt = null)
    {
        var result = responses[0];
        var innerCompletion = (ChatCompletion)result.InnerContent!;
        var activity = rootActivity.Activity ?? throw new ArgumentNullException(nameof(rootActivity));

        activity.SetTag("name", "aiadapter");
        activity.SetTag("telemetry.sdk.name", "opentelemetry");

        activity.SetTag("gen_ai.system", llmProvider);
        activity.SetTag("gen_ai.operation.name", "chat");
        activity.SetTag("gen_ai.endpoint", "openai.chat.completions");
        activity.SetTag("gen_ai.response.id", innerCompletion.Id);
        activity.SetTag("gen_ai.environment", "default");
        activity.SetTag("gen_ai.application_name", "palladin");
        activity.SetTag("gen_ai.request.model", result.ModelId);
        activity.SetTag("gen_ai.response.finish_reason", innerCompletion.FinishReason);

        if (prompt is not null)
        {
            activity.SetTag("langfuse.prompt.name", prompt.Name);
            if (int.TryParse(prompt.Version, out var version))
            {
                activity.SetTag("langfuse.prompt.version", version);
            }
        }

        if (aiSettings is not null)
        {
            activity.SetTag("langfuse.session.id", aiSettings.SessionId);
        }

        activity.SetTag("langsmith.span.kind", "LLM");
        activity.SetTag("llm.request.type", "chat");

        activity.SetTag("scope", "openlit.otel.tracing");

        activity.SetStatus(ActivityStatusCode.Ok);
    }
}
