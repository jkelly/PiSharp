// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/src/tools/edit.ts (v1.2.0 renamed the result's content to output)
using PiSharp.Durable.Chord;
using PiSharp.Durable.Env;

namespace PiSharp.Durable.Tools;

/// <summary>Upstream <c>createEditTool</c>.</summary>
internal static class EditTool
{
    /// <summary><c>Type.Object({ path, edits: Type.Array(Type.Object({ oldText, newText })) })</c> as TypeBox prints it.</summary>
    internal static readonly JsonObjectValue Parameters = (JsonObjectValue)JsonValue.Parse(
        """{"type":"object","required":["path","edits"],"properties":{"path":{"type":"string","description":"Path to the file to edit (relative or absolute)"},"edits":{"type":"array","items":{"type":"object","required":["oldText","newText"],"properties":{"oldText":{"type":"string","description":"Exact text for one targeted replacement. It must be unique in the original file and must not overlap with any other edits[].oldText in the same call."},"newText":{"type":"string","description":"Replacement text for this targeted edit."}}},"description":"One or more targeted replacements. Each edit is matched against the original file, not incrementally. Do not include overlapping or nested edits. If two changes touch the same block or nearby lines, merge them into one edit instead."}}}""");

    public static ToolRegistration Create() => Define.Tool(new ToolRegistration
    {
        Name = "edit",
        Description = "Edit a single file using exact text replacement. Every edits[].oldText must match a unique, non-overlapping region of the original file. If two changes affect the same block or nearby lines, merge them into one edit instead of emitting overlapping edits. Do not include large unchanged regions just to connect distant changes.",
        Parameters = Parameters,
        PrepareArguments = PrepareEditArguments,
        Execute = ExecuteAsync,
    });

    private static bool IsSingleEditInput(JsonValue? value) =>
        value is JsonObjectValue edit && edit.TryGetValue("oldText", out var oldText) && oldText is JsonStringValue &&
        edit.TryGetValue("newText", out var newText) && newText is JsonStringValue;

    /// <summary>
    /// Repairs shapes models commonly send: <c>edits</c> as a JSON string or as a single edit object, and a top-level <c>oldText</c>/
    /// <c>newText</c> pair. JSON values are immutable, so the call's arguments stay unchanged.
    /// </summary>
    internal static JsonValue PrepareEditArguments(JsonValue input)
    {
        if (input is not JsonObjectValue args) return input;
        if (args.TryGetValue("edits", out var edits) && edits is JsonStringValue text)
        {
            try
            {
                var parsed = JsonValue.Parse(text.Value);
                if (parsed is JsonArrayValue) args = args.SetItem("edits", parsed);
                else if (IsSingleEditInput(parsed)) args = args.SetItem("edits", JsonValue.Array(parsed));
            }
            catch (Exception) { } // Not JSON: validation reports the string.
        }
        else if (IsSingleEditInput(edits))
        {
            args = args.SetItem("edits", JsonValue.Array(edits!));
        }

        if (!(args.TryGetValue("oldText", out var oldText) && oldText is JsonStringValue && args.TryGetValue("newText", out var newText) &&
            newText is JsonStringValue)) return args;
        List<JsonValue> merged = args.TryGetValue("edits", out var existing) && existing is JsonArrayValue array ? [.. array] : [];
        merged.Add(JsonValue.Object([new("oldText", oldText), new("newText", newText)]));
        return args.Remove("oldText").Remove("newText").SetItem("edits", JsonValue.Array(merged));
    }

    private static (string Path, List<EditReplacement> Edits) ValidateEditInput(JsonObjectValue input)
    {
        if (!input.TryGetValue("edits", out var value) || value is not JsonArrayValue { Count: > 0 } edits)
            throw new InvalidOperationException("Edit tool input is invalid. edits must contain at least one replacement.");
        return (((JsonStringValue)input["path"]).Value, [.. edits.Cast<JsonObjectValue>()
            .Select(edit => new EditReplacement(((JsonStringValue)edit["oldText"]).Value, ((JsonStringValue)edit["newText"]).Value))]);
    }

    private static InvalidOperationException EditAccessError(string path, FileError error) =>
        new($"Could not edit file: {path}. Error code: {error.CodeName}.", error);

    private static async Task<ToolExecutionResult> ExecuteAsync(JsonValue arguments, IToolExecutionApi api, DurableContext context)
    {
        var (path, edits) = ValidateEditInput((JsonObjectValue)arguments);
        var env = ToolEnv.Require(api);
        var absolutePath = await PathUtils.ResolveToolPathAsync(env, path, context).ConfigureAwait(false);
        return await FileMutationQueue.RunAsync(env, absolutePath, async () =>
        {
            if (context.IsCancellationRequested) throw Aborted();
            var info = await env.FileInfoAsync(absolutePath, context).ConfigureAwait(false);
            if (!info.IsOk) throw EditAccessError(path, info.Error);
            if (info.Value.Kind is not (FileKind.File or FileKind.Symlink))
                throw new InvalidOperationException($"Could not edit file: {path}. Path is not a file.");

            var readResult = await env.ReadTextFileAsync(absolutePath, context).ConfigureAwait(false);
            if (!readResult.IsOk) throw EditAccessError(path, readResult.Error);
            if (context.IsCancellationRequested) throw Aborted();

            var (bom, content) = EditDiff.StripBom(readResult.Value);
            var originalEnding = EditDiff.DetectLineEnding(content);
            var normalizedContent = EditDiff.NormalizeToLF(content);
            var (baseContent, newContent) = EditDiff.ApplyEditsToNormalizedContent(normalizedContent, edits, path);
            if (context.IsCancellationRequested) throw Aborted();

            var finalContent = bom + EditDiff.RestoreLineEndings(newContent, originalEnding);
            var writeResult = await env.WriteFileAsync(absolutePath, finalContent, context).ConfigureAwait(false);
            if (!writeResult.IsOk) throw EditAccessError(path, writeResult.Error);
            if (context.IsCancellationRequested) throw Aborted();

            var diffResult = EditDiff.GenerateDiffString(baseContent, newContent);
            var details = JsonObjectValue.CreateBuilder();
            details["diff"] = diffResult.Diff;
            details["patch"] = EditDiff.GenerateUnifiedPatch(path, baseContent, newContent);
            if (diffResult.FirstChangedLine is { } firstChangedLine) details["firstChangedLine"] = firstChangedLine;
            return new ToolExecutionResult
            {
                Output = ToolContent.Text($"Successfully replaced {edits.Count} block(s) in {path}."),
                Details = details.ToImmutable(),
            };
        }, context).ConfigureAwait(false);
    }

    private static OperationCanceledException Aborted() => new("Operation aborted");
}
