namespace Palladin.Core.AbTesting;

public interface IActionSelector
{
    Action Select(string key, params (string name, Action action)[] actions);
}
