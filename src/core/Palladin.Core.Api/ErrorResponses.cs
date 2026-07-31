namespace Palladin.Core.Api;

public static class ErrorResponses
{
    public static string General(string key)
        => $"errors.backend.{key}";
}
