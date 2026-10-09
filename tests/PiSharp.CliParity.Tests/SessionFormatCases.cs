// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/session-manager.ts.
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiSharp.Cli.Pi;

// session-manager.ts writes every entry as `${JSON.stringify(entry)}\n`. The SessionFormat/*.scenario.json goldens hold the files the
// installed @earendil-works/pi-coding-agent@1.1.0 (`pi --mode rpc --offline`) wrote for scripted conversations against an offline fake
// provider (the capture root is <ROOT>, Pi's package directory <PIPKG>). The same RPC steps and provider responses are replayed through
// PiSharp's Pi entry and both files are compared entry by entry after only ids, timestamps and durations are replaced by placeholders
// (their formats are checked first; a parent session path keeps only its file name format). Every line must also be byte-identical to
// JSON.stringify of its own value. Each scenario holds its runs (one `pi --mode rpc` process each over the same home and project, the
// RPC commands sent one at a time after the previous one settled), the provider responses in request order, the project files and the
// models.json/settings.json it ran with. Paths are compared with / separators (the goldens were captured on Windows). A scenario with
// "extensions" loads a TypeScript extension and needs the Pi runtime the ExtensionParity suite installs; it is skipped without it.
// SESSFMT_DIR replays another golden directory and SESSFMT_DUMP writes PiSharp's files and RPC output for inspection.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> SessionFormatCases()
    {
        var directory = Environment.GetEnvironmentVariable("SESSFMT_DIR") is { Length: > 0 } custom ? custom : Path.Combine(AppContext.BaseDirectory, "SessionFormat");
        if (!Directory.Exists(directory)) return [];
        return Directory.GetFiles(directory, "*.scenario.json").Order(StringComparer.Ordinal)
            .Select(file => ("session-format." + Path.GetFileName(file)[..^".scenario.json".Length], (Func<Task>)(() => SessionFormatScenario(file))));
    }

    private static async Task SessionFormatScenario(string file)
    {
        var scenario = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        var name = scenario["name"]!.GetValue<string>();
        using var sandbox = new Sandbox("sessfmt-" + name);
        foreach (var (relative, text) in scenario["files"]!.AsObject()) sandbox.Write(Path.Combine(sandbox.Cwd, relative), text!.GetValue<string>());
        // The capture pointed the built-in providers at the fake server with baseUrl-only overrides; here the injected handler is the
        // fake server, so those overrides are dropped and custom providers keep a placeholder address.
        var models = scenario["models"]!.DeepClone().AsObject();
        foreach (var (provider, config) in models["providers"]!.AsObject().ToArray())
            if (config is JsonObject only && only.Count == 1 && only.ContainsKey("baseUrl")) models["providers"]!.AsObject().Remove(provider);
        sandbox.Write(Path.Combine(sandbox.AgentDir, "models.json"), models.ToJsonString().Replace("{base}", "http://127.0.0.1:9", StringComparison.Ordinal));
        if (scenario["settings"] is JsonObject settings) sandbox.Write(Path.Combine(sandbox.AgentDir, "settings.json"), settings.ToJsonString());
        sandbox.Vars["OPENAI_API_KEY"] = "sk-test-key";
        if (scenario["extensions"]?.GetValue<bool>() == true)
        {
            // TypeScript extensions run on the Pi 1.1.0 packages the ExtensionParity suite installs once per machine (PiNodeRuntime).
            var runtime = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "pisharp-ext-parity-pi-runtime", PiSharp.Cli.Extensions.Pi.PiNodeRuntime.PiVersion);
            if (!Directory.Exists(Path.Combine(runtime, "node_modules"))) throw new SkipException("Needs the Pi extension runtime the ExtensionParity suite installs: " + runtime);
            sandbox.Vars["PISHARP_PI_RUNTIME_DIR"] = runtime;
            foreach (var variable in new[] { "PATH", "PATHEXT", "SystemRoot", "PISHARP_NODE" })
                if (Environment.GetEnvironmentVariable(variable) is { } value) sandbox.Vars[variable] = value;
        }
        var responses = scenario["responses"]!.AsArray();
        sandbox.Respond = (_, index) => index < responses.Count
            ? new HttpResponseMessage((HttpStatusCode)responses[index]!["status"]!.GetValue<int>())
            { Content = new StringContent(responses[index]!["body"]!.GetValue<string>(), Encoding.UTF8, responses[index]!["contentType"]!.GetValue<string>()) }
            : AnthropicError(500, "no scripted response " + index);
        // Each run is one `pi --mode rpc` process over the same home, agent directory and project (a later run may --continue).
        var problems = new List<string>(); var next = 0; var events = new List<string>();
        foreach (var run in scenario["runs"]!.AsArray())
        {
            var steps = run!["steps"]!.AsArray().Select(step => step!.AsObject()).ToArray();
            var first = next; var input = new ScriptedInput();
            void Send()
            {
                if (next - first >= steps.Length) { input.Complete(); return; }
                var step = steps[next - first];
                var command = new JsonObject { ["id"] = (++next).ToString(System.Globalization.CultureInfo.InvariantCulture) };
                foreach (var (key, value) in step) command[key] = value?.DeepClone();
                input.Send(command.ToJsonString());
            }
            using var output = new LineOutput(line =>
            {
                lock (events) events.Add(line.ToJsonString());
                var type = line["type"]?.GetValue<string>();
                var prompt = next > first && steps[next - first - 1]["type"]!.GetValue<string>() == "prompt";
                var current = next.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var answered = type == "response" && line["id"]?.GetValue<string>() == current;
                if (answered && line["success"]?.GetValue<bool>() == false) lock (problems) problems.Add("step " + current + ": " + line["error"]);
                // A prompt the session handles without a run (an extension command) settles with its response.
                var settled = answered && (line["success"]?.GetValue<bool>() == false || line["data"]?["disposition"]?.GetValue<string>() == "handled");
                if (prompt ? type == "agent_settled" || settled : answered) Send();
            });
            using var stdout = new StringWriter(); using var stderr = new StringWriter();
            var host = sandbox.Host(stdout, stderr, null, rpcInput: input, rpcOutput: output) with { StdoutIsTty = false };
            Send();
            var code = await PiCommand.RunAsync(["--mode", "rpc", "--offline", .. run["args"]!.AsArray().Select(arg => arg!.GetValue<string>())], host, CancellationToken.None);
            if (Environment.GetEnvironmentVariable("SESSFMT_DUMP") is { Length: > 0 } eventsDump)
            { Directory.CreateDirectory(eventsDump); File.WriteAllLines(Path.Combine(eventsDump, name + ".events.jsonl"), events); File.WriteAllText(Path.Combine(eventsDump, name + ".stderr.txt"), stderr.ToString()); }
            Equal(0, code, "rpc exit; " + stderr);
        }
        var expected = scenario["expected"]!.AsArray().Select(lines => lines!.AsArray().Select(line => line!.GetValue<string>()).ToArray()).ToArray();
        // Files in creation order: by their header's timestamp, then name.
        var files = sandbox.SessionFiles().OrderBy(path => JsonNode.Parse(File.ReadLines(path).First())!["timestamp"]!.GetValue<string>(), StringComparer.Ordinal)
            .ThenBy(path => path, StringComparer.Ordinal).ToArray();
        var actual = files.Select(path => File.ReadAllText(path)).ToArray();
        if (Environment.GetEnvironmentVariable("SESSFMT_DUMP") is { Length: > 0 } dump)
        {
            Directory.CreateDirectory(dump);
            for (var index = 0; index < actual.Length; index++)
                File.WriteAllText(Path.Combine(dump, name + (actual.Length > 1 ? "." + index : "") + ".jsonl"), Scrub(actual[index], sandbox.Root), new UTF8Encoding(false));
        }
        Equal(expected.Length, actual.Length, "session file count");
        for (var index = 0; index < actual.Length; index++)
        {
            Check(actual[index].EndsWith('\n'), "every entry line ends with LF");
            var lines = actual[index].Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
                if (PiSharp.AI.StreamingJson.JsonReformat(line) != line) problems.Add("not JSON.stringify(entry): " + line);
            var want = NormalizeSession(expected[index], "pi", problems);
            var have = NormalizeSession(lines.Select(line => Scrub(line, sandbox.Root)), "pisharp", problems);
            for (var line = 0; line < Math.Max(want.Count, have.Count); line++)
            {
                var left = line < want.Count ? want[line] : "<missing>"; var right = line < have.Count ? have[line] : "<missing>";
                if (left != right) problems.Add($"file {index} line {line + 1}:\n  pi:      {left}\n  pisharp: {right}");
            }
        }
        Check(problems.Count == 0, string.Join("\n", problems));
    }

    /// <summary>The sandbox root becomes &lt;ROOT&gt; and the application directory (where the docs the system prompt names live) Pi's
    /// package directory, as in the goldens.</summary>
    private static string Scrub(string text, string root)
    {
        static string Escaped(string value) => JsonSerializer.Serialize(value, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping })[1..^1];
        var application = Path.Combine(root, "app");
        var package = Path.Combine("<PIPKG>", "@earendil-works", "pi-coding-agent");
        // The codemode description names the docs next to the running binaries.
        var binaries = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return text.Replace(Escaped(application), Escaped(package), StringComparison.Ordinal).Replace(Escaped(binaries), Escaped(package), StringComparison.Ordinal)
            .Replace(Escaped(root), "<ROOT>", StringComparison.Ordinal).Replace(root.Replace('\\', '/'), "<ROOT>", StringComparison.Ordinal);
    }

    private static readonly Regex IsoTimestamp = new(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$", RegexOptions.CultureInvariant);
    private static readonly Regex EntryId = new("^[0-9a-f]{8}$", RegexOptions.CultureInvariant);
    private static readonly Regex SessionFileName = new(@"^\d{4}-\d{2}-\d{2}T\d{2}-\d{2}-\d{2}-\d{3}Z_[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}\.jsonl$", RegexOptions.CultureInvariant);
    private static readonly Regex SessionId = new("^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$", RegexOptions.CultureInvariant);

    /// <summary>Replaces ids (consistently, in order of appearance), ISO and epoch timestamps and durations after checking their formats.</summary>
    private static List<string> NormalizeSession(IEnumerable<string> lines, string side, List<string> problems)
    {
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        var result = new List<string>();
        var options = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        string Id(string value)
        {
            if (!EntryId.IsMatch(value)) problems.Add($"{side}: entry id '{value}' is not 8 hex characters");
            if (!ids.TryGetValue(value, out var mapped)) ids[value] = mapped = "<id" + (ids.Count + 1) + ">";
            return mapped;
        }
        void Walk(JsonNode? node, bool top, bool header)
        {
            if (node is JsonArray array) { foreach (var item in array) Walk(item, false, false); return; }
            if (node is not JsonObject value) return;
            foreach (var (key, child) in value.ToArray())
            {
                if (child is JsonValue scalar && scalar.GetValueKind() == JsonValueKind.String)
                {
                    var text = scalar.GetValue<string>();
                    if (key == "timestamp")
                    {
                        if (!IsoTimestamp.IsMatch(text)) problems.Add($"{side}: timestamp '{text}' is not Date.toISOString()");
                        value[key] = "<iso>";
                    }
                    else if (top && key == "id" && header)
                    {
                        if (!SessionId.IsMatch(text) && !EntryId.IsMatch(text)) problems.Add($"{side}: session id '{text}' is not a uuidv7");
                        value[key] = "<session>";
                    }
                    else if (top && header && key == "parentSession")
                    {
                        // <agent dir>/sessions/--<encoded cwd>--/<timestamp>_<uuidv7>.jsonl: the encoded directory holds the sandbox path.
                        var name = text.Replace('\\', '/');
                        if (!name.StartsWith("<ROOT>/home/.pi/agent/sessions/--", StringComparison.Ordinal) ||
                            !SessionFileName.IsMatch(name[(name.LastIndexOf('/') + 1)..])) problems.Add($"{side}: parentSession '{text}' is not a session file path");
                        value[key] = "<parent session file>";
                    }
                    else if ((top && key is "id" or "parentId" or "targetId" or "firstKeptEntryId" or "fromId") || (key == "fromId" && ids.ContainsKey(text)))
                        value[key] = Id(text);
                }
                else if (child is JsonValue number && number.GetValueKind() == JsonValueKind.Number && key is "timestamp" or "durationMs")
                {
                    if (!long.TryParse(number.ToJsonString(), out _)) problems.Add($"{side}: {key} {number.ToJsonString()} is not an integer");
                    value[key] = "<" + key + ">";
                }
                else Walk(child, false, false);
            }
        }
        foreach (var line in lines)
        {
            var node = JsonNode.Parse(line)!;
            Walk(node, true, node["type"]?.GetValue<string>() == "session");
            // The goldens were captured on Windows: path separators are compared as /.
            result.Add(node.ToJsonString(options).Replace(@"\\", "/", StringComparison.Ordinal));
        }
        return result;
    }
}
