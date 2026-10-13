// Node differentials of the coding tools against Fixtures/tools.upstream.json, which Fixtures/capture-tools.mjs prints by running the pinned
// upstream tools (Pi abe508e1b89912adde45528136c3221eb69acdd7, packages/durable/src/tools) under Node, as tools.node.<case>:
// - declarations: the name, description and parameters JSON each tool declares to models, its output limits and replay, byte for byte;
// - diffs: generateDiffString and generateUnifiedPatch (jsdiff 8.0.4 underneath) for 600 seeded old/new texts;
// - edits: the edit tool over a real file for authored cases (exact, CRLF, BOM, fuzzy, errors): text, details, the file afterwards;
// - bash: the bash tool for authored commands: streamed output, error and diagnostics (spill paths masked). The fixture was captured with
//   Git Bash on Windows; the commands print the same under any POSIX shell.
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

    private static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String ? value.GetString() : null;

    private static IEnumerable<(string, Func<Task>)> ToolsNodeCases()
    {
        yield return Case("tools.node.declarations-match-upstream-byte-for-byte", () =>
        {
            ToolRegistration[] tools = [CodingTools.CreateReadTool(), CodingTools.CreateWriteTool(), CodingTools.CreateEditTool(),
                CodingTools.CreateBashTool(), CodingTools.CreatePowerShellTool()];
            var declarations = ToolsFixture().GetProperty("declarations");
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
            }
            Equal("coding-tools:read,write,edit,bash", $"{CodingTools.Extension.Name}:{string.Join(",", CodingTools.Extension.Tools!.Select(tool => tool.Name))}",
                "CodingTools");
            Check(ReferenceEquals(CodingTools.Extension, CodingTools.Extension), "one CodingTools extension");
        });

        yield return Case("tools.node.diffs-match-jsdiff-for-seeded-edits", () =>
        {
            var diffs = ToolsFixture().GetProperty("diffs");
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
            foreach (var upstream in ToolsFixture().GetProperty("edits").EnumerateArray())
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

        yield return Case("tools.node.bash-results-match-upstream", () => WithToolDir(async cwd =>
        {
            var env = new LocalExecutionEnv(cwd);
            var tool = CodingTools.CreateBashTool();
            foreach (var upstream in ToolsFixture().GetProperty("bash").EnumerateArray())
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
                if (error is null) Equal(null, result!.Content, name + " content");
                var diagnostics = string.Join("|", api.Reported.Select(diagnostic =>
                    $"{diagnostic.Severity.ToString().ToLowerInvariant()} {diagnostic.Code} " +
                    (diagnostic.Message.StartsWith("Full output: ", StringComparison.Ordinal) ? "Full output: <spill>" : diagnostic.Message)));
                Equal(string.Join("|", upstream.GetProperty("diagnostics").EnumerateArray().Select(diagnostic =>
                    $"{diagnostic.GetProperty("severity").GetString()} {OptionalString(diagnostic, "code")} {diagnostic.GetProperty("message").GetString()}")),
                    diagnostics, name + " diagnostics");
            }
        }));
    }
}
