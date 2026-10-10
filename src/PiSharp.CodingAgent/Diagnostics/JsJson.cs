// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/bug-report.ts (JSON.stringify output).
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Contracts.Compatibility;

namespace PiSharp.CodingAgent.Diagnostics;

/// <summary><c>JSON.stringify</c> output for the files the diagnostics modules write: ECMAScript key order, number and string
/// forms through <see cref="EcmaScriptJsonProjection"/>, and the <c>space</c> argument's layout with <c>\n</c> line breaks.</summary>
internal static class JsJson
{
    private static readonly JsonSerializerOptions NodeOutput = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly EcmaScriptJsonProjectionOptions Unbounded = new(MaximumInputCharacters: int.MaxValue,
        MaximumInputBytes: int.MaxValue, MaximumOutputCharacters: int.MaxValue, MaximumOutputBytes: int.MaxValue, MaximumDepth: PiSharp.Contracts.JsonData.MaximumDepth,
        MaximumNodes: int.MaxValue, MaximumPropertiesPerObject: int.MaxValue, MaximumNumbers: int.MaxValue,
        MaximumNumberCharacters: 16_384, MaximumTotalNumberCharacters: int.MaxValue, MaximumStringCharacters: int.MaxValue);

    /// <summary><c>JSON.stringify(value)</c>.</summary>
    internal static string Stringify(JsonNode? value) =>
        EcmaScriptJsonProjection.Project(value is null ? "null" : value.ToJsonString(NodeOutput), Unbounded);

    /// <summary><c>JSON.stringify(value, null, indent)</c>: empty objects and arrays stay <c>{}</c> and <c>[]</c>.</summary>
    internal static string Stringify(JsonNode? value, int indent)
    {
        var compact = Stringify(value); var output = new StringBuilder(compact.Length * 2); var depth = 0;
        void Break() { output.Append('\n'); output.Append(' ', depth * indent); }
        for (var index = 0; index < compact.Length; index++)
        {
            var character = compact[index];
            if (character == '"')
            {
                var end = index + 1;
                while (compact[end] != '"') end += compact[end] == '\\' ? 2 : 1;
                output.Append(compact, index, end - index + 1); index = end;
            }
            else if (character is '{' or '[')
            {
                if (compact[index + 1] is '}' or ']') { output.Append(character).Append(compact[index + 1]); index++; continue; }
                output.Append(character); depth++; Break();
            }
            else if (character is '}' or ']') { depth--; Break(); output.Append(character); }
            else if (character == ',') { output.Append(','); Break(); }
            else if (character == ':') output.Append(": ");
            else output.Append(character);
        }
        return output.ToString();
    }

    /// <summary>JavaScript <c>String.prototype.trim</c>: ECMAScript white space and line terminators.</summary>
    internal static string Trim(string value)
    {
        int start = 0, end = value.Length;
        while (start < end && IsWhiteSpace(value[start])) start++;
        while (end > start && IsWhiteSpace(value[end - 1])) end--;
        return value[start..end];
    }

    internal static bool IsWhiteSpace(char character) => Resources.PromptTemplateParser.IsEcmaWhitespace(character);
}
