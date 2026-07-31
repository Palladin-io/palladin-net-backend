namespace Palladin.Tests.Integrations.Shared.Extensions;

public static class ReflectionExtensions
{
    public static void SetPrivateFieldValue<T>(this object obj, string propName, T val)
    {
        ArgumentNullException.ThrowIfNull(obj);
        var type = obj.GetType();
        type.GetProperty(propName)!.SetValue(obj, val);
    }
}
