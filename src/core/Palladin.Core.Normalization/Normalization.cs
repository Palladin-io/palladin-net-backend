namespace Palladin.Core.Normalization;

public static class Normalization
{
    public static string Do(string name) =>
        name.Trim().ToLower();
}
