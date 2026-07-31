using System.Text.Json;
using Palladin.Core.Ai.EngineeringPlatform;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace Palladin.Core.Ai.SemanticKernel;

internal sealed class SemanticKernelAiClient(
    IEngineeringPlatformClient engineeringPlatformClient,
    IOptions<SemanticKernelOptions> options,
    IServiceProvider serviceProvider) : IAiClient
{
    public async Task<AiResult<T>> ChatAsync<T>(
        AiSettings settings,
        CancellationToken cancellationToken = default,
        params AiChatMessage[] messages)
    {
#pragma warning disable SKEXP0010
        var promptSettings = new OpenAIPromptExecutionSettings
        {
            ResponseFormat = typeof(T)
        };
#pragma warning restore SKEXP0010

        var response = await ChatAsync(settings,
            messages,
            promptSettings,
            cancellationToken);

        var json = response.Response;
        var value = JsonSerializer.Deserialize<T>(json)!;

        return new AiResult<T>(value, json);
    }

    public async Task<AiResult> ChatAsync(
        AiSettings settings,
        CancellationToken cancellationToken = default,
        params AiChatMessage[] messages) =>
        await ChatAsync(settings,
            messages,
            cancellationToken: cancellationToken);

    private async Task<AiResult> ChatAsync(
        AiSettings settings,
        AiChatMessage[] messages,
        PromptExecutionSettings? promptSettings = null,
        CancellationToken cancellationToken = default)
    {
        var chatHistory = await GetChatHistoryAsync(settings, messages, cancellationToken);
        var llmProvider = settings.Provider ?? options.Value.DefaultProvider;
        var chatCompletionService = serviceProvider.GetRequiredKeyedService<IChatCompletionService>(llmProvider);

        using var activity = SemanticKernelTracing.Start(settings.ConversationName);

        var response = await chatCompletionService.GetChatMessageContentsAsync(
            chatHistory,
            promptSettings,
            cancellationToken: cancellationToken);

        return new AiResult(response.First().Content!);
    }

    private async ValueTask<ChatHistory> GetChatHistoryAsync(
        AiSettings settings,
        AiChatMessage[] messages,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(settings?.PromptNavigation?.Key))
        {
            var systemPrompt = await engineeringPlatformClient.GetPromptAsync(
                settings.PromptNavigation.Key,
                cancellationToken);

            messages = systemPrompt.Messages
                .Select(x => x with { Content = ReplaceVariables(settings.PromptNavigation, x) })
                .Union(messages)
                .ToArray();
        }

        return MapToSemanticKernelMessages(messages);
    }

    private ChatHistory MapToSemanticKernelMessages(ICollection<AiChatMessage> messages)
    {
        var chatHistory = new ChatHistory();

        foreach (var promptMessage in messages)
        {
            chatHistory.Add(new ChatMessageContent(new AuthorRole(promptMessage.Role), promptMessage.Content));
        }

        return chatHistory;
    }

    private static string ReplaceVariables(AiPromptNavigation promptNavigation, AiChatMessage promptMessage)
    {
        return promptNavigation.Args
            .Aggregate(promptMessage.Content, (current, arg) => current.Replace($"{{{arg.argName}}}", arg.argValue));
    }
}
