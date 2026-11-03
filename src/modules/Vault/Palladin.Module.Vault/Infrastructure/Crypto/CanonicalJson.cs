using System.Globalization;
using System.Text;

namespace Palladin.Module.Vault.Infrastructure.Crypto;

// RFC 8785 serializer for the closed Agent Submission objects: string, non-negative integer and
// nested-object members only, keys sorted by UTF-16 code units and strings escaped like ECMAScript.
internal static class CanonicalJson
{
    internal static byte[] Serialize(IReadOnlyDictionary<string, object> value)
    {
        var builder = new StringBuilder(512);
        WriteObject(builder, value);
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    internal static string SerializeToString(IReadOnlyDictionary<string, object> value) =>
        Encoding.UTF8.GetString(Serialize(value));

    private static void WriteObject(StringBuilder builder, IReadOnlyDictionary<string, object> value)
    {
        builder.Append('{');
        var first = true;
        foreach (var key in value.Keys.Order(StringComparer.Ordinal))
        {
            if (!first)
            {
                builder.Append(',');
            }

            first = false;
            WriteString(builder, key);
            builder.Append(':');
            WriteValue(builder, value[key]);
        }

        builder.Append('}');
    }

    private static void WriteValue(StringBuilder builder, object value)
    {
        switch (value)
        {
            case string text:
                WriteString(builder, text);
                break;
            case ushort or uint:
                builder.Append(Convert.ToUInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
                break;
            case IReadOnlyDictionary<string, object> nested:
                WriteObject(builder, nested);
                break;
            default:
                throw new InvalidOperationException("Canonical JSON supports only strings, small integers and objects.");
        }
    }

    private static void WriteString(StringBuilder builder, string value)
    {
        builder.Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (character < 0x20)
                    {
                        builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(character);
                    }

                    break;
            }
        }

        builder.Append('"');
    }
}
