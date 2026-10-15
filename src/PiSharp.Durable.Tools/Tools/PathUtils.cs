// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/src/tools/path-utils.ts
// JavaScript's String.prototype.normalize leaves lone surrogates as they are, where .NET's Normalize rejects them; Unicode.Normalize
// normalizes the well-formed runs between them instead.
using System.Text;
using System.Text.RegularExpressions;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Env;

namespace PiSharp.Durable.Tools;

/// <summary>Upstream <c>resolveToolPath</c> and <c>resolveReadToolPath</c>.</summary>
internal static partial class PathUtils
{
    private const string NarrowNoBreakSpace = "\u202F";

    [GeneratedRegex("[\u00A0\u2000-\u200A\u202F\u205F\u3000]")]
    private static partial Regex UnicodeSpaces();

    [GeneratedRegex(" (AM|PM)\\.", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Meridiem();

    private static string NormalizeToolPath(string path)
    {
        var normalized = UnicodeSpaces().Replace(path, " ");
        return normalized.StartsWith('@') ? normalized[1..] : normalized;
    }

    /// <summary>The absolute path of a tool's path argument: Unicode spaces become spaces and a leading <c>@</c> is dropped.</summary>
    public static async Task<string> ResolveToolPathAsync(IExecutionEnv env, string path, DurableContext context) =>
        (await env.AbsolutePathAsync(NormalizeToolPath(path), context).ConfigureAwait(false)).GetOrThrow();

    /// <summary><see cref="ResolveToolPathAsync"/>, then the first existing variant among the spellings macOS screenshot names, decomposed
    /// characters and curly apostrophes produce; the resolved path when none exists.</summary>
    public static async Task<string> ResolveReadToolPathAsync(IExecutionEnv env, string path, DurableContext context)
    {
        var resolved = await ResolveToolPathAsync(env, path, context).ConfigureAwait(false);
        var decomposed = Unicode.Normalize(resolved, NormalizationForm.FormD);
        string[] variants =
        [
            resolved,
            Meridiem().Replace(resolved, NarrowNoBreakSpace + "$1."),
            decomposed,
            resolved.Replace('\'', '\u2019'),
            decomposed.Replace('\'', '\u2019'),
        ];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var variant in variants)
        {
            if (!seen.Add(variant)) continue;
            if ((await env.ExistsAsync(variant, context).ConfigureAwait(false)).GetOrThrow()) return variant;
        }
        return resolved;
    }
}

/// <summary>Unicode normalization as JavaScript's <c>String.prototype.normalize</c> performs it.</summary>
internal static class Unicode
{
    /// <summary>The text normalized; lone surrogates stay where they are and the well-formed runs between them are normalized.</summary>
    public static string Normalize(string text, NormalizationForm form)
    {
        if (!HasLoneSurrogate(text)) return text.Normalize(form);
        var builder = new StringBuilder(text.Length);
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            var value = text[index];
            if (char.IsHighSurrogate(value) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1])) { index++; continue; }
            if (!char.IsSurrogate(value)) continue;
            builder.Append(text[start..index].Normalize(form)).Append(value);
            start = index + 1;
        }
        return builder.Append(text[start..].Normalize(form)).ToString();
    }

    private static bool HasLoneSurrogate(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            var value = text[index];
            if (char.IsHighSurrogate(value) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1])) { index++; continue; }
            if (char.IsSurrogate(value)) return true;
        }
        return false;
    }
}
