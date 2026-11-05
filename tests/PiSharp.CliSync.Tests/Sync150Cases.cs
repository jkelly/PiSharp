using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Cli.Commands;
using PiSharp.CodingAgent;
using PiSharp.CodingAgent.Resources;
using PiSharp.CodingAgent.ToolSelection;
using PiSharp.Contracts;

// Pi 1.5.0 (authored from the v1.5.0 sources, f9a26365568127461c47a07a37dcb42c140a3ac5): core/prompt-templates.ts parseCommandArgs
// (empty quoted arguments keep their position), core/agent-session.ts _restoreToolsFromTranscript (a resumed session with the default
// tools keeps its branch's loadout, with --tools +name/-name applied; extension tools the branch never mentioned activate; the whole
// branch, its compacted part included, is replayed) and _applyToolLoadout (tools that stay active keep their order), core/sdk.ts
// usesDefaultTools. Authored expectations, never captured from an upstream run.
internal static partial class Program
{
    private static IEnumerable<(string Id, Func<Task> Run)> Sync150Cases() =>
    [
        ("sync150.prompt-template-empty-quoted-arguments", Sync(EmptyQuotedTemplateArguments)),
        ("sync150.resume-keeps-branch-tools-with-default-selection", ResumeKeepsBranchTools),
        ("sync150.resume-cli-keeps-tools-enabled-in-the-session", ResumeCliKeepsSessionTools),
        ("sync150.resume-activates-unmentioned-extension-tools-over-the-whole-branch", ResumeActivatesNewExtensionTools),
        ("sync150.active-tools-keep-their-order", ActiveToolsKeepOrder),
    ];

    // prompt-templates.ts parseCommandArgs (#10867): "" and '' are kept as empty arguments, so later positions do not shift and a
    // positional default applies to the empty one.
    private static void EmptyQuotedTemplateArguments()
    {
        Names(["", " "], PromptTemplateExpander.ParseCommandArgs("\"\" \" \""), "empty and blank quoted arguments");
        Names(["a", "", "c"], PromptTemplateExpander.ParseCommandArgs("a '' c"), "empty single-quoted argument in the middle");
        Names(["a", ""], PromptTemplateExpander.ParseCommandArgs("a \"\""), "trailing empty double-quoted argument");
        Names(["a", "b"], PromptTemplateExpander.ParseCommandArgs("  a   b  "), "unquoted whitespace still separates");
        var review = new PromptTemplate("review", "test", "Review $2. Focus: ${1:-correctness}.");
        Equal("Review src/index.ts. Focus: correctness.", PromptTemplateExpander.ExpandPromptTemplate("/review \"\" src/index.ts", [review]), "double quotes");
        Equal("Review src/index.ts. Focus: correctness.", PromptTemplateExpander.ExpandPromptTemplate("/review '' src/index.ts", [review]), "single quotes");
    }

    private static SessionRegisteredTool Builtin(string name) => Tool(name) with { IsExtension = false };

    /// <summary>The loadout the session file records: every system message replayed (pi-ai getCurrentTools).</summary>
    private static string[] FileLoadout(string path)
    {
        var tools = new List<string>();
        foreach (var line in File.ReadAllLines(path))
        {
            var record = JsonDocument.Parse(line).RootElement;
            if (!record.TryGetProperty("message", out var message) || message.GetProperty("role").GetString() != "system") continue;
            foreach (var name in SystemTools(message, "toolsRemoved")) tools.Remove(name);
            foreach (var name in SystemTools(message, "toolsAdded")) if (!tools.Contains(name)) tools.Add(name);
        }
        return [.. tools];
    }

    private static int SystemRecords(PersistentAgentSession session) => session.Snapshot.Log.Entries.Count(entry =>
        entry.WireBody.Value.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object && message.GetProperty("role").GetString() == "system");

    private static async Task ResumeKeepsBranchTools()
    {
        ImmutableArray<SessionRegisteredTool> builtins = [Builtin("read"), Builtin("bash"), Builtin("edit"), Builtin("write"), Builtin("grep")];
        // The user turned grep on during the session (ctx.setActiveTools, /tools): the branch records it.
        var path = await RecordedSession(builtins, ["read", "bash", "edit", "write"], ["read", "bash", "edit", "write", "grep"]);
        try
        {
            async Task<string[]> Resume(AllowedToolSelection selection, string what)
            {
                var before = FileBytes(path);
                await using var reopened = await Reopen(path, RestoredRegistry(builtins).RetainToolSelection(selection));
                Check(reopened.PendingToolNames.IsEmpty, what + ": a registered tool became pending");
                Check(FileBytes(path).SequenceEqual(before), what + ": opening the session wrote to its file");
                return [.. reopened.GetActiveTools()];
            }
            // No --tools: the default tools would be read,bash,edit,write; the recorded loadout wins, grep included.
            Names(["read", "bash", "edit", "write", "grep"], await Resume(AllowedToolSelection.Create(), "default tools"), "default selection restores the branch");
            // defaultTools from settings is still the default selection.
            Names(["read", "bash", "edit", "write", "grep"], await Resume(AllowedToolSelection.Create(configuredDefaults: ["read"]), "defaultTools"),
                "defaultTools restores the branch");
            // --tools +name/-name entries apply to the restored loadout; the kept tools take the initial order.
            Names(["read", "edit", "write", "grep"], await Resume(AllowedToolSelection.Create(["-bash"]), "-bash"), "-bash removes from the restored loadout");
            Names(["read", "bash", "edit", "write"], await Resume(AllowedToolSelection.Create(["-grep"]), "-grep"), "-grep removes the session's tool");
            // Plain names, --no-builtin-tools and --no-tools are explicit selections that replace it.
            Names(["read", "bash"], await Resume(AllowedToolSelection.Create(["read", "bash"]), "--tools read,bash"), "--tools names replace the branch");
            Names([], await Resume(AllowedToolSelection.Create(noTools: NoToolsMode.Builtin), "--no-builtin-tools"), "--no-builtin-tools replaces the branch");
            Names([], await Resume(AllowedToolSelection.Create(noTools: NoToolsMode.All), "--no-tools"), "--no-tools replaces the branch");

            // The restored loadout equals the recorded one, so the next prompt records no tool change.
            await using var resumed = await Reopen(path, RestoredRegistry(builtins).RetainToolSelection(AllowedToolSelection.Create()));
            var records = SystemRecords(resumed);
            await resumed.PromptAsync(SettledUser("go")); await resumed.WaitForIdleAsync();
            Equal(records, SystemRecords(resumed), "the prompt recorded a tool change");
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); }

        // A branch that records no loadout keeps the initial names (`if (systemMessages.length === 0) return`).
        var directory = Temp("sync150-empty-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var emptyPath = Path.Combine(directory, "session.jsonl");
        await File.WriteAllTextAsync(emptyPath, JsonSerializer.Serialize(new { type = "session", version = 3, id = "sync150-empty",
            timestamp = "2026-10-08T00:00:00.000Z", cwd = directory }) + "\n", new UTF8Encoding(false));
        try
        {
            await using var reopened = await PersistentAgentSession.OpenWithRegistryAsync(emptyPath, RestoredRegistry(builtins).RetainToolSelection(AllowedToolSelection.Create()),
                () => 7, () => "sync150-" + Guid.NewGuid().ToString("N"), fallbackModel: SettledModel);
            Names(["read", "bash", "edit", "write"], reopened.GetActiveTools(), "no recorded loadout keeps the default tools");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    // The CLI resume path (session prompt on an existing file, as pi --session/--continue): without --tools the tools turned on in
    // the session stay on; +name/-name apply on top; plain names replace them.
    private static async Task ResumeCliKeepsSessionTools()
    {
        var root = Temp("sync150-resume-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var session = Path.Combine(root, "session.jsonl"); var script = Path.Combine(root, "script.json");
            await File.WriteAllTextAsync(script, JsonSerializer.Serialize(new { schemaVersion = 1, turns = new object[] { CompletionsTextTurn("ok", ["go"]) } }),
                new UTF8Encoding(false));
            Equal("0", await Run(["session", "create", "--session", session, "--workspace", root, "--offline-api", "openai-completions", "--tools", "read"]), "create");
            Names(["read"], FileLoadout(session), "created with read only");
            async Task Prompt(params string[] flags) =>
                Equal("0", await Run(["session", "prompt", "--session", session, "--workspace", root, "--offline-api", "openai-completions",
                    "--offline-script", script, "--message", "go", .. flags]), "prompt " + string.Join(" ", flags));
            // The defaults (read, edit and write here) would replace the session's loadout before 1.5.0.
            await Prompt();
            Names(["read"], FileLoadout(session), "resume with the default tools keeps the recorded loadout");
            await Prompt("--tools", "+write");
            Names(["read", "write"], FileLoadout(session), "+write applies to the restored loadout");
            await Prompt();
            Names(["read", "write"], FileLoadout(session), "the next resume keeps the tool +write added");
            await Prompt("--tools", "read,edit");
            Names(["read", "edit"], FileLoadout(session), "plain --tools names replace the loadout");
        }
        finally { Directory.Delete(root, recursive: true); }

        static async Task<string> Run(string[] args)
        {
            using var output = new StringWriter(); using var errors = new StringWriter();
            var code = await SessionCommands.RunAsync(args, output, errors);
            return code + (code == 0 ? "" : " " + errors + " " + output.ToString()[..Math.Min(4000, output.ToString().Length)]);
        }
    }

    private sealed class AuthoredSummary : ISessionSummaryGenerator
    {
        public ValueTask<SessionGeneratedSummary> GenerateAsync(PiSharp.Sessions.Compaction.SessionSummaryRequest request, CancellationToken token = default) =>
            ValueTask.FromResult(new SessionGeneratedSummary("authored summary", TokenUsage.Zero));
    }

    private static async Task ResumeActivatesNewExtensionTools()
    {
        ImmutableArray<SessionRegisteredTool> start = [Builtin("read"), Tool("ext_kept"), Tool("ext_removed")];
        var (session, directory) = await PendingSession(start);
        var path = Path.Combine(directory, "session.jsonl");
        try
        {
            await using (session)
            {
                await Recorded(session, ["read", "ext_kept", "ext_removed"]);
                // The user turns ext_removed off; then the history is compacted, so the projected context no longer holds that record.
                await Recorded(session, ["read", "ext_kept"]);
                await Recorded(session, ["read", "ext_kept"]);
                var receipt = await session.CompactAsync(session.Snapshot.Log.Header.Id, new(new(KeepRecentTokens: 1), ContextWindow: 128_000), new AuthoredSummary());
                Check(receipt is not null, "the session was not compacted");
                Check(!session.Snapshot.Context.LlmMessages.Any(message => message.Role == "system" &&
                    message.WireBody!.Value.TryGetProperty("toolsRemoved", out _)), "the compacted context still records the removal");
            }
            // Reopened where extensions registered more tools since: ext_new activates (registered after the branch recorded its
            // loadout), ext_hidden_by_default (defaultActive: false), the deferred one and the built-in grep do not, and ext_removed,
            // removed before the compaction, stays off.
            ImmutableArray<SessionRegisteredTool> later = [Builtin("read"), Builtin("grep"), Tool("ext_kept"), Tool("ext_removed"), Tool("ext_new"),
                Tool("ext_inactive") with { DefaultActive = false }, Tool("ext_deferred", ToolExposure.Deferred)];
            var before = FileBytes(path);
            var reopened = await Reopen(path, RestoredRegistry(later));
            await using var owner = new ReplaceableAgentSession(reopened, (_, _) => throw new InvalidOperationException("No replacement expected."));
            Names(["read", "ext_kept", "ext_new"], reopened.GetActiveTools(), "resume adds the unmentioned extension tool only");
            Check(FileBytes(path).SequenceEqual(before), "opening the session wrote to its file");
            var count = reopened.Snapshot.Log.Entries.Length; var leaf = reopened.Snapshot.Context.LeafId;
            await reopened.PromptAsync(SettledUser("go")); await reopened.WaitForIdleAsync();
            var record = RecordedAtPrompt(reopened, count, leaf);
            Names(["ext_new"], SystemTools(record, "toolsAdded"), "the next prompt records the activated extension tool");
            Names([], SystemTools(record, "toolsRemoved"), "the next prompt removes nothing");
        }
        finally { Directory.Delete(directory, recursive: true); }

        // /tree: navigating to a branch adds extension tools registered after that branch recorded its loadout.
        (session, directory) = await PendingSession([Builtin("read"), Tool("ext_kept")]);
        try
        {
            await Recorded(session, ["read", "ext_kept"]); var withKept = session.Snapshot.Context.LeafId!;
            await Recorded(session, ["read"]);
            await Publish(session, [Builtin("read"), Tool("ext_kept"), Tool("ext_new")], restoring: false);
            await using var owner = new ReplaceableAgentSession(session, (_, _) => throw new InvalidOperationException("No replacement expected."));
            Names(["read"], session.GetActiveTools(), "the publication keeps the active names");
            await NavigateTo(owner, withKept);
            Names(["read", "ext_kept", "ext_new"], session.GetActiveTools(), "navigation restores the branch and adds the new extension tool");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    // _applyToolLoadout (#11495): the active tools are a set, so setting the same tools in another order changes nothing and records
    // nothing; new tools are appended after the kept ones.
    private static async Task ActiveToolsKeepOrder()
    {
        var requests = new List<ChatRequest>();
        var directory = Temp("keep-order-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory); var ids = 0;
        var header = new PiSharp.Sessions.Serialization.SessionEntryCodec().Parse(JsonSerializer.Serialize(new { type = "session", version = 3, id = "keep-order-header",
            timestamp = "2026-10-08T00:00:00.000Z", cwd = directory }));
        var registry = new SessionRuntimeRegistry([new(SettledModel, new CapturingTransport(requests))], [Builtin("read"), Builtin("bash"), Builtin("grep")], new Deny());
        var session = await PersistentAgentSession.CreateAsync(Path.Combine(directory, "session.jsonl"), header, registry, SettledModel,
            () => 7, () => "keep-order-entry-" + Interlocked.Increment(ref ids));
        try
        {
            await Recorded(session, ["read", "bash"]);
            var records = SystemRecords(session);
            await session.SetActiveToolsAsync(["bash", "read"]);
            Names(["read", "bash"], session.GetActiveTools(), "reordered selection keeps the order");
            Equal(records, SystemRecords(session), "the selection wrote a record");
            await session.PromptAsync(SettledUser("again")); await session.WaitForIdleAsync();
            Equal(records, SystemRecords(session), "the reordered selection recorded a tool change");
            Names(["read", "bash"], DeclaredTools(requests[^1]), "request tool order");
            await session.SetActiveToolsAsync(["grep", "bash", "read"]);
            Names(["read", "bash", "grep"], session.GetActiveTools(), "a new tool is appended");
            var activation = session.ScheduleToolActivation(["grep", "read"]);
            Names(["read", "grep"], activation.Names, "scheduled activation keeps the order");
        }
        finally { await session.DisposeAsync(); Directory.Delete(directory, recursive: true); }
    }
}
