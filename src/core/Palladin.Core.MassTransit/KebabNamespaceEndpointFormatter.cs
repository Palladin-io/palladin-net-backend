using System.Text;
using System.Text.RegularExpressions;
using MassTransit;
using MassTransit.Metadata;
using MassTransit.NewIdFormatters;

namespace Palladin.Core.MassTransit;

public sealed class KebabCaseWithNamespacesEndpointNameFormatter :
    IEndpointNameFormatter
{
    private const int MaxTemporaryQueueNameLength = 72;
    private const int OverheadLength = 29;

    private static readonly Regex _nonAlpha = new("[^a-zA-Z0-9]", RegexOptions.Compiled | RegexOptions.Singleline);

    public string Separator => "";

    public string TemporaryEndpoint(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            tag = "endpoint";
        }

        var host = HostMetadataCache.Host;

        var machineName = _nonAlpha.Replace(host.MachineName ?? string.Empty, "");
        var machineNameLength = machineName.Length;

        var processName = _nonAlpha.Replace(host.ProcessName ?? string.Empty, "");
        var processNameLength = processName.Length;

        var tagLength = tag.Length;

        var nameLength = machineNameLength + processNameLength + tagLength + OverheadLength;

        var overage = nameLength - MaxTemporaryQueueNameLength;

        const int spread = (MaxTemporaryQueueNameLength - OverheadLength) / 3;

        if (overage > 0 && machineNameLength > spread)
        {
            overage -= machineNameLength - spread;
            machineNameLength = spread;
        }

        if (overage > 0 && processNameLength > spread)
        {
            overage -= processNameLength - spread;
            processNameLength = spread;
        }

        if (overage > 0 && tagLength > spread)
        {
            tagLength = spread;
        }

        var sb = new StringBuilder(machineNameLength + processNameLength + tagLength + OverheadLength);

        sb.Append(machineName, 0, machineNameLength);
        sb.Append('_');
        sb.Append(processName, 0, processNameLength);

        sb.Append('_');
        sb.Append(tag, 0, tagLength);
        sb.Append('_');
        sb.Append(NewId.Next().ToString(ZBase32Formatter.LowerCase));

        return sb.ToString();
    }

    public string Consumer<T>()
        where T : class, IConsumer
    {
        return GetConsumerName<T>();
    }

    public string Message<T>()
        where T : class
    {
        return GetMessageName(typeof(T));
    }

    public string Saga<T>()
        where T : class, ISaga
    {
        return GetSagaName<T>();
    }

    public string ExecuteActivity<T, TArguments>()
        where T : class, IExecuteActivity<TArguments>
        where TArguments : class
    {
        var activityName = GetActivityName<T>();

        return $"{activityName}_execute";
    }

    public string CompensateActivity<T, TLog>()
        where T : class, ICompensateActivity<TLog>
        where TLog : class
    {
        var activityName = GetActivityName<T>();

        return $"{activityName}_compensate";
    }

    private string GetConsumerName<T>()
    {
        if (typeof(T).IsGenericType && typeof(T).Name.Contains('`'))
        {
            return SanitizeName(FormatName(typeof(T).GetGenericArguments().Last()));
        }

        var consumerName = FormatName(typeof(T));

        return SanitizeName(consumerName);
    }

    private string GetMessageName(Type type)
    {
        if (type.IsGenericType && type.Name.Contains('`'))
        {
            return SanitizeName(FormatName(type.GetGenericArguments().Last()));
        }

        var messageName = type.Name;

        return SanitizeName(messageName);
    }

    private string GetSagaName<T>()
    {
        const string saga = "Saga";

        var sagaName = FormatName(typeof(T));

        if (sagaName.EndsWith(saga, StringComparison.InvariantCultureIgnoreCase))
        {
            sagaName = sagaName.Substring(0, sagaName.Length - saga.Length);
        }

        return SanitizeName(sagaName);
    }

    private string GetActivityName<T>()
    {
        const string activity = "Activity";

        var activityName = FormatName(typeof(T));

        if (activityName.EndsWith(activity, StringComparison.InvariantCultureIgnoreCase))
        {
            activityName = activityName.Substring(0, activityName.Length - activity.Length);
        }

        return SanitizeName(activityName);
    }

    private static readonly Regex _pattern = new("(?<=[a-z0-9])[A-Z]", RegexOptions.Compiled);
    private const char HyphenSeparator = '-';

    public string SanitizeName(string name)
    {
        return _pattern.Replace(name, m => HyphenSeparator + m.Value).ToLowerInvariant();
    }

    private static string FormatName(Type type)
    {
        var name = type.Name;
        var moduleName = type.Assembly
            .GetName()
            .Name!
            .Split(".")
            .Last();

        return $"{moduleName}.{name}";
    }
}
