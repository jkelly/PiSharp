// Node differentials of the coding tools against Fixtures/v1.2.0/tools.upstream.json, which Fixtures/capture-tools.mjs prints by running the
// pinned upstream tools (Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff, packages/durable/src/tools) under Node, as tools.node.<case>:
// - declarations: the name, description and parameters JSON each tool declares to models, its output limits, replay, structuredOutputSchema,
//   callers and deferred, byte for byte (read's description names the image formats since v1.2.0);
// - diffs: generateDiffString and generateUnifiedPatch (jsdiff 8.0.4 underneath) for 600 seeded old/new texts;
// - edits: the edit tool over a real file for authored cases (exact, CRLF, BOM, fuzzy, errors): text, details, the file afterwards;
// - bash: the bash tool for authored commands: streamed output, error, isError, structured output (long output digested, spill path masked)
//   and diagnostics, the streamed ones then the result's (spill paths masked). The fixture was captured with Git Bash on Windows; the
//   commands print the same under any POSIX shell;
// - images (v1.2.0, work list area e): the read tool without an image processor over small hand-made images (capture-tools.mjs's
//   IMAGE_CASES): isError, output (long data digested), diagnostics and details.
using System.Text;
using System.Text.Json;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Tools;
using PiSharp.Durable.Tools.Env;

internal static partial class Program
{
    private static readonly string[] DiffWords = ["alpha", "beta", "gamma", "", "  indented", "x", "y", "tail  ", "\u00E9"];

    /// <summary>capture-tools.mjs's <c>diffPair</c>.</summary>
    private static (string OldText, string NewText) DiffPair(Func<double> next)
    {
        string Word() => DiffWords[(int)Math.Floor(next() * DiffWords.Length)];
        var lines = new List<string>();
        var count = (int)Math.Floor(next() * 14);
        for (var index = 0; index < count; index++) lines.Add(Word());
        var edited = new List<string>();
        foreach (var line in lines)
        {
            var roll = next();
            if (roll < 0.55) edited.Add(line);
            else if (roll < 0.7) continue;
            else if (roll < 0.85) edited.Add(Word());
            else edited.AddRange([line, Word()]);
        }
        if (next() < 0.3) edited.Insert(0, Word());
        var oldEnd = next() < 0.7 ? "\n" : "";
        var newEnd = next() < 0.7 ? "\n" : "";
        return (lines.Count == 0 ? "" : string.Join("\n", lines) + oldEnd, edited.Count == 0 ? "" : string.Join("\n", edited) + newEnd);
    }

    /// <summary>capture-tools.mjs's <c>shellOutput</c>: bash's structured output with long output digested and a spill path masked.</summary>
    private static JsonValue ShellOutput(JsonValue? value)
    {
        if (value is not JsonObjectValue fields) return JsonValue.Null;
        var output = ((JsonStringValue)fields["output"]).Value;
        var properties = new List<KeyValuePair<string, JsonValue>>
        {
            KeyValuePair.Create("output", output.Length > 400
                ? JsonValue.Object([KeyValuePair.Create("length", (JsonValue)output.Length), KeyValuePair.Create("digest", (JsonValue)LineScanDigest(output))])
                : (JsonValue)output),
        };
        if (fields.ContainsKey("fullOutputPath")) properties.Add(KeyValuePair.Create("fullOutputPath", (JsonValue)"<spill>"));
        properties.AddRange(fields.Where(field => field.Key is not ("output" or "fullOutputPath")));
        return JsonValue.Object(properties);
    }

    private static JsonElement ToolsFixture120() => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(RepositoryRoot(), "tests", "PiSharp.Durable.Tests", "Fixtures", "v1.2.0", "tools.upstream.json"))).RootElement;

    private static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String ? value.GetString() : null;

    /// <summary>capture-tools.mjs's IMAGE_CASES.</summary>
    private static IEnumerable<(string Name, byte[] Bytes)> ToolsImageCases()
    {
        static byte[] U32BE(long n) => [(byte)(n >>> 24), (byte)(n >>> 16), (byte)(n >>> 8), (byte)n];
        static byte[] U32LE(long n) => [(byte)n, (byte)(n >>> 8), (byte)(n >>> 16), (byte)(n >>> 24)];
        static byte[] U16LE(int n) => [(byte)n, (byte)(n >>> 8)];
        static byte[] U16BE(int n) => [(byte)(n >>> 8), (byte)n];
        static byte[] Ascii(string text) => Encoding.Latin1.GetBytes(text);
        // A PNG declaring `width` by `height` in its IHDR, then an IDAT of `padding` bytes and IEND (CRCs zero: read checks only the header).
        static byte[] Png(long width, long height, int padding = 0) =>
        [
            0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0, 0, 0, 13, .. Ascii("IHDR"), .. U32BE(width), .. U32BE(height), 8, 6, 0, 0, 0, 0, 0, 0, 0,
            .. U32BE(padding), .. Ascii("IDAT"), .. new byte[padding], 0, 0, 0, 0, 0, 0, 0, 0, .. Ascii("IEND"), 0, 0, 0, 0,
        ];
        yield return ("png", Png(2, 3));
        yield return ("png-too-wide", Png(2001, 10));
        yield return ("png-too-tall", Png(10, 2001));
        yield return ("png-too-large", Png(10, 10, 3_500_000));
        yield return ("gif", [.. Ascii("GIF89a"), .. U16LE(4), .. U16LE(5), 0, 0, 0, 0x3b]);
        yield return ("jpeg", [0xff, 0xd8, 0xff, 0xe0, 0, 4, 0, 0, 0xff, 0xc0, 0, 11, 8, .. U16BE(6), .. U16BE(7), 1, 1, 0x11, 0, 0xff, 0xd9]);
        yield return ("jpeg-no-size", [0xff, 0xd8, 0xff, 0xe0, 0, 4, 0, 0, 0xff, 0xd9, 0, 0, 0, 0, 0, 0, 0, 0]);
        yield return ("webp-vp8l", [.. Ascii("RIFF"), 26, 0, 0, 0, .. Ascii("WEBPVP8L"), 14, 0, 0, 0, 0x2f,
            .. U32LE(((9 - 1) & 0x3fff) | (((7 - 1) & 0x3fff) << 14)), 0, 0, 0, 0, 0]);
        yield return ("bmp", [.. Ascii("BM"), 70, 0, 0, 0, 0, 0, 0, 0, 54, 0, 0, 0, 40, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 24, 0, .. new byte[40]]);
        yield return ("apng", [.. Png(2, 2)[..33], 0, 0, 0, 8, .. Ascii("acTL"), 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0]);
    }

    private static IEnumerable<(string, Func<Task>)> ToolsNodeCases()
    {
        yield return Case("tools.node.declarations-match-upstream-byte-for-byte", () =>
        {
            ToolRegistration[] tools = [CodingTools.CreateReadTool(), CodingTools.CreateWriteTool(), CodingTools.CreateEditTool(),
                CodingTools.CreateBashTool(), CodingTools.CreatePowerShellTool()];
            var declarations = ToolsFixture120().GetProperty("declarations");
            Equal(declarations.GetArrayLength(), tools.Length, "tools");
            for (var index = 0; index < tools.Length; index++)
            {
                var tool = tools[index];
                var upstream = declarations[index];
                Equal(upstream.GetProperty("name").GetString(), tool.Name, "name");
                Equal(upstream.GetProperty("description").GetString(), tool.Description, tool.Name + " description");
                Equal(upstream.GetProperty("parameters").GetString(), tool.Parameters.ToJson(), tool.Name + " parameters");
                Equal(PiSharp.Contracts.ToolSchemaOrigin.TypeBox, tool.ParametersOrigin, tool.Name + " parameters origin");
                var limits = tool.OutputLimits is null ? null
                    : "{" + string.Join(",", new[] { tool.OutputLimits.MaxBytes is { } bytes ? $"\"maxBytes\":{bytes}" : null,
                        tool.OutputLimits.MaxLines is { } lines ? $"\"maxLines\":{lines}" : null,
                        tool.OutputLimits.Retain is { } retain ? $"\"retain\":\"{retain.ToString().ToLowerInvariant()}\"" : null }.OfType<string>()) + "}";
                Equal(OptionalString(upstream, "outputLimits"), limits, tool.Name + " output limits");
                // Upstream sets no replay on any coding tool: the default, unsafe.
                Equal(OptionalString(upstream, "replay"), tool.Replay?.ToString().ToLowerInvariant(), tool.Name + " replay");
                Equal(upstream.GetProperty("prepareArguments").GetBoolean(), tool.PrepareArguments is not null, tool.Name + " prepareArguments");
                Equal(OptionalString(upstream, "structuredOutputSchema"), tool.StructuredOutputSchema?.ToJson(), tool.Name + " structuredOutputSchema");
                Equal(upstream.GetProperty("callers").ValueKind == System.Text.Json.JsonValueKind.Null ? null
                    : upstream.GetProperty("callers").GetRawText(), tool.Callers is { } callers
                    ? JsonValue.Array(callers.Select(caller => (JsonValue)(caller == ToolCaller.Model ? "model" : "tools"))).ToJson() : null, tool.Name + " callers");
                Equal(upstream.GetProperty("deferred").ValueKind == System.Text.Json.JsonValueKind.True, tool.Deferred, tool.Name + " deferred");
            }
            Equal("coding-tools:read,write,edit,bash", $"{CodingTools.Extension.Name}:{string.Join(",", CodingTools.Extension.Tools!.Select(tool => tool.Name))}",
                "CodingTools");
            Check(ReferenceEquals(CodingTools.Extension, CodingTools.Extension), "one CodingTools extension");
        });

        yield return Case("tools.node.diffs-match-jsdiff-for-seeded-edits", () =>
        {
            var diffs = ToolsFixture120().GetProperty("diffs");
            for (var seed = 1; seed <= 600; seed++)
            {
                var (oldText, newText) = DiffPair(LineScanRandom(seed));
                var display = EditDiff.GenerateDiffString(oldText, newText);
                var patch = EditDiff.GenerateUnifiedPatch("file.txt", oldText, newText);
                var actual = string.Join("\0", oldText, newText, display.Diff, display.FirstChangedLine?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "undefined", patch);
                Equal(diffs[seed - 1].GetString(), LineScanDigest(actual), $"seed {seed}: {JsonValue.String(oldText).ToJson()} -> {JsonValue.String(newText).ToJson()}\n{display.Diff}\n{patch}");
                if (oldText != newText) Equal(newText, ToolRunner.ApplyPatch(oldText, patch), $"seed {seed} patch applies");
            }
        });

        yield return Case("tools.node.edit-results-match-upstream", () => WithToolDir(async cwd =>
        {
            var env = new LocalExecutionEnv(cwd);
            var tool = CodingTools.CreateEditTool();
            foreach (var upstream in ToolsFixture120().GetProperty("edits").EnumerateArray())
            {
                var name = upstream.GetProperty("name").GetString()!;
                await File.WriteAllBytesAsync(Path.Combine(cwd, "edit.txt"), Encoding.UTF8.GetBytes(upstream.GetProperty("content").GetString()!));
                var args = JsonValue.Object([new("path", "edit.txt"), new("edits", JsonValue.FromElement(upstream.GetProperty("edits")))]);
                if (OptionalString(upstream, "error") is { } error)
                {
                    var failed = await ToolRunner.RunFailing(tool, args, env);
                    Equal(error, failed.Error.Message, name + " error");
                }
                else
                {
                    var result = await ToolRunner.Run(tool, args, env);
                    Equal(upstream.GetProperty("text").GetString(), result.Text, name + " text");
                    Equal(upstream.GetProperty("details").GetRawText().Length > 0 ? JsonValue.FromElement(upstream.GetProperty("details")).ToJson() : null,
                        result.Result.Details?.ToJson(), name + " details");
                }
                Equal(upstream.GetProperty("after").GetString(), Convert.ToHexStringLower(await File.ReadAllBytesAsync(Path.Combine(cwd, "edit.txt"))),
                    name + " file afterwards");
            }
        }));

        yield return Case("tools.node.read-images-match-upstream", () => WithToolDir(async cwd =>
        {
            var env = new LocalExecutionEnv(cwd);
            var tool = CodingTools.CreateReadTool();
            var cases = ToolsImageCases().ToDictionary(item => item.Name, item => item.Bytes);
            var upstreamCases = ToolsFixture120().GetProperty("images").EnumerateArray().ToList();
            Equal(string.Join(",", cases.Keys), string.Join(",", upstreamCases.Select(item => item.GetProperty("name").GetString())), "image cases");
            static JsonValue Long(string text) => text.Length > 400
                ? JsonValue.Object([new("length", text.Length), new("digest", LineScanDigest(text))]) : text;
            foreach (var upstream in upstreamCases)
            {
                var name = upstream.GetProperty("name").GetString()!;
                var bytes = cases[name];
                Equal(JsonValue.FromElement(upstream.GetProperty("file")).ToJson(), Long(Convert.ToHexStringLower(bytes)).ToJson(), name + " file");
                await File.WriteAllBytesAsync(Path.Combine(cwd, name + ".bin"), bytes);
                var result = await tool.Execute(JsonValue.Object([new("path", name + ".bin")]), new FakeToolApi(env), ToolContext);
                Equal(null, OptionalString(upstream, "error"), name + " error");
                Equal(upstream.GetProperty("isError").GetBoolean(), result.IsError ?? false, name + " isError");
                var output = JsonValue.Array((result.Output ?? JsonArrayValue.Empty).Cast<JsonObjectValue>().Select(part =>
                    (JsonValue)(((JsonStringValue)part["type"]).Value == "image"
                        ? JsonValue.Object([new("type", "image"), new("mimeType", part["mimeType"]), new("data", Long(((JsonStringValue)part["data"]).Value))])
                        : JsonValue.Object([new("type", part["type"]), new("text", Long(((JsonStringValue)part["text"]).Value))]))));
                Equal(JsonValue.FromElement(upstream.GetProperty("output")).ToJson(), output.ToJson(), name + " output");
                var diagnostics = JsonValue.Array((result.Diagnostics ?? []).Select(diagnostic =>
                {
                    var fields = new List<KeyValuePair<string, JsonValue>> { new("severity", diagnostic.Severity.ToString().ToLowerInvariant()) };
                    if (diagnostic.Code is { } code) fields.Add(new("code", code));
                    fields.Add(new("message", diagnostic.Message));
                    return (JsonValue)JsonValue.Object(fields);
                }));
                Equal(JsonValue.FromElement(upstream.GetProperty("diagnostics")).ToJson(), diagnostics.ToJson(), name + " diagnostics");
                Equal(upstream.GetProperty("details").GetRawText(), result.Details?.ToJson() ?? "null", name + " details");
            }
        }));

        yield return Case("tools.node.bash-results-match-upstream", () => WithToolDir(async cwd =>
        {
            var env = new LocalExecutionEnv(cwd);
            var tool = CodingTools.CreateBashTool();
            foreach (var upstream in ToolsFixture120().GetProperty("bash").EnumerateArray())
            {
                var name = upstream.GetProperty("name").GetString()!;
                var api = new FakeToolApi(env);
                string? error = null;
                ToolExecutionResult? result = null;
                try { result = await tool.Execute(JsonValue.FromElement(upstream.GetProperty("args")), api, ToolContext); }
                catch (Exception thrown) { error = thrown.Message; }
                var output = string.Concat(api.Collected);
                var expectedOutput = upstream.GetProperty("output");
                if (expectedOutput.ValueKind == System.Text.Json.JsonValueKind.String) Equal(expectedOutput.GetString(), output, name + " output");
                else
                {
                    Equal(expectedOutput.GetProperty("length").GetInt32(), output.Length, name + " output length");
                    Equal(expectedOutput.GetProperty("digest").GetString(), LineScanDigest(output), name + " output digest");
                }
                Equal(OptionalString(upstream, "error"), error, name + " error");
                if (error is null)
                {
                    Equal(null, result!.Output, name + " output");
                    var isError = upstream.GetProperty("isError");
                    Equal(isError.ValueKind == System.Text.Json.JsonValueKind.Null ? null : isError.GetBoolean(), result.IsError, name + " isError");
                    Equal(JsonValue.FromElement(upstream.GetProperty("structuredOutput")).ToJson(), ShellOutput(result.StructuredOutput).ToJson(),
                        name + " structuredOutput");
                }
                var diagnostics = string.Join("|", api.Reported.Concat(error is null ? result!.Diagnostics ?? [] : []).Select(diagnostic =>
                    $"{diagnostic.Severity.ToString().ToLowerInvariant()} {diagnostic.Code} " +
                    (diagnostic.Message.StartsWith("Full output: ", StringComparison.Ordinal) ? "Full output: <spill>" : diagnostic.Message)));
                Equal(string.Join("|", upstream.GetProperty("diagnostics").EnumerateArray().Select(diagnostic =>
                    $"{diagnostic.GetProperty("severity").GetString()} {OptionalString(diagnostic, "code")} {diagnostic.GetProperty("message").GetString()}")),
                    diagnostics, name + " diagnostics");
            }
        }));
    }
}
