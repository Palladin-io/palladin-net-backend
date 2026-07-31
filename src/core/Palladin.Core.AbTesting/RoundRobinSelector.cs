namespace Palladin.Core.AbTesting;

internal sealed class RoundRobinSelector : IActionSelector
{
    private readonly Dictionary<string, int> _indices = new();
    private readonly object _lock = new();

    public Action Select(string key, params (string name, Action action)[] actions)
    {
        if (actions.Length == 0)
        {
            throw new ArgumentException("At least one action is required", nameof(actions));
        }

        lock (_lock)
        {
            _indices.TryGetValue(key, out var index);
            var selectedAction = actions[index % actions.Length].action;
            _indices[key] = index + 1;
            return selectedAction;
        }
    }
}
