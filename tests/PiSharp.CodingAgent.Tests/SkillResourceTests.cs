using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.Cli.Skills;
using PiSharp.CodingAgent;
using PiSharp.CodingAgent.Resources;
using PiSharp.CodingAgent.Resources.Skills;
using PiSharp.Contracts;
using PiSharp.Rpc;
using PiSharp.Rpc.Protocol;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

internal static class SkillResourceTests
{
    public const string Prefix = "skill resource ";
    private static readonly ModelDescriptor Model = new("skill-fixture", "openai-responses", "fixture");
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        (Prefix + "standard YAML metadata folded literal strict true and warning-only names", Metadata),
        (Prefix + "directory root stop recursion collisions duplicates and denied reads", Discovery),
        (Prefix + "physical and aggregate byte entry depth bounds isolate failures", Bounds),
        (Prefix + "selected fresh body block XML descriptions hidden skill and unknown commands", Expansion),
        (Prefix + "raw commands handlers skills templates image and opt-out order", Ordering),
        (Prefix + "loader cancellation joins its original borrowed read before returning", LoadCancellation),
        (Prefix + "actual session cancellation joins expansion without lazy materialization", SessionCancellation),
        (Prefix + "actual RPC catalog queues system descriptions and selected content reach model", Rpc),
        (Prefix + "failed selected read preserves raw input diagnostic and no unrelated reads", FailedExpansion)
        ,(Prefix + "malformed fresh YAML returns original command and valid edits retain captured identity", FreshYaml)
        ,(Prefix + "failed directory enumeration conservatively consumes remaining aggregate allowance", FailedEnumeration)
    ];
    private static string P(params string[] parts)
    { string[] paths = [Path.GetTempPath(), "pisharp-skill-injected", .. parts]; return Path.Combine(paths); }
    private static void Equal<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}; observed {actual}."); }
    private static string Document(string name, string description, string body = "Use these instructions", bool disabled = false) =>
        "---\n" + JsonSerializer.Serialize(new Dictionary<string, object> { ["name"] = name, ["description"] = description,
            ["disable-model-invocation"] = disabled }) + "\n---\n" + body;
    // This borrowed decoder is deliberately JSON-only for filesystem/ordering fixtures. Standard YAML is tested separately.
    private static SkillMetadata JsonMetadata(string text)
    {
        var value = JsonData.Parse(text).Value;
        return new(value.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null,
            value.TryGetProperty("description", out var description) && description.ValueKind == JsonValueKind.String ? description.GetString() : null,
            value.TryGetProperty("disable-model-invocation", out var disabled) && disabled.ValueKind == JsonValueKind.True);
    }
    private sealed class Files : ISkillResourceFileSystem
    {
        internal Dictionary<string, byte[]> Text { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, ImmutableArray<PromptTemplateDirectoryEntry>> Directories { get; } = new(StringComparer.Ordinal);
        internal List<string> Reads { get; } = []; internal int Stats, Lists;
        internal Func<string, int, CancellationToken, ValueTask<byte[]>>? Pending;
        internal void Put(string path, string text) => Text[path] = Encoding.UTF8.GetBytes(text);
        public PromptTemplateFileKind Stat(string path) { Stats++; return Directories.ContainsKey(path) ? PromptTemplateFileKind.Directory : Text.ContainsKey(path) ? PromptTemplateFileKind.File : PromptTemplateFileKind.Missing; }
        public ImmutableArray<PromptTemplateDirectoryEntry> ReadDirectory(string path, int maximumEntries)
        { Lists++; var values = Directories[path]; if (values.Length > maximumEntries) throw new IOException("Entry bound"); return values; }
        public ValueTask<byte[]> ReadFileAsync(string path, int maximumBytes, CancellationToken token)
        {
            Reads.Add(path); if (Pending is not null) return Pending(path, maximumBytes, token);
            token.ThrowIfCancellationRequested(); var bytes = Text[path]; if (bytes.Length > maximumBytes) throw new IOException("Byte bound");
            return ValueTask.FromResult(bytes.ToArray());
        }
    }
    private static async Task Metadata()
    {
        var fs = new Files(); var path = P("fallback", "SKILL.md");
        fs.Put(path, "\uFEFF---\r\ndescription: |\r\n  café & details\r\n  second line\r\ndisable-model-invocation: true\r\n---\r\n body ");
        var set = await SkillResourceSet.LoadAsync([new(path)], SkillFrontendDecoder.Decode, fs);
        Equal("fallback", set.Skills.Single().Name); Equal("café & details\nsecond line\n", set.Skills[0].Description);
        Equal(true, set.Skills[0].DisableModelInvocation); Equal("", set.FormatForPrompt());
        fs.Put(path, "---\nname: Bad--NAME\ndescription: >\n  first line\n  second line\ndisable-model-invocation: 'true'\n---\nbody");
        set = await SkillResourceSet.LoadAsync([new(path)], SkillFrontendDecoder.Decode, fs);
        Equal("Bad--NAME", set.Skills.Single().Name); Equal("first line second line\n", set.Skills[0].Description);
        Equal(false, set.Skills[0].DisableModelInvocation); Equal("InvalidName", set.Diagnostics.Single().Code);
        fs.Put(path, "---\ndescription: 123\n---\nbody");
        set = await SkillResourceSet.LoadAsync([new(path)], SkillFrontendDecoder.Decode, fs);
        Equal(0, set.Skills.Length); Equal("DescriptionRequired", set.Diagnostics.Single().Code);
        fs.Put(path, "---\ndescription: [\n---\nbody");
        set = await SkillResourceSet.LoadAsync([new(path)], SkillFrontendDecoder.Decode, fs); Equal("InvalidFrontmatter", set.Diagnostics.Single().Code);
        fs.Put(path, Document("long", new string('d', 1025)));
        set = await SkillResourceSet.LoadAsync([new(path)], JsonMetadata, fs); Equal(1, set.Skills.Length); Equal("DescriptionTooLong", set.Diagnostics.Single().Code);
    }
    private static PromptTemplateDirectoryEntry Entry(string directory, string name, PromptTemplateFileKind kind) => new(name, Path.Combine(directory, name), kind);
    private static async Task Discovery()
    {
        var fs = new Files(); var root = P("root"); var declared = P("root", "unit"); var nested = P("root", "unit", "nested");
        fs.Directories[root] = [Entry(root, "one.md", PromptTemplateFileKind.File), Entry(root, "README.md", PromptTemplateFileKind.File),
            Entry(root, "unit", PromptTemplateFileKind.Directory), Entry(root, ".hidden", PromptTemplateFileKind.Directory), Entry(root, "node_modules", PromptTemplateFileKind.Directory)];
        fs.Directories[declared] = [Entry(declared, "SKILL.md", PromptTemplateFileKind.File), Entry(declared, "nested", PromptTemplateFileKind.Directory)];
        fs.Directories[nested] = [Entry(nested, "SKILL.md", PromptTemplateFileKind.File)];
        fs.Put(Path.Combine(root, "one.md"), Document("same", "first")); fs.Put(Path.Combine(root, "README.md"), "ordinary document");
        fs.Put(Path.Combine(declared, "SKILL.md"), Document("same", "loser")); fs.Put(Path.Combine(nested, "SKILL.md"), Document("unselected", "hidden"));
        var set = await SkillResourceSet.LoadAsync([new(root), new(Path.Combine(root, "one.md")), new(P("denied"), false)], JsonMetadata, fs);
        Equal("first", set.Skills.Single().Description); Equal(3, fs.Reads.Count); Equal(false, fs.Reads.Contains(Path.Combine(nested, "SKILL.md")));
        Equal("NameCollision", set.Diagnostics[0].Code); Equal(Path.Combine(root, "one.md"), set.Diagnostics[0].WinnerPath);
        Equal("DeniedSelection", set.Diagnostics[1].Code); Equal(2, fs.Lists);
        var denied = new Files(); await SkillResourceSet.LoadAsync([new(P("denied"), false)], JsonMetadata, denied);
        Equal(0, denied.Stats); Equal(0, denied.Lists); Equal(0, denied.Reads.Count);
    }
    private static async Task Bounds()
    {
        using var fixture = new DirectoryFixture(); var physical = new SystemSkillResourceFileSystem();
        File.WriteAllBytes(fixture.Skill, new byte[65_537]);
        var set = await SkillResourceSet.LoadAsync([new(fixture.Skill)], JsonMetadata, physical); Equal("ReadFailed", set.Diagnostics.Single().Code);
        var fs = new Files(); var first = P("a.md"); var second = P("b.md"); fs.Put(first, new string('x', 65)); fs.Put(second, Document("b", "description"));
        set = await SkillResourceSet.LoadAsync([new(first), new(second)], JsonMetadata, fs, new(MaximumFileBytes: 64, MaximumTotalBytes: 64));
        Equal(1, fs.Reads.Count); Equal("ReadFailed", set.Diagnostics[0].Code); Equal("ReadLimit", set.Diagnostics[1].Code);
        var root = P("entries"); fs.Directories[root] = [Entry(root, "a.md", PromptTemplateFileKind.File), Entry(root, "b.md", PromptTemplateFileKind.File)];
        set = await SkillResourceSet.LoadAsync([new(root)], JsonMetadata, fs, new(MaximumEntries: 1)); Equal("DiscoveryFailed", set.Diagnostics.Single().Code);
        var child = Path.Combine(root, "child"); fs.Directories[root] = [Entry(root, "child", PromptTemplateFileKind.Directory)]; fs.Directories[child] = [];
        set = await SkillResourceSet.LoadAsync([new(root)], JsonMetadata, fs, new(MaximumDepth: 0)); Equal("DepthLimit", set.Diagnostics.Single().Code);
        var priorReads = fs.Reads.Count;
        fs.Directories[root] = [new("outside.md", P("outside.md"), PromptTemplateFileKind.File)];
        set = await SkillResourceSet.LoadAsync([new(root)], JsonMetadata, fs);
        Equal("DiscoveryFailed", set.Diagnostics.Single().Code); Equal(priorReads, fs.Reads.Count);
    }
    private static async Task Expansion()
    {
        var fs = new Files(); var path = P("unit", "SKILL.md"); fs.Put(path, Document("review", "Unicode café <&\"'>"));
        var set = await SkillResourceSet.LoadAsync([new(path)], JsonMetadata, fs);
        Equal(true, set.FormatForPrompt().Contains("Unicode café &lt;&amp;&quot;&apos;&gt;", StringComparison.Ordinal));
        fs.Put(path, Document("ignored-renamed", "updated description", "\uFEFF  new instructions\nsecond line \uFEFF"));
        var expanded = await set.ExpandAsync("/skill:review  focus here \uFEFF"); var block = SkillResourceSet.ParseBlock(expanded)!;
        Equal("review", block.Name); Equal(path, block.Location); Equal("focus here", block.UserMessage);
        Equal("References are relative to " + Path.GetDirectoryName(path) + ".\n\nnew instructions\nsecond line", block.Content);
        Equal("/skill:unknown text", await set.ExpandAsync("/skill:unknown text")); Equal(2, fs.Reads.Count);
        Equal<ParsedSkillBlock?>(null, SkillResourceSet.ParseBlock("prefix " + expanded));
        fs.Put(path, Document("manual", "hidden", "explicit body", true)); set = await SkillResourceSet.LoadAsync([new(path)], JsonMetadata, fs);
        Equal("", set.FormatForPrompt()); Equal(true, (await set.ExpandAsync("/skill:manual")).Contains("explicit body", StringComparison.Ordinal));
    }
    private sealed class Handler(Func<PromptInput, PromptInputDecision> callback) : IPromptInputAdmission
    { public ValueTask<PromptInputDecision> ReduceAsync(PromptInput input, CancellationToken token) { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(callback(input)); } }
    private sealed class Commands : IPromptTemplateCommandAdmission
    { internal bool Handled; public bool IsRegisteredCommand(string text) => Handled; public ValueTask<bool> TryExecuteAsync(string text, CancellationToken token) => ValueTask.FromResult(Handled); }
    private static async Task Ordering()
    {
        var fs = new Files(); var path = P("order", "SKILL.md"); fs.Put(path, Document("review", "description", "/plain body"));
        var binding = await SkillCliBinding.LoadAsync(new([new(path)]), fs, JsonMetadata);
        var images = JsonData.Parse("[{\"type\":\"image\",\"mimeType\":\"image/png\",\"data\":\"AA==\"}]");
        var handler = new Handler(input => { Equal("raw", input.Text); return new(PromptInputAction.Transform, "/skill:review user", images); });
        var admission = binding.AdmissionFor(PromptTemplateInputOperation.Prompt, rawHandlers: handler);
        var decision = await admission.ReduceAsync(new("raw"), default); Equal(PromptInputAction.Transform, decision.Action);
        Equal(true, decision.Text!.StartsWith("<skill name=\"review\"", StringComparison.Ordinal)); Equal(images.ToString(), decision.Images!.ToString());
        Equal(true, decision.Text!.Contains("\n\n/plain body\n</skill>", StringComparison.Ordinal));
        var commands = new Commands { Handled = true }; var reads = fs.Reads.Count;
        admission = binding.AdmissionFor(PromptTemplateInputOperation.Prompt, rawHandlers: new Handler(_ => throw new InvalidOperationException("Handled command ran handlers")), rawCommands: commands);
        Equal(PromptInputAction.Handled, (await admission.ReduceAsync(new("/skill:review"), default)).Action); Equal(reads, fs.Reads.Count);
        var refused = false;
        try { await binding.AdmissionFor(PromptTemplateInputOperation.Steer, rawCommands: commands).ReduceAsync(new("/skill:review"), default); }
        catch (PromptInputAdmissionException error) when (error.Failure == PromptInputAdmissionFailure.InvalidInput) { refused = true; }
        Equal(true, refused); Equal(reads, fs.Reads.Count);
        admission = binding.AdmissionFor(PromptTemplateInputOperation.ExtensionMessage);
        Equal(PromptInputAction.Continue, (await admission.ReduceAsync(new("/skill:review"), default)).Action); Equal(reads, fs.Reads.Count);
        admission = binding.AdmissionFor(PromptTemplateInputOperation.Prompt, expandTemplates: false);
        Equal(PromptInputAction.Continue, (await admission.ReduceAsync(new("/skill:review"), default)).Action); Equal(reads, fs.Reads.Count);
        var templates = new PromptTemplateCatalogSnapshot([new(new("plain", "plain", "template $1"), path, new(path, "local", PromptTemplateSourceScope.Temporary, PromptTemplateSourceOrigin.TopLevel))], []);
        var templateResult = await binding.AdmissionFor(PromptTemplateInputOperation.Prompt, templates).ReduceAsync(new("/plain argument"), default);
        Equal("template argument", templateResult.Text); Equal(reads, fs.Reads.Count);
    }
    private static async Task LoadCancellation()
    {
        var fs = new Files(); var path = P("cancel", "SKILL.md"); fs.Put(path, Document("cancel", "description"));
        using var stop = new CancellationTokenSource(); var entered = Gate(); var release = Gate(); var joined = false;
        fs.Pending = async (_, _, _) => { entered.TrySetResult(); try { await release.Task; return fs.Text[path]; } finally { joined = true; } };
        var original = SkillCliBinding.LoadAsync(new([new(path), new(P("next.md"))]), fs, JsonMetadata, token: stop.Token);
        await CancelAndJoin(original, entered, release, stop); Equal(true, joined); Equal(1, fs.Reads.Count); Equal(1, fs.Stats);
    }
    private static async Task SessionCancellation()
    {
        using var directory = new DirectoryFixture(); var fs = new Files(); fs.Put(directory.Skill, Document("held", "description"));
        var binding = await SkillCliBinding.LoadAsync(new([new(directory.Skill)]), fs, JsonMetadata);
        await using var session = await SessionFixture.Create(directory, binding);
        using var stop = new CancellationTokenSource(); var entered = Gate(); var release = Gate(); var joined = false;
        CancellationToken readToken = default;
        fs.Pending = async (_, _, token) => { readToken = token; entered.TrySetResult(); try { await release.Task; return fs.Text[directory.Skill]; } finally { joined = true; } };
        var before = session.Session.Snapshot.Log.CommittedByteLength;
        var original = session.Session.SubmitInputAsync(new("/skill:held", PromptInputSource.Rpc),
            binding.AdmissionFor(PromptTemplateInputOperation.Prompt), cancellationToken: stop.Token);
        await CancelAndJoin(original, entered, release, stop, () => readToken);
        Equal(true, joined); Equal(false, File.Exists(directory.Session)); Equal(before, session.Session.Snapshot.Log.CommittedByteLength);
        Equal(0, session.Script.Calls); Equal(false, session.Session.Snapshot.IsAdmittingInput);
    }
    private static async Task CancelAndJoin(Task original, TaskCompletionSource entered, TaskCompletionSource release, CancellationTokenSource stop,
        Func<CancellationToken>? expected = null)
    {
        Exception? primary = null, settlement = null;
        try { await Task.WhenAny(entered.Task, original); Equal(true, entered.Task.IsCompletedSuccessfully); stop.Cancel(); Equal(false, original.IsCompleted); }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            release.TrySetResult(); try { await original; }
            catch (Exception error) { settlement = error; if (primary is not null && !ReferenceEquals(primary, error)) primary.Data["OriginalSkillOperationSettlement"] = error; }
        }
        if (settlement is not OperationCanceledException canceled) throw new InvalidOperationException("Original skill operation did not cancel.", settlement);
        Equal(expected?.Invoke() ?? stop.Token, canceled.CancellationToken); Equal(true, canceled.CancellationToken.IsCancellationRequested);
    }
    private static async Task FailedExpansion()
    {
        var fs = new Files(); var path = P("failure", "SKILL.md"); fs.Put(path, Document("fail", "description"));
        var set = await SkillResourceSet.LoadAsync([new(path)], JsonMetadata, fs); var diagnostics = new List<SkillDiagnostic>();
        fs.Pending = (_, _, _) => throw new IOException("private-marker");
        Equal("/skill:fail args", await set.ExpandAsync("/skill:fail args", (diagnostic, _) => { diagnostics.Add(diagnostic); return ValueTask.CompletedTask; }));
        Equal("ExpansionReadFailed", diagnostics.Single().Code); Equal(false, JsonSerializer.Serialize(diagnostics).Contains("private-marker", StringComparison.Ordinal));
        Equal(2, fs.Reads.Count); Equal("/skill:unselected", await set.ExpandAsync("/skill:unselected")); Equal(2, fs.Reads.Count);
    }
    private static async Task FreshYaml()
    {
        var fs = new Files(); var path = P("fresh", "SKILL.md"); fs.Put(path, Document("fresh", "initial", "initial body"));
        var set = await SkillResourceSet.LoadAsync([new(path)], SkillFrontendDecoder.Decode, fs); var errors = new List<SkillDiagnostic>();
        fs.Put(path, "---\ndescription: [\n---\nmalformed instructions must not expand"); const string original = "/skill:fresh exact args ";
        Equal(original, await set.ExpandAsync(original, (error, _) => { errors.Add(error); return ValueTask.CompletedTask; }));
        Equal("ExpansionReadFailed", errors.Single().Code); Equal(path, errors[0].Path); Equal(2, fs.Reads.Count);
        fs.Put(path, Document("renamed", "changed", "new body"));
        var block = SkillResourceSet.ParseBlock(await set.ExpandAsync(original))!;
        Equal("fresh", block.Name); Equal(true, block.Content.EndsWith("new body", StringComparison.Ordinal)); Equal("exact args", block.UserMessage);
    }
    private static async Task FailedEnumeration()
    {
        var fs = new Files(); var first = P("failed-list"); var second = P("unvisited-list");
        fs.Directories[first] = [Entry(first, "one.md", PromptTemplateFileKind.File), Entry(first, "two.md", PromptTemplateFileKind.File), Entry(first, "three.md", PromptTemplateFileKind.File)];
        fs.Directories[second] = [];
        var set = await SkillResourceSet.LoadAsync([new(first), new(second)], JsonMetadata, fs, new(MaximumEntries: 2));
        Equal(1, fs.Lists); Equal(0, fs.Reads.Count); Equal("DiscoveryFailed", set.Diagnostics[0].Code); Equal("EntryLimit", set.Diagnostics[1].Code);
    }
    private static async Task Rpc()
    {
        using var directory = new DirectoryFixture(); File.WriteAllText(directory.Skill, Document("review", "review descriptor", "Follow the selected instructions"));
        var binding = await SkillCliBinding.LoadAsync(new([new(directory.Skill)]));
        await using var fixture = await SessionFixture.Create(directory, binding); using var capture = new Capture();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var rpc = new RpcSessionDispatcher(fixture.Session, new JsonlWriter(capture, ownership: JsonlStreamOwnership.Borrowed), fixture.Clock,
            [new(Model, ModelWire())], sessionOwnership: RpcSessionOwnership.Borrowed,
            inputAdmission: binding.AdmissionFor(PromptTemplateInputOperation.Prompt), extensionCommandCatalog: binding.Commands(),
            inputAdmissionSelector: type => binding.AdmissionFor(type switch { "prompt" => PromptTemplateInputOperation.Prompt,
                "steer" => PromptTemplateInputOperation.Steer, "follow_up" => PromptTemplateInputOperation.FollowUp, _ => throw new ArgumentException("Input type") }));
        await rpc.SubmitAsync(JsonData.Parse("{\"id\":\"commands\",\"type\":\"get_commands\"}"), stop.Token);
        var commands = capture.Frames().Last().Value.GetProperty("data").GetProperty("commands");
        Equal("skill:review", commands.EnumerateArray().Single().GetProperty("name").GetString()); Equal("skill", commands[0].GetProperty("source").GetString());
        Equal(false, File.Exists(directory.Session)); Equal(0, fixture.Script.Calls);
        await rpc.SubmitAsync(JsonData.Parse("{\"id\":\"queue\",\"type\":\"steer\",\"message\":\"/skill:review focus\"}"), stop.Token);
        Equal(true, fixture.Session.GetPendingInputQueueSnapshot().SteeringMessages.Single().WireBody.Value.GetProperty("content")[0].GetProperty("text").GetString()!.StartsWith("<skill", StringComparison.Ordinal));
        Equal(false, File.Exists(directory.Session)); Equal(0, fixture.Script.Calls);
        await rpc.SubmitAsync(JsonData.Parse("{\"id\":\"clear\",\"type\":\"clear_queue\"}"), stop.Token);
        await rpc.SubmitAsync(JsonData.Parse("{\"id\":\"prompt\",\"type\":\"prompt\",\"message\":\"/skill:review focus\"}"), stop.Token);
        while (true) { var record = await capture.Records.Reader.ReadAsync(stop.Token); if (record.Value.GetProperty("type").GetString() == "agent_settled") break; }
        Equal(1, fixture.Script.Calls); Equal(1, fixture.Script.Cleanups); Equal(true, File.Exists(directory.Session));
        var request = fixture.Script.Request!; Equal(true, request.Messages.Any(message => message.Role == "system" && message.WireBody.ToString().Contains("available_skills", StringComparison.Ordinal)));
        var user = request.Messages.Single(message => message.Role == "user").WireBody.Value;
        Equal(true, user.GetProperty("content")[0].GetProperty("text").GetString()!.Contains("Follow the selected instructions", StringComparison.Ordinal));
        Equal(true, fixture.Session.Snapshot.Log.Entries.Any(entry => entry.Kind == SessionEntryKind.Message && entry.WireBody.ToString().Contains("Follow the selected instructions", StringComparison.Ordinal)));
    }
    private static JsonData ModelWire() => JsonData.Parse(JsonSerializer.Serialize(new { id = Model.Id, name = Model.Id, provider = Model.Provider,
        api = Model.Api, baseUrl = "https://offline.invalid", reasoning = false, input = new[] { "text" }, contextWindow = 128000, maxTokens = 1024,
        cost = new { input = 0, output = 0, cacheRead = 0, cacheWrite = 0 } }));
    private sealed class Script : IChatTransport
    {
        internal int Calls, Cleanups; internal ChatRequest? Request;
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); Calls++; Request = request;
            var final = new AssistantMessage(Model.Api, Model.Provider, Model.Id, 0, [new TextContent("done")], TokenUsage.Zero, StopReason.Stop);
            try { await Task.CompletedTask; yield return new StreamStarted(final with { Content = [], StopReason = StopReason.Pending });
                yield return new TextStarted(0, new("")); yield return new TextEnded(0, "done"); yield return new StreamDone(final.StopReason, final); }
            finally { Cleanups++; }
        }
    }
    private sealed class NoPolicy : IToolActionPolicy
    { public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token) => throw new InvalidOperationException("Skill resource acquired a tool grant"); }
    private sealed class SessionFixture : IAsyncDisposable
    {
        internal PersistentAgentSession Session = null!; internal Script Script { get; } = new(); internal SessionStorageBackend Backend = null!;
        private long ticks; private int id; internal long Clock() => Interlocked.Increment(ref ticks);
        internal static async Task<SessionFixture> Create(DirectoryFixture directory, SkillCliBinding binding)
        {
            var fixture = new SessionFixture(); fixture.Backend = new(directory.Root, SessionStorageMode.LazyLocal);
            var registry = new SessionRuntimeRegistry([new(Model, fixture.Script)], [], new NoPolicy());
            try
            {
                fixture.Session = await PersistentAgentSession.CreateAsync(directory.Session, new SessionEntryCodec().Parse(JsonSerializer.Serialize(new
                    { type = "session", version = 3, id = "skill-session", timestamp = "2026-10-05T00:00:00.000Z", cwd = directory.Root })),
                    registry, Model, fixture.Clock, () => "skill-entry-" + Interlocked.Increment(ref fixture.id),
                    new(SessionLogStoreOptions: new(StorageFactory: fixture.Backend)));
                await fixture.Session.ConfigureAsync(new(SystemMessage: new("system", binding.AddToSystemMessage(JsonData.Parse("{\"role\":\"system\",\"content\":\"base\",\"timestamp\":0,\"toolsAdded\":[]}")))));
                return fixture;
            }
            catch { if (fixture.Session is not null) await fixture.Session.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync() { await Session.DisposeAsync(); Equal(0, Backend.ActiveWriterCount); }
    }
    private sealed class Capture : MemoryStream
    {
        private readonly object gate = new(); internal Channel<JsonData> Records { get; } = Channel.CreateUnbounded<JsonData>();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); lock (gate) { Write(buffer.Span); Records.Writer.TryWrite(JsonData.Parse(Encoding.UTF8.GetString(buffer.Span).TrimEnd('\n'))); } return ValueTask.CompletedTask; }
        internal JsonData[] Frames() { lock (gate) return Encoding.UTF8.GetString(ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonData.Parse(line)).ToArray(); }
    }
    private sealed class DirectoryFixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "pisharp-skill-owned-" + Guid.NewGuid().ToString("N"));
        internal string Skill => Path.Combine(Root, "SKILL.md"); internal string Session => Path.Combine(Root, "session.jsonl");
        internal DirectoryFixture() => Directory.CreateDirectory(Root);
        public void Dispose()
        {
            var target = Path.GetFullPath(Root); var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!target.StartsWith(parent, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
                !Path.GetFileName(target).StartsWith("pisharp-skill-owned-", StringComparison.Ordinal)) throw new InvalidOperationException("Invalid owned root.");
            Directory.Delete(target, true);
        }
    }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
