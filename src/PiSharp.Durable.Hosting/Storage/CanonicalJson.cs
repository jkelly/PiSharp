// PiSharp-only (decision 0010): one spelling of a JSON value for hashes, so the same input, policy or evidence always has the same identity.
using System.Security.Cryptography;
using System.Text;
using PiSharp.Durable.Chord;

namespace PiSharp.Durable.Hosting;

/// <summary>Canonical JSON: object keys in ordinal order, JavaScript number and string formatting, no whitespace.</summary>
public static class CanonicalJson
{
    /// <summary>The canonical text of <paramref name="value"/>.</summary>
    public static string Serialize(JsonValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var builder = new StringBuilder();
        Write(builder, value);
        return builder.ToString();
    }

    /// <summary><c>sha256:</c> and the lowercase hex SHA-256 of the canonical text.</summary>
    public static string Hash(JsonValue value) => HashText(Serialize(value));

    /// <summary><c>sha256:</c> and the lowercase hex SHA-256 of the UTF-8 text.</summary>
    public static string HashText(string text) => HashBytes(Encoding.UTF8.GetBytes(text));

    /// <summary><c>sha256:</c> and the lowercase hex SHA-256 of the bytes.</summary>
    public static string HashBytes(ReadOnlySpan<byte> bytes) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>A short stable identifier: <paramref name="prefix"/> and 24 hex characters of the hash of the parts.</summary>
    public static string Id(string prefix, params string[] parts) =>
        prefix + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u001f', parts))))[..24];

    private static void Write(StringBuilder builder, JsonValue value)
    {
        switch (value)
        {
            case JsonObjectValue obj:
                builder.Append('{');
                var first = true;
                foreach (var key in obj.Keys.OrderBy(key => key, StringComparer.Ordinal))
                {
                    if (!first) builder.Append(',');
                    first = false;
                    JsonValue.String(key).Write(builder);
                    builder.Append(':');
                    Write(builder, obj[key]);
                }
                builder.Append('}');
                break;
            case JsonArrayValue array:
                builder.Append('[');
                for (var index = 0; index < array.Count; index++)
                {
                    if (index > 0) builder.Append(',');
                    Write(builder, array[index]);
                }
                builder.Append(']');
                break;
            default:
                value.Write(builder);
                break;
        }
    }
}
