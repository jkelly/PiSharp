using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Contracts;
using PiSharp.Contracts.ModelOperations;
using PiSharp.Extensions;
using PiSharp.Extensions.Facade.Context;
using PiSharp.Extensions.Runtime.Facade.Context;

// Owner decision 10: native C# extensions in the Pi entry are discovered like Pi extensions (global and project extension folders,
// settings, packages, -e), gated only by project trust, without approval or preflight documents, on every platform, and share one
// registry and session with the Node extensions. The extension below is this test assembly's own type: each case copies the assembly
// into an extension folder with a pisharp-extension.json manifest, and the run loads it into its own load context.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> NativeCases() =>
    [
        ("native.project-folder-extension-loads-when-trusted", NativeProjectTrusted),
        ("native.project-folder-extension-skipped-when-untrusted", NativeProjectUntrusted),
        ("native.cli-extension-runs-without-node", NativeWithoutNode),
        ("native.shares-the-session-with-node-extensions", NativeWithNode),
        ("native.reload-loads-native-extensions-again", NativeReload),
        ("native.command-named-like-another-extensions-becomes-name-n", NativeDuplicateCommand),
        ("native.classifier-and-image-provider-reaches-node-extensions", NativeProviderFromNode),
        ("native.classifier-provider-reaches-native-model-registry-and-codemode", NativeProviderFromNativeAndCodemode),
        ("native.unregister-and-reload-remove-the-provider", NativeProviderRemoved),
        ("native.llama-command-in-rpc-mode-notifies", NativeLlamaRpcNotify),
        ("native.classifier-provider-without-api-key-needs-a-credential", NativeProviderWithoutKey),
        ("native.classifier-and-image-callbacks-run-under-the-owner-callback-lease", NativeProviderCallbackLease),
    ];

    // extensions/llama/index.ts: outside interactive mode the built-in /llama only warns through ctx.ui.notify (an RPC
    // extension_ui_request); nothing reaches the model.
    private static async Task NativeLlamaRpcNotify()
    {
        using var sandbox = NativeSandbox("native-llama-rpc");
        NativeExtensionFolder(Path.Combine(sandbox.Cwd, ".pi", "extensions", "native-hello"));
        var (code, records, stderr) = await RunRpc(sandbox, [.. Model], ["""{"id":"l","type":"prompt","message":"/llama"}"""], (record, _) => IsResponse(record, "l"));
        Equal(0, code, "exit; " + stderr);
        var notify = records.SingleOrDefault(record => record["type"]?.GetValue<string>() == "extension_ui_request" && record["method"]?.GetValue<string>() == "notify");
        Check(notify?["message"]?.GetValue<string>() == "/llama is available in interactive mode" && notify["notifyType"]?.GetValue<string>() == "warning",
            "notify: " + string.Join("|", records.Select(record => record.ToJsonString())));
        Check(records.Single(record => IsResponse(record, "l"))["success"]?.GetValue<bool>() == true, "handled");
        Equal(0, sandbox.Requests.Count, "no model request");
    }

    private const string NativeProviderVariable = "PISHARP_EXTENSION_PARITY_NATIVE_PROVIDER";

    private static Sandbox NativeProviderSandbox(string name, bool node)
    {
        var sandbox = node ? NodeSandbox(name) : new Sandbox(name);
        Environment.SetEnvironmentVariable(NativeLogVariable, Path.Combine(sandbox.Root, "native.log"));
        Environment.SetEnvironmentVariable(NativeProviderVariable, "1");
        sandbox.Vars["NATIVE_ACME_KEY"] = "native-secret";
        NativeExtensionFolder(Path.Combine(sandbox.Cwd, ".pi", "extensions", "native-hello"));
        return sandbox;
    }

    // types.ts ProviderConfig.classifiers/images from a native C# extension (IExtensionModelOperationProviderRegistry): a Node extension's
    // ctx.modelRegistry lists the models and classify/generateImages run the native implementation with the provider's resolved key.
    private static async Task NativeProviderFromNode()
    {
        using var sandbox = NativeProviderSandbox("native-provider-node", node: true);
        try
        {
            var extension = sandbox.Write(Path.Combine(sandbox.Cwd, "judge.ts"), Probe + """
                export default function (pi: any) {
                  pi.registerTool({
                    name: "judge", label: "Judge", description: "Classify and paint", parameters: { type: "object", properties: {} },
                    async execute(id: string, params: any, signal: any, onUpdate: any, ctx: any) {
                      const judge = ctx.modelRegistry.getModelOfType("classifier", "native-acme", "judge");
                      const painter = ctx.modelRegistry.getModelOfType("image", "native-acme", "painter");
                      log("models", judge?.api ?? null, judge?.baseUrl ?? null, judge?.contextWindow ?? null, painter?.output ?? null,
                        ctx.modelRegistry.getModelsOfType("classifier", "native-acme").map((m: any) => m.id));
                      const verdict = await ctx.modelRegistry.classify(judge, { state: { text: "hello" }, questions: { safe: { type: "bool", instructions: "Is it safe?", criteria: { true: "safe", false: "unsafe" } } } }, { temperature: 2 });
                      const image = await ctx.modelRegistry.generateImages(painter, { input: [{ type: "text", text: "a cat" }] });
                      log("results", verdict.stopReason, verdict.answers?.safe?.probability ?? null, verdict.errorMessage ?? null, image.stopReason, image.output?.[0]?.mimeType ?? null, image.errorMessage ?? null);
                      return { content: [{ type: "text", text: "judged" }], details: {} };
                    },
                  });
                }
                """);
            sandbox.Respond = (_, index) => index == 0 ? AnthropicToolCall("judge", new { }) : AnthropicText("done");
            var (code, _, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "-e", extension, "judge it"]);
            Equal(0, code, "exit; " + stderr);
            var log = LogLines(sandbox);
            Equal("""["models","native-classify","https://acme.invalid/v1",8000,["image"],["judge"]]""", log.Single(line => line.StartsWith("[\"models\"", StringComparison.Ordinal)), "models listed");
            Equal("""["results","stop",0.75,null,"stop","image/png",null]""", log.Single(line => line.StartsWith("[\"results\"", StringComparison.Ordinal)), "results");
            var native = NativeLog(sandbox);
            Check(native.Contains("classify judge native-acme native-classify https://acme.invalid/v1 safe key=native-secret temperature=2"), "the native classifier ran: " + string.Join("|", native));
            Check(native.Contains("images painter native-acme a cat key=native-secret"), "the native image provider ran: " + string.Join("|", native));
        }
        finally { Environment.SetEnvironmentVariable(NativeProviderVariable, null); }
    }

    // The native extension's own ctx.modelRegistry and codemode's `models` reach the run's registry (built-in providers and the
    // extensions' providers), not only the built-in catalogs.
    private static async Task NativeProviderFromNativeAndCodemode()
    {
        using var sandbox = NativeProviderSandbox("native-provider-native", node: false);
        try
        {
            var (code, _, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "/native-judge"]);
            Equal(0, code, "exit; " + stderr);
            Check(NativeLog(sandbox).Contains("judge stop 0.75"), "the native command classified through ctx.modelRegistry: " + string.Join("|", NativeLog(sandbox)));
            Check(NativeLog(sandbox).Contains("available judge"), "getAvailableOfType lists it: " + string.Join("|", NativeLog(sandbox)));
            const string script = "const judge = await models.getModelOfType(\"classifier\", \"native-acme\", \"judge\");\n" +
                "const verdict = await models.classify(judge, { state: { text: \"hi\" }, questions: { safe: { type: \"bool\", instructions: \"Safe?\", criteria: { true: \"yes\", false: \"no\" } } } });\n" +
                "return \"verdict \" + verdict.answers.safe.probability;";
            sandbox.Respond = (_, index) => index == 0 ? AnthropicToolCall("codemode", new { code = script }) : AnthropicText("done");
            (code, _, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "--tools", "read,codemode", "use codemode"]);
            Equal(0, code, "codemode exit; " + stderr);
            var result = ToolResultText(sandbox.Requests[^1]);
            Check(result.Contains("verdict 0.75", StringComparison.Ordinal), "codemode classified with the native provider: " + result);
        }
        finally { Environment.SetEnvironmentVariable(NativeProviderVariable, null); }
    }

    // provider-composer.ts composeApiKeyAuth: a native provider registered without ApiKey (and no built-in provider of its id) resolves
    // only a request key or a stored api_key credential; without one classify fails before the implementation runs.
    private static async Task NativeProviderWithoutKey()
    {
        using var sandbox = NativeProviderSandbox("native-provider-keyless", node: false);
        Environment.SetEnvironmentVariable(NativeProviderVariable, "keyless");
        try
        {
            var (code, _, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "/native-judge"]);
            Equal(0, code, "exit; " + stderr);
            var log = NativeLog(sandbox);
            Check(log.Contains("judge error Provider is not configured: native-acme") && !log.Any(line => line.StartsWith("classify ", StringComparison.Ordinal)) &&
                !log.Contains("available judge"), "unconfigured: " + string.Join("|", log));
            File.Delete(Path.Combine(sandbox.Root, "native.log"));
            File.WriteAllText(Path.Combine(sandbox.AgentDir, "auth.json"), """{"native-acme":{"type":"api_key","key":"stored-secret"}}""");
            (code, _, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "/native-judge"]);
            Equal(0, code, "stored exit; " + stderr);
            log = NativeLog(sandbox);
            Check(log.Contains("judge stop 0.75") && log.Contains("available judge") && log.Any(line => line.StartsWith("classify judge ", StringComparison.Ordinal) &&
                line.EndsWith(" key=stored-secret", StringComparison.Ordinal)), "the stored credential configures it: " + string.Join("|", log));
        }
        finally { Environment.SetEnvironmentVariable(NativeProviderVariable, null); }
    }

    // A native provider's classify/generateImages run as the owner's callbacks, as the registry runs its tools: under the owner's callback
    // lease (its disposal waits for them), inside its callback frame (it cannot dispose itself from one), cancelled with its lifetime, and
    // refused once the owner generation retired.
    private static async Task NativeProviderCallbackLease()
    {
        var registry = new PiSharp.Extensions.Runtime.ExtensionRegistry();
        var host = new CapturingModelOperationProviderHost();
        registry.ModelOperationProviderHost = host;
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reentrant = "";
        IExtensionRegistry? own = null;
        var extension = new InlineProviderExtension(scope =>
        {
            own = scope;
            ((IExtensionModelOperationProviderRegistry)scope).RegisterModelOperationProvider(new("lease-acme")
            {
                Models = [JsonData.Parse("""{"id":"judge","type":"classifier","api":"lease-classify"}"""), JsonData.Parse("""{"id":"painter","type":"image","api":"lease-images"}""")],
                Classifiers = ImmutableDictionary<string, ExtensionClassifierImplementation>.Empty.Add("lease-classify", async (model, context, options, token) =>
                {
                    try { await ((IAsyncDisposable)own!).DisposeAsync(); reentrant = "disposed"; }
                    catch (PiSharp.Extensions.Runtime.ExtensionRegistrationException error) { reentrant = error.Failure.ToString(); }
                    entered.SetResult(token);
                    await release.Task;
                    return new ClassifierResult("lease-classify", "lease-acme", "judge", [], ModelOperationStopReason.Stop, 1);
                }),
                Images = ImmutableDictionary<string, ExtensionImagesImplementation>.Empty.Add("lease-images", (model, context, options, token) =>
                    Task.FromResult(new AssistantImages("lease-images", "lease-acme", "painter", [], ModelOperationStopReason.Stop, 2)))
            });
        });
        var owner = await registry.ActivateAsync("lease-owner", extension);
        var provider = host.Providers.Single();
        var model = JsonData.Parse("""{"id":"judge","provider":"lease-acme","api":"lease-classify","type":"classifier"}""");
        var context = new ClassifierContext(JsonData.Parse("""{"text":"hi"}"""), [new("safe", new ClassifierBoolQuestion("Safe?", "yes", "no"))]);
        var images = await provider.Images["lease-images"](JsonData.Parse("""{"id":"painter","provider":"lease-acme","api":"lease-images","type":"image"}"""),
            new ImagesContext([new ImagesTextBlock("a cat")]), new(), CancellationToken.None);
        Equal(ModelOperationStopReason.Stop, images.StopReason, "an image callback of the active owner runs");
        var running = provider.Classifiers["lease-classify"](model, context, new(), CancellationToken.None);
        var token = await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Equal("ReentrantDisposal", reentrant, "the callback runs in the owner's callback frame");
        Check(token.CanBeCanceled && !token.IsCancellationRequested, "the callback token follows the extension lifetime");
        var disposal = owner.DisposeAsync().AsTask();
        Check(await WaitFor(() => token.IsCancellationRequested), "disposal cancels the running callback's token");
        await Task.Delay(200);
        Check(!disposal.IsCompleted, "disposal waits for the leased callback");
        release.SetResult();
        Equal(ModelOperationStopReason.Stop, (await running).StopReason, "the callback finishes");
        await disposal.WaitAsync(TimeSpan.FromSeconds(30));
        Check(host.Providers.IsEmpty, "the retired owner's providers left the host");
        var refused = await ThrowsAsync<PiSharp.Extensions.Runtime.ExtensionRegistrationException>(() => provider.Classifiers["lease-classify"](model, context, new(), CancellationToken.None));
        Equal(PiSharp.Extensions.Runtime.ExtensionRegistrationFailure.StaleSnapshot, refused.Failure, "a retired owner's classifier is refused");
        await ThrowsAsync<PiSharp.Extensions.Runtime.ExtensionRegistrationException>(() => provider.Images["lease-images"](model, new ImagesContext([new ImagesTextBlock("x")]), new(), CancellationToken.None));
    }

    private static async Task<bool> WaitFor(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++) await Task.Delay(25);
        return condition();
    }

    private sealed class CapturingModelOperationProviderHost : IExtensionModelOperationProviderHost
    {
        private ImmutableList<(string Owner, ExtensionModelOperationProvider Provider)> entries = [];
        public ImmutableArray<ExtensionModelOperationProvider> Providers => [.. entries.Select(entry => entry.Provider)];
        public void Register(string ownerId, ExtensionModelOperationProvider provider) => ImmutableInterlocked.Update(ref entries, list => list.Add((ownerId, provider)));
        public void Unregister(string ownerId, string name) => ImmutableInterlocked.Update(ref entries, list => list.RemoveAll(entry => entry.Owner == ownerId && entry.Provider.Name == name));
        public void UnregisterOwner(string ownerId) => ImmutableInterlocked.Update(ref entries, list => list.RemoveAll(entry => entry.Owner == ownerId));
    }

    private sealed class InlineProviderExtension(Action<IExtensionRegistry> initialize) : IPiSharpExtension
    {
        public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken cancellationToken) { initialize(registry); return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // model-runtime.ts unregisterProvider and reload: the provider leaves ctx.modelRegistry.
    private static async Task NativeProviderRemoved()
    {
        using var sandbox = NativeProviderSandbox("native-provider-removed", node: false);
        try
        {
            var (code, _, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "/native-drop"]);
            Equal(0, code, "exit; " + stderr);
            Names(["probe found", "probe missing"], NativeLog(sandbox).Where(line => line.StartsWith("probe ", StringComparison.Ordinal)), "unregisterProvider");
            File.Delete(Path.Combine(sandbox.Root, "native.log"));
            var steps = new Queue<string>(["""{"id":"reload","type":"prompt","message":"/reload"}""", """{"id":"after","type":"prompt","message":"/native-probe"}"""]);
            var (rpcCode, _, rpcStderr) = await RunRpc(sandbox, [.. Model], ["""{"id":"before","type":"prompt","message":"/native-probe"}"""],
                (record, _) => IsResponse(record, "after"),
                react: (record, push) =>
                {
                    if (IsResponse(record, "before")) Environment.SetEnvironmentVariable(NativeProviderVariable, null);
                    if (steps.Count > 0 && (IsResponse(record, "before") || IsResponse(record, "reload"))) push(steps.Dequeue());
                });
            Equal(0, rpcCode, "rpc exit; " + rpcStderr);
            Names(["probe found", "probe missing"], NativeLog(sandbox).Where(line => line.StartsWith("probe ", StringComparison.Ordinal)), "the reloaded generation registers none");
        }
        finally { Environment.SetEnvironmentVariable(NativeProviderVariable, null); }
    }

    // runner.ts resolveRegisteredCommands over every extension (Node and native, load order): a name two extensions register is
    // invoked as name:1 and name:2; neither extension fails to load.
    private static async Task NativeDuplicateCommand()
    {
        using var sandbox = NodeSandbox("native-duplicate-command");
        Environment.SetEnvironmentVariable(NativeLogVariable, Path.Combine(sandbox.Root, "native.log"));
        NativeExtensionFolder(Path.Combine(sandbox.Cwd, ".pi", "extensions", "native-hello"));
        var node = sandbox.Write(Path.Combine(sandbox.Cwd, "node.ts"), Probe + """
            export default function (pi: any) {
              pi.registerCommand("native-hello", { description: "Node hello", handler: async (args: string) => log("node", args) });
              pi.registerCommand("list", { description: "List", handler: async () =>
                log("commands", pi.getCommands().filter((c: any) => c.source === "extension").map((c: any) => c.name)) });
            }
            """);
        var (code, _, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "-e", node, "/list"]);
        Equal(0, code, "exit; " + stderr);
        var commands = LogLines(sandbox).Single(line => line.StartsWith("[\"commands\"", StringComparison.Ordinal));
        Check(commands.Contains("\"native-hello:1\"", StringComparison.Ordinal) && commands.Contains("\"native-hello:2\"", StringComparison.Ordinal) &&
            !commands.Contains("\"native-hello\"", StringComparison.Ordinal), "invocation names: " + commands + " stderr " + stderr);
        Check(!stderr.Contains("native-hello", StringComparison.Ordinal), "no load failure: " + stderr);
        File.Delete(Path.Combine(sandbox.Cwd, "probe.log"));
        foreach (var (suffix, text) in new[] { ("1", "first"), ("2", "second") })
        {
            (code, _, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "-e", node, $"/native-hello:{suffix} {text}"]);
            Equal(0, code, "exit; " + stderr);
        }
        var native = NativeLog(sandbox).Where(line => line.StartsWith("command ", StringComparison.Ordinal)).ToArray();
        var nodeRuns = LogLines(sandbox).Where(line => line.StartsWith("[\"node\"", StringComparison.Ordinal)).ToArray();
        Check(native.Length == 1 && nodeRuns.Length == 1 && (native[0] == "command first" ? nodeRuns[0] == """["node","second"]""" :
            native[0] == "command second" && nodeRuns[0] == """["node","first"]"""), "each name:N runs one extension: native " +
            string.Join("|", native) + " node " + string.Join("|", nodeRuns));
        Equal(0, sandbox.Requests.Count, "commands handled without a model request");
    }

    private const string NativeLogVariable = "PISHARP_EXTENSION_PARITY_NATIVE_LOG";

    /// <summary>Writes an extension folder: a copy of this assembly and its manifest. Returns the manifest path.</summary>
    private static string NativeExtensionFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        var assembly = typeof(Program).Assembly.Location;
        File.Copy(assembly, Path.Combine(folder, "ParityNative.dll"), overwrite: true);
        var manifest = Path.Combine(folder, "pisharp-extension.json");
        File.WriteAllText(manifest, JsonSerializer.Serialize(new { assembly = "ParityNative.dll", entryType = typeof(ParityNativeExtension).FullName }));
        return manifest;
    }

    private static string[] NativeLog(Sandbox sandbox)
    {
        var path = Path.Combine(sandbox.Root, "native.log");
        return File.Exists(path) ? File.ReadAllLines(path) : [];
    }

    private static Sandbox NativeSandbox(string name, bool trusted = true)
    {
        var sandbox = new Sandbox(name, trusted);
        Environment.SetEnvironmentVariable(NativeLogVariable, Path.Combine(sandbox.Root, "native.log"));
        return sandbox;
    }

    // package-manager.ts collectAutoExtensionEntries over <cwd>/.pi/extensions (a trusted project): the extension's folder holds its
    // manifest; its command and tool join the session.
    private static async Task NativeProjectTrusted()
    {
        using var sandbox = NativeSandbox("native-trusted");
        NativeExtensionFolder(Path.Combine(sandbox.Cwd, ".pi", "extensions", "native-hello"));
        var (code, _, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "/native-hello world"]);
        Equal(0, code, "exit; " + stderr);
        Check(NativeLog(sandbox).Contains("command world"), "the native command ran: " + string.Join("|", NativeLog(sandbox)));
        Check(sandbox.Requests.Count == 0, "the command was handled without a model request");
        sandbox.Respond = (_, index) => index == 0 ? AnthropicToolCall("native_echo", new { text = "ping" }) : AnthropicText("done");
        (code, _, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "use it"]);
        Equal(0, code, "exit; " + stderr);
        Check(sandbox.Requests[0].Json.GetProperty("tools").EnumerateArray().Any(tool => tool.GetProperty("name").GetString() == "native_echo"), "native tool declared");
        Equal("native echo: ping", ToolResultText(sandbox.Requests[1]), "native tool executed");
        Check(NativeLog(sandbox).Contains("start startup"), "session_start observed with reason startup: " + string.Join("|", NativeLog(sandbox)));
    }

    // trust-manager.ts: project extensions (native ones too) load only for a trusted project.
    private static async Task NativeProjectUntrusted()
    {
        using var sandbox = NativeSandbox("native-untrusted", trusted: false);
        NativeExtensionFolder(Path.Combine(sandbox.Cwd, ".pi", "extensions", "native-hello"));
        var (code, _, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "--no-approve", "/native-hello world"]);
        Equal(0, code, "exit; " + stderr);
        Equal(0, NativeLog(sandbox).Length, "nothing of the untrusted project's extension ran");
        Check(sandbox.Requests.Single().Body!.Contains("/native-hello world", StringComparison.Ordinal), "the text went to the model as a prompt");
        (code, _, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "--approve", "/native-hello again"]);
        Equal(0, code, "approved exit; " + stderr);
        Check(NativeLog(sandbox).Contains("command again"), "loaded once the project is trusted");
    }

    // cli/args.ts -e: a native extension needs no Node.js (a run whose PATH has no node).
    private static async Task NativeWithoutNode()
    {
        using var sandbox = NativeSandbox("native-no-node");
        var manifest = NativeExtensionFolder(Path.Combine(sandbox.Root, "elsewhere", "native"));
        sandbox.Vars["PATH"] = Path.Combine(sandbox.Root, "empty-bin");
        sandbox.Vars["PISHARP_NODE"] = Path.Combine(sandbox.Root, "no-node");
        var (code, _, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "-e", manifest, "/native-hello bare"]);
        Equal(0, code, "exit; " + stderr);
        Check(NativeLog(sandbox).Contains("command bare"), "the native command ran without Node: " + stderr);
    }

    // One runner: the Node and native extensions are owners of one registry; getCommands lists both, pi.events reaches both.
    private static async Task NativeWithNode()
    {
        using var sandbox = NodeSandbox("native-with-node");
        Environment.SetEnvironmentVariable(NativeLogVariable, Path.Combine(sandbox.Root, "native.log"));
        NativeExtensionFolder(Path.Combine(sandbox.Cwd, ".pi", "extensions", "native-hello"));
        var node = sandbox.Write(Path.Combine(sandbox.Cwd, "node.ts"), Probe + """
            export default function (pi: any) {
              pi.events.on("native-pong", (data: any) => log("pong", data.text));
              pi.registerCommand("list", { description: "List", handler: async () => {
                log("commands", pi.getCommands().filter((c: any) => c.source === "extension").map((c: any) => c.name));
                pi.events.emit("native-ping", { text: "hi" });
                await new Promise((resolve) => setTimeout(resolve, 300));
              } });
            }
            """);
        var (code, _, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "-e", node, "/list"]);
        Equal(0, code, "exit; " + stderr);
        var log = LogLines(sandbox);
        Check(log.Any(line => line.StartsWith("[\"commands\"", StringComparison.Ordinal) && line.Contains("\"list\"", StringComparison.Ordinal) &&
            line.Contains("\"native-hello\"", StringComparison.Ordinal)), "both extensions' commands: " + string.Join("|", log));
        Check(log.Contains("""["pong","hi"]"""), "the native extension answered on pi.events: " + string.Join("|", log) + " native " + string.Join("|", NativeLog(sandbox)));
    }

    // agent-session.ts reload: native extensions load again (session_shutdown reload, then session_start reload with the new instance).
    private static async Task NativeReload()
    {
        using var sandbox = NativeSandbox("native-reload");
        NativeExtensionFolder(Path.Combine(sandbox.Cwd, ".pi", "extensions", "native-hello"));
        var steps = new Queue<string>(["""{"id":"reload","type":"prompt","message":"/reload"}""", """{"id":"after","type":"prompt","message":"/native-hello after"}""",
            """{"id":"use","type":"prompt","message":"use it"}"""]);
        sandbox.Respond = (_, index) => index == 0 ? AnthropicToolCall("native_echo", new { text = "reloaded" }) : AnthropicText("done");
        var (code, records, stderr) = await RunRpc(sandbox, [.. Model], ["""{"id":"before","type":"prompt","message":"/native-hello before"}"""],
            (record, _) => record["type"]?.GetValue<string>() == "agent_settled",
            react: (record, push) => { if (steps.Count > 0 && (IsResponse(record, "before") || IsResponse(record, "reload") || IsResponse(record, "after"))) push(steps.Dequeue()); });
        Equal(0, code, "exit; " + stderr);
        Check(sandbox.Requests[0].Json.GetProperty("tools").EnumerateArray().Count(tool => tool.GetProperty("name").GetString() == "native_echo") == 1,
            "the reloaded extension's tool is declared once: " + sandbox.Requests[0].Body);
        Equal("native echo: reloaded", ToolResultText(sandbox.Requests[1]), "the reloaded extension's tool executed");
        var log = NativeLog(sandbox);
        Names(["start startup", "command before", "shutdown reload", "start reload", "command after"],
            log.Where(line => !line.StartsWith("shutdown quit", StringComparison.Ordinal)), "native lifecycle across the reload; records " +
            string.Join("\n", records.Select(item => item.ToJsonString()).Where(text => text.Contains("response", StringComparison.Ordinal) || text.Contains("error", StringComparison.Ordinal))));
    }
}

/// <summary>The native C# extension the cases load (from a copy of this assembly in its own load context).</summary>
public sealed class ParityNativeExtension : IPiSharpExtension
{
    private static void Log(string line)
    {
        if (Environment.GetEnvironmentVariable("PISHARP_EXTENSION_PARITY_NATIVE_LOG") is { } path) File.AppendAllText(path, line + "\n");
    }

    public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken cancellationToken)
    {
        registry.RegisterCommand(new("native-hello", "native-hello", "Native hello", (arguments, context, token) =>
        {
            Log("command " + (arguments.Value.ValueKind == JsonValueKind.String ? arguments.Value.GetString() : ""));
            return ValueTask.CompletedTask;
        }));
        registry.RegisterTool(new("native-echo", "native_echo", "Echoes text", JsonData.Parse("""{"type":"object","properties":{"text":{"type":"string"}}}"""),
            (arguments, context, token) => ValueTask.FromResult(JsonData.Parse(JsonSerializer.Serialize(new
            {
                content = new[] { new { type = "text", text = "native echo: " + arguments.Value.GetProperty("text").GetString() } }, details = new { }
            })))));
        registry.Observe(new("on-session-start", "session_start", (observation, context, token) =>
        {
            Log("start " + observation.Value.GetProperty("reason").GetString());
            return ValueTask.CompletedTask;
        }));
        registry.Observe(new("on-session-shutdown", "session_shutdown", (observation, context, token) =>
        { Log("shutdown " + observation.Value.GetProperty("reason").GetString()); return ValueTask.CompletedTask; }));
        if (registry is IExtensionEventBusRegistry bus)
            bus.Events.On("native-ping", data => bus.Events.Emit("native-pong", data));
        if (Environment.GetEnvironmentVariable("PISHARP_EXTENSION_PARITY_NATIVE_PROVIDER") is "1" or "keyless" && registry is IExtensionModelOperationProviderRegistry providers)
        {
            providers.RegisterModelOperationProvider(new("native-acme")
            {
                DisplayName = "Native Acme", BaseUrl = "https://acme.invalid/v1",
                ApiKey = Environment.GetEnvironmentVariable("PISHARP_EXTENSION_PARITY_NATIVE_PROVIDER") == "keyless" ? null : "NATIVE_ACME_KEY",
                Models = [JsonData.Parse("""{"id":"judge","name":"Judge","type":"classifier","api":"native-classify","input":["text"],"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0},"contextWindow":8000}"""),
                    JsonData.Parse("""{"id":"painter","name":"Painter","type":"image","api":"native-images","input":["text"],"output":["image"],"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0}}""")],
                Classifiers = ImmutableDictionary<string, ExtensionClassifierImplementation>.Empty.Add("native-classify", (model, context, options, token) =>
                {
                    var json = model.Value;
                    Log($"classify {json.GetProperty("id").GetString()} {json.GetProperty("provider").GetString()} {json.GetProperty("api").GetString()} {json.GetProperty("baseUrl").GetString()} " +
                        $"{string.Join(",", context.Questions.Select(question => question.Key))} key={options.ApiKey}" + (options.Temperature is { } temperature ? $" temperature={temperature}" : ""));
                    return Task.FromResult(new ClassifierResult("native-classify", "native-acme", json.GetProperty("id").GetString()!,
                        [new("safe", new ClassifierBoolAnswer(0.75))], ModelOperationStopReason.Stop, 1));
                }),
                Images = ImmutableDictionary<string, ExtensionImagesImplementation>.Empty.Add("native-images", (model, context, options, token) =>
                {
                    Log($"images {model.Value.GetProperty("id").GetString()} {model.Value.GetProperty("provider").GetString()} {((ImagesTextBlock)context.Input[0]).Text} key={options.ApiKey}");
                    return Task.FromResult(new AssistantImages("native-images", "native-acme", "painter", [new ImageContent("aGk=", "image/png")], ModelOperationStopReason.Stop, 2));
                })
            });
            registry.RegisterCommand(ExtensionCommandFacade.CreateCommand("native-judge", "native-judge", "Classify natively", async (arguments, facade, token) =>
            {
                var models = (IExtensionModelOperationsFacade)facade;
                var judge = models.GetModelOfType(ModelType.Classifier, "native-acme", "judge") ?? throw new InvalidOperationException("judge missing");
                var verdict = await models.ClassifyAsync(judge, new ClassifierContext(JsonData.Parse("""{"text":"hi"}"""),
                    [new("safe", new ClassifierBoolQuestion("Safe?", "yes", "no"))]), cancellationToken: token);
                Log($"judge {ModelOperationJson.StopReasonName(verdict.StopReason)} {(verdict.GetAnswer("safe") as ClassifierBoolAnswer)?.Probability}{verdict.ErrorMessage}");
                foreach (var available in await models.GetAvailableOfTypeAsync(ModelType.Classifier, "native-acme", token))
                    Log("available " + available.Value.GetProperty("id").GetString());
            }));
            registry.RegisterCommand(ExtensionCommandFacade.CreateCommand("native-drop", "native-drop", "Unregister the provider", (arguments, facade, token) =>
            {
                var models = (IExtensionModelOperationsFacade)facade;
                Log("probe " + (models.GetModelOfType(ModelType.Classifier, "native-acme", "judge") is null ? "missing" : "found"));
                providers.UnregisterModelOperationProvider("native-acme");
                Log("probe " + (models.GetModelOfType(ModelType.Classifier, "native-acme", "judge") is null ? "missing" : "found"));
                return ValueTask.CompletedTask;
            }));
        }
        registry.RegisterCommand(ExtensionCommandFacade.CreateCommand("native-probe", "native-probe", "Probe the provider", (arguments, facade, token) =>
        {
            Log("probe " + (((IExtensionModelOperationsFacade)facade).GetModelOfType(ModelType.Classifier, "native-acme", "judge") is null ? "missing" : "found"));
            return ValueTask.CompletedTask;
        }));
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
