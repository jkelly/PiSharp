// Authored for PiSharp (no upstream test file): support for the questions.* and plan.* cases of the PiSharp-only ask_user_question tool
// and plan mode (decision 0005, "PiSharp-only additions: ask_user_question and plan mode"). Every wait is on a condition: a conversation
// view revision, a submission's settlement, or the Harness closing; none sleeps.
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Env;
using PiSharp.Durable.Sqlite;
using PiSharp.Durable.Storage;
using PiSharp.Durable.Storage.Jsonl;
using PiSharp.Durable.Tools;
using PiSharp.Durable.Tools.Env;
using static ChatTests;
using static Program;
using static SessionTests;

/// <summary>Shared setup of the interaction cases.</summary>
internal static class InteractionTests
{
    internal static readonly DurableContext Ctx = DurableContext.Background;
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <summary>The backends the flow cases run on: name prefix and a fresh reopenable location (each call of the returned function opens
    /// the same store). Memory keeps its records across a Harness close through ControlledStorage.</summary>
    internal static IEnumerable<(string Prefix, Func<Func<IStorage>> Location)> Backends() =>
    [
        ("", () =>
        {
            var storage = new ControlledStorage(new MemoryStorage()) { KeepOpenForReopen = true };
            return () => storage;
        }),
        ("jsonl.", () =>
        {
            var directory = Temp("interaction-jsonl");
            return () => JsonlStorage.OpenAsync(directory, Ctx).GetAwaiter().GetResult();
        }),
        ("sqlite.", () =>
        {
            var path = Path.Combine(Temp("interaction-sqlite"), "session.sqlite");
            return () => SqliteStorage.OpenAsync(path).GetAwaiter().GetResult();
        }),
    ];

    /// <summary>A registry with the coding tools, user-interaction and plan-mode; plan-mode is left out of the default selection.</summary>
    internal static ChatSetup NewSetup()
    {
        var setup = Setup();
        setup.Registry.Install(CodingTools.CreateCodingTools());
        setup.Registry.Install(UserInteraction.CreateExtension());
        setup.Registry.Install(PlanMode.CreateExtension());
        DefaultSelection(setup);
        return setup;
    }

    /// <summary>Every installed extension but plan-mode, as a host's default selection.</summary>
    internal static void DefaultSelection(ChatSetup setup) =>
        setup.Extensions = [.. setup.Registry.Snapshot().Installed().Where(extension => extension.Name != PlanMode.ExtensionName)];

    /// <summary>A project directory (the conversation's cwd), a plan directory beside it, and an outside directory.</summary>
    internal sealed class Dirs
    {
        public Dirs(string name)
        {
            var root = Temp("interaction-" + name);
            Project = Path.Combine(root, "project");
            Plans = Path.Combine(root, "plans", "1");
            Outside = Path.Combine(root, "outside");
            Directory.CreateDirectory(Project);
            Directory.CreateDirectory(Plans);
            Directory.CreateDirectory(Outside);
            Env = new LocalExecutionEnv(Project);
        }

        public string Project { get; }
        public string Plans { get; }
        public string Outside { get; }
        public LocalExecutionEnv Env { get; }
    }

    /// <summary>Opens a Harness over <paramref name="open"/> with <paramref name="dirs"/>' environment and resumes it; the first open sets the
    /// root's cwd to the project directory.</summary>
    internal static async Task<OpenedChat> Open(Func<IStorage> open, ChatSetup setup, Dirs dirs, bool first = true)
    {
        var opened = await OpenChat(open(), setup, (_, _) => Task.FromResult<IExecutionEnv?>(dirs.Env));
        if (first) await opened.Root.ConfigureAsync(new AgentChange { Cwd = dirs.Project }, Ctx);
        opened.Harness.Resume();
        return opened;
    }

    /// <summary>A faux model that answers each request from the current turn: the last user text and the tool results since it.</summary>
    internal static void Script(ChatSetup setup, Func<string, IReadOnlyList<JsonObjectValue>, JsonObjectValue> respond)
    {
        FauxStep step = FauxStep.From(request =>
        {
            var messages = request.Messages.OfType<JsonObjectValue>().ToList();
            var last = messages.FindLastIndex(message => S(message["role"]) == "user");
            var results = messages.Skip(last + 1).Where(message => S(message["role"]) == "toolResult").ToList();
            return respond(last < 0 ? "" : TextOf(messages[last]) ?? "", results);
        });
        setup.Faux.SetResponses([.. Enumerable.Repeat(step, 64)]);
    }

    /// <summary>A tool-calling answer.</summary>
    internal static JsonObjectValue Call(params (string Name, JsonObjectValue Args, string Id)[] calls) => ToolsTests.Calls(calls);

    internal static readonly JsonObjectValue Done = ToolsTests.Done;

    /// <summary>Waits until the conversation's committed view has pending interactions satisfying <paramref name="until"/>.</summary>
    internal static async Task<IReadOnlyList<PendingInteraction>> WaitPending(IConversation conversation, Func<IReadOnlyList<PendingInteraction>, bool> until)
    {
        using var state = await conversation.ViewStateAsync(Ctx);
        var reached = new TaskCompletionSource<IReadOnlyList<PendingInteraction>>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check(ConversationView view)
        {
            var pending = UserInteraction.PendingIn(view);
            if (until(pending)) reached.TrySetResult(pending);
        }
        using var subscription = state.Subscribe((view, _, _) => Check(view));
        Check(state.Value);
        return await reached.Task.WaitAsync(Timeout);
    }

    /// <summary>Waits for <paramref name="count"/> pending interactions and returns them.</summary>
    internal static Task<IReadOnlyList<PendingInteraction>> WaitPending(IConversation conversation, int count = 1) =>
        WaitPending(conversation, pending => pending.Count == count);

    /// <summary>Submits user input and returns the submission.</summary>
    internal static Task<ISubmission> Submit(IConversation conversation, string text) => conversation.SubmitAsync(SubmissionDraft.Input(text), Ctx);

    internal static async Task<SubmissionRecord> Settled(ISubmission submission) => await submission.WaitAsync(Ctx).WaitAsync(Timeout);

    /// <summary>The tool result messages of the conversation, oldest first.</summary>
    internal static async Task<List<JsonObjectValue>> Results(IConversation conversation) => ToolsTests.Results(await AllEntries(conversation));

    /// <summary>The tool result messages of one tool.</summary>
    internal static async Task<List<JsonObjectValue>> Results(IConversation conversation, string tool) =>
        [.. (await Results(conversation)).Where(result => S(result["toolName"]) == tool)];

    internal static string Text(JsonObjectValue result) => ToolsTests.ResultText(result);

    internal static bool IsError(JsonObjectValue result) => result["isError"] is JsonBooleanValue { Value: true };

    /// <summary>The <c>ask_user_question</c> arguments of <paramref name="questions"/>.</summary>
    internal static JsonObjectValue Questions(params UserQuestion[] questions) =>
        Obj(("questions", JsonValue.Array(questions.Select(question => (JsonValue)question.ToJson()))));

    internal static UserQuestion Question(string header, string question, bool multi, params string[] labels) =>
        new(question, header, [.. labels.Select(label => new QuestionOption(label, $"About {label}"))], multi);

    internal static readonly UserQuestion Color = Question("Color", "Which color should the button be?", false, "Blue (Recommended)", "Green", "Red");
    internal static readonly UserQuestion Features = Question("Features", "Which features should ship first?", true, "Search", "Export", "Sharing");

    /// <summary>Runs the turn after <paramref name="text"/> until the conversation's first interaction is pending, then returns it.</summary>
    internal static async Task<(ISubmission Submission, PendingInteraction Pending)> UntilPending(IConversation conversation, string text = "go")
    {
        var submission = await Submit(conversation, text);
        var pending = await WaitPending(conversation);
        return (submission, pending[0]);
    }

    /// <summary>A storage failing the first batch that appends a tool result, as a storage failure at the settling commit: the Session fails
    /// and closes, and nothing of the batch lands.</summary>
    internal static IStorage FailingSettle(IStorage inner) => new CrashStorage(inner, CrashScenarios.ToolResultEntry, after: false, "settle-failed", fail: true);

    /// <summary>Waits for a Harness that failed in Storage to close itself.</summary>
    internal static async Task ClosedByFailure(IHarness harness)
    {
        var end = await harness.Closed.WaitAsync(Timeout);
        Equal(SessionEndReason.Failed, end.Reason, "Harness end");
    }

    /// <summary>The live tasks after the conversation went idle.</summary>
    internal static async Task NoLiveTasks(IHarness harness)
    {
        var inspection = await harness.InspectAsync(Ctx);
        Equal(0, inspection.Tasks.Length, "live tasks");
    }

    /// <summary>The <c>pi.agent</c> document of a conversation.</summary>
    internal static Task<JsonObjectValue?> AgentDoc(IHarness harness, IConversation conversation) =>
        harness.SnapshotAsync(Agents.AgentDoc, conversation.Id, Ctx);

    /// <summary>A tool execution api for running a wrapped tool directly: the environment and committed reads of a document reader.</summary>
    internal sealed class ReaderApi(IExecutionEnv env, IDocumentReader reader, ConversationId conversationId)
        : DelegatingToolExecutionApi(System.Reflection.DispatchProxy.Create<IToolExecutionApi, ToolApiStub>())
    {
        public override IExecutionEnv? Env => env;
        public override ConversationId ConversationId => conversationId;
        public override TaskId TaskId => new(1);
        public override string CallId => "direct";
        public override Task<JsonObjectValue?> SnapshotAsync(ConversationDocToken token, ConversationId id, DurableContext context) =>
            reader.SnapshotAsync(token, id, context);
    }

    /// <summary>A document reader holding one plan-mode state document.</summary>
    internal sealed class PlanStateReader(JsonObjectValue? state) : IDocumentReader
    {
        public Task<JsonObjectValue?> SnapshotAsync(SessionDocToken token, DurableContext context) => Task.FromResult<JsonObjectValue?>(null);
        public Task<JsonObjectValue?> SnapshotAsync(ConversationDocToken token, ConversationId conversationId, DurableContext context) =>
            Task.FromResult(ReferenceEquals(token, PlanMode.StateDoc) ? state : null);
        public Task<JsonObjectValue?> SnapshotAsync(TaskDocToken token, TaskId taskId, DurableContext context) => Task.FromResult<JsonObjectValue?>(null);
        public Task<JsonObjectValue?> SnapshotAsync(SessionDocFamilyToken token, string key, DurableContext context) => Task.FromResult<JsonObjectValue?>(null);
        public Task<JsonObjectValue?> SnapshotAsync(ConversationDocFamilyToken token, ConversationId conversationId, string key, DurableContext context) =>
            Task.FromResult<JsonObjectValue?>(null);
        public Task<JsonObjectValue?> SnapshotAsync(TaskDocFamilyToken token, TaskId taskId, string key, DurableContext context) => Task.FromResult<JsonObjectValue?>(null);
        public Task<JsonObjectValue?> SnapshotAsOfAsync(RewindableConversationDocToken token, ConversationId conversationId, EntryId at, DurableContext context) =>
            Task.FromResult<JsonObjectValue?>(null);
        public Task<JsonObjectValue?> SnapshotAsOfAsync(RewindableConversationDocFamilyToken token, ConversationId conversationId, string key, EntryId at,
            DurableContext context) => Task.FromResult<JsonObjectValue?>(null);
    }

    /// <summary>The plan-mode state document of a conversation in plan mode with <paramref name="directory"/>.</summary>
    internal static JsonObjectValue PlanState(string directory) => Obj(("mode", "plan"), ("directory", directory));
}
