namespace Palladin.Core.Ai.Exceptions;

internal sealed class PromptNotFoundException(string key) : Exception($"Prompt with key '{key}' not found.");
