using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Hosting;
using PiSharp.Durable.Tools;

internal static partial class Program
{
    internal static IEnumerable<(string, Func<Task>)> AppsCases() =>
    [
        Case("apps.app-state-path-operations", () =>
        {
            var document = (JsonObjectValue)JsonValue.Parse("""{"goal":"x","counter":1,"notes":["a"],"nested":{"a/b":1,"t~":2}}""");
            JsonObjectValue Apply(AppStateOperation operation, string path, JsonValue? value = null) =>
                AppStateDocument.Apply(document, new AppStateChange(operation, path, value));
            Equal("""{"goal":"x","counter":1,"notes":["a"],"nested":{"a/b":1,"t~":2},"new":true}""", Apply(AppStateOperation.Create, "/new", true).ToJson(), "create");
            Equal(3.0, ((JsonNumberValue)AppStateDocument.Read(Apply(AppStateOperation.Increment, "/counter", 2), "/counter")!).Value, "increment");
            Equal(2.0, ((JsonNumberValue)AppStateDocument.Read(Apply(AppStateOperation.Increment, "/counter"), "/counter")!).Value, "increment by 1");
            Equal("""["a","b"]""", AppStateDocument.Read(Apply(AppStateOperation.Create, "/notes/-", "b"), "/notes")!.ToJson(), "append");
            Equal("""["z","a"]""", AppStateDocument.Read(Apply(AppStateOperation.Create, "/notes/0", "z"), "/notes")!.ToJson(), "insert");
            Equal("[]", AppStateDocument.Read(Apply(AppStateOperation.Delete, "/notes/0"), "/notes")!.ToJson(), "delete from an array");
            Equal("2", AppStateDocument.Read(document, "/nested/t~0")!.ToJson(), "~0 is ~");
            Equal("1", AppStateDocument.Read(document, "/nested/a~1b")!.ToJson(), "~1 is /");
            Equal("{}", Apply(AppStateOperation.Delete, "").ToJson(), "delete the whole document");
            Equal("""{"only":1}""", Apply(AppStateOperation.Update, "", JsonValue.Object([new("only", 1.0)])).ToJson(), "replace the whole document");
            Throws<HostInvalidException>(() => Apply(AppStateOperation.Create, "/goal", "y"), "create an existing field");
            Throws<HostInvalidException>(() => Apply(AppStateOperation.Update, "/missing", 1), "update a missing field");
            Throws<HostInvalidException>(() => Apply(AppStateOperation.Increment, "/goal"), "increment a string");
            Throws<HostInvalidException>(() => Apply(AppStateOperation.Delete, "/notes/5"), "delete past the end");
            Throws<HostInvalidException>(() => Apply(AppStateOperation.Create, "goal", 1), "a path without /");
            Throws<HostInvalidException>(() => Apply(AppStateOperation.Create, "/__proto__", 1), "a prototype segment");
            Throws<HostInvalidException>(() => Apply(AppStateOperation.Update, "", "text"), "a document that is not an object");
            JsonValue deep = 1.0;
            for (var depth = 0; depth < 30; depth++) deep = JsonValue.Object([new("d", deep)]);
            Throws<HostInvalidException>(() => Apply(AppStateOperation.Create, "/deep", deep), "too deep");
            Equal("""{"a":{"b":{"c":1}}}""", AppStateDocument.Set(JsonObjectValue.Empty, "/a/b/c", 1).ToJson(), "set creates parents");
        }),

        Case("apps.app-state-revisions-and-receipts", async () =>
        {
            await using var device = await TestDevice.OpenAsync("apps-state");
            var state = device.Host.AppState;
            Equal(0L, state.Get().Revision, "revision before the first change");
            Equal(0.0, ((JsonNumberValue)AppStateDocument.Read(state.Get().Value, "/counter")!).Value, "the seeded counter");
            var first = state.Change(new AppStateChange(AppStateOperation.Increment, "/counter"), receiptKey: "r-1");
            Equal(1L, first.Revision, "revision");
            var replay = state.Change(new AppStateChange(AppStateOperation.Increment, "/counter"), receiptKey: "r-1");
            Equal(1L, replay.Revision, "a used receipt changes nothing");
            Equal("1", AppStateDocument.Read(state.Get().Value, "/counter")!.ToJson(), "counted once");
            var conflict = Throws<AppStateConflictException>(() => state.Replace(JsonObjectValue.Empty, expectedRevision: 0), "a stale save");
            Equal("conflict", conflict.Code, "code");
            var saved = state.Replace((JsonObjectValue)JsonValue.Parse("""{"goal":"ship"}"""), expectedRevision: 1);
            Equal(2L, saved.Revision, "a current save");
            await device.ReopenAsync();
            Equal("""{"goal":"ship"}""", device.Host.AppState.Get().Value.ToJson(), "after a reopen");
            Equal(2L, device.Host.AppState.Get().Revision, "revision after a reopen");
        }),

        Case("apps.state-tools-apply-once-and-note-the-transcript", async () =>
        {
            await using var device = await TestDevice.OpenAsync("apps-state-tools");
            var session = await device.CreateAsync("state");
            var changed = await session.SendAsync(1, new SendRequest("""tool: update_state {"operation":"increment","path":"/counter"}""", "st-1") { Wait = Answer });
            Check(changed.AnswerText?.Contains("\"revision\":1") == true, "the tool's answer: " + changed.AnswerText);
            Equal("1", AppStateDocument.Read(device.Host.AppState.Get().Value, "/counter")!.ToJson(), "counter");
            var read = await session.SendAsync(1, new SendRequest("""tool: read_state {"path":"/counter"}""", "st-2") { Wait = Answer });
            Check(read.AnswerText?.Contains("\"value\":1") == true, "read_state: " + read.AnswerText);
            var entries = await AllEntriesAsync(session, 1);
            Equal(1, entries.Count(entry => entry.Kind == "pisharp.state-change"), "one state-change entry");
            var other = await device.CreateAsync("other");
            var shared = await other.SendAsync(1, new SendRequest("""tool: read_state {"path":"/counter"}""", "st-3") { Wait = Answer });
            Check(shared.AnswerText?.Contains("\"value\":1") == true, "another session sees the same App State: " + shared.AnswerText);
            var fromUi = await session.ChangeAppStateAsync(1, new AppStateChange(AppStateOperation.Update, "/goal", "from the UI"), expectedRevision: 1);
            Equal(2L, fromUi.Revision, "a UI change");
            await WaitUntilAsync(async () => (await AllEntriesAsync(session, 1)).Count(entry => entry.Kind == "pisharp.state-change") == 2, "the UI change is noted");
        }),

        Case("apps.search-history-finds-entries-before-a-reset", async () =>
        {
            await using var device = await TestDevice.OpenAsync("apps-history");
            var session = await device.CreateAsync("history");
            await session.SendAsync(1, new SendRequest("remember the alpha marker", "h-1") { Wait = Answer });
            await session.ResetAsync(1, "Fresh start.");
            await WaitUntilAsync(async () => (await ContextHeadAsync(session)) == "pi.reset", "the reset is placed");
            var found = await session.SendAsync(1, new SendRequest("""tool: search_history {"query":"alpha marker","order":"oldest","limit":1}""", "h-2") { Wait = Answer });
            Check(found.AnswerText?.Contains("before reset") == true, "the hit is marked before reset: " + found.AnswerText);
            Check(found.AnswerText?.Contains("\"total\":") == true, "a result page");
        }),

        Case("apps.handoff-starts-a-new-context", async () =>
        {
            await using var device = await TestDevice.OpenAsync("apps-handoff");
            var session = await device.CreateAsync("handoff");
            await session.SendAsync(1, new SendRequest("first", "ho-1") { Wait = Answer });
            await session.SendAsync(1, new SendRequest("""tool: handoff {"note":"carry on with the plan"}""", "ho-2") { Wait = Answer });
            await WaitUntilAsync(async () => (await ContextHeadAsync(session)) == "pi.reset", "the handoff started a new context");
        }),

        Case("apps.subagent-answers-and-is-listed", async () =>
        {
            await using var device = await TestDevice.OpenAsync("apps-subagent");
            var session = await device.CreateAsync("subagent");
            var reply = await session.SendAsync(1, new SendRequest("""tool: subagent {"task":"hi there"}""", "sa-1") { Wait = Answer });
            Equal("Tool finished: subagent: Echo: hi there", reply.AnswerText, "the child's answer comes back");
            var child = (await session.AgentsAsync()).Single(agent => agent.Kind == AgentKind.Subagent);
            Equal(1L, child.OwnerAgentId, "owned by Main");
            Equal($"Subagent #{child.Id}", child.Name, "name");
            var tools = (await session.ToolsAsync(child.Id)).Where(tool => tool.Offered).Select(tool => tool.Name).ToList();
            Check(!tools.Contains("subagent") && !tools.Contains("ask_user_question"), "the child cannot delegate or ask: " + string.Join(",", tools));
        }),

        Case("apps.start-session-creates-a-session-once-and-prompts-it", async () =>
        {
            await using var device = await TestDevice.OpenAsync("apps-start");
            Directory.CreateDirectory(Path.Join(device.Work, "child"));
            var session = await device.CreateAsync("parent");
            var reply = await session.SendAsync(1, new SendRequest("""tool: start_session {"name":"child","cwd":"child","prompt":"hello child"}""", "ss-1") { Wait = Answer });
            Check(reply.AnswerText?.Contains("Started session child") == true, "answer: " + reply.AnswerText);
            var child = device.Host.ResolveSession("child");
            Equal(Path.Join(device.Work, "child"), child.Cwd, "a relative cwd resolves against the parent's");
            await WaitUntilAsync(async () => (await AllEntriesAsync(child, 1)).Any(entry => SubmissionAnswers.TextOf(entry) == "Echo: hello child"), "the child answered its prompt");
            Equal(2, device.Host.Sessions.Count, "sessions");
        }),

        Case("apps.optional-extensions-and-the-prompt-checklist", async () =>
        {
            await using var device = await TestDevice.OpenAsync("apps-extensions");
            Directory.CreateDirectory(device.Host.PromptsDirectory);
            File.WriteAllText(Path.Join(device.Host.PromptsDirectory, "terse.md"), "# Be terse\nAnswer in TERSE-MARKER style.");
            File.WriteAllText(Path.Join(device.Host.PromptsDirectory, "other.md"), "Other prompt.");
            var session = await device.CreateAsync("extensions");
            var initial = await session.ExtensionsAsync(1);
            Equal("system-prompts:False,command-guard:False", string.Join(",", initial.Optional.Select(extension => $"{extension.Name}:{extension.Enabled}")), "off by default");
            Check(initial.BuiltIn.Contains("durable-apps") && initial.BuiltIn.Contains("coding-tools"), "built in: " + string.Join(",", initial.BuiltIn));
            Equal("other.md,terse.md", string.Join(",", initial.Optional[0].Prompts.Select(prompt => prompt.Id)), "prompt files");
            Equal("Be terse", initial.Optional[0].Prompts[1].Description, "a prompt's description");

            await session.SetExtensionAsync(1, "system-prompts", true);
            var checkedOne = await session.SetPromptAsync(1, "terse.md", true);
            Check(checkedOne.Optional[0].Enabled && checkedOne.Optional[0].Prompts.Single(prompt => prompt.Id == "terse.md").Enabled, "on and checked");
            await session.SendAsync(1, new SendRequest("hello", "ext-1") { Wait = Answer });
            var conversation = (await session.GetHarness().ConversationAsync(ConversationId.Root, DurableContext.Background))!;
            var messages = string.Concat((await conversation.ContextAsync(DurableContext.Background)).Messages.Select(message => message.ToJson()));
            Check(messages.Contains("TERSE-MARKER"), "the checked prompt is in the system prompt");
            await ThrowsAsync<HostInvalidException>(() => session.SetExtensionAsync(1, "coding-tools", false), "a built-in extension");
            await ThrowsAsync<HostNotFoundException>(() => session.SetPromptAsync(1, "missing.md", true), "an unknown prompt");
            var fork = await session.CreateAgentAsync();
            Check(!(await session.ExtensionsAsync(fork.Id)).Optional[0].Enabled, "a new agent starts with the defaults");
        }),

        Case("apps.command-guard-blocks-destructive-commands", async () =>
        {
            Check(CommandGuard.Offline("rm -rf build") is { Block: true }, "rm -rf");
            Check(CommandGuard.Offline("git push --force origin main") is { Block: true }, "force push");
            Check(CommandGuard.Offline("Remove-Item -Recurse -Force C:\\temp\\x") is { Block: true }, "PowerShell recursive delete");
            Check(CommandGuard.Offline("sudo shutdown -h now") is { Block: true }, "shutdown");
            Check(CommandGuard.Offline("ls -la && git status") is null, "a harmless command");
            Check(CommandGuard.Offline("echo halt") is null, "a word in an argument");

            await using var device = await TestDevice.OpenAsync("apps-guard");
            var session = await device.CreateAsync("guard");
            await session.SetExtensionAsync(1, "command-guard", true);
            var blocked = await session.SendAsync(1, new SendRequest("!rm -rf ./nothing-here", "g-1") { Wait = Answer });
            Check(blocked.AnswerText?.Contains("Blocked by the command guard") == true, "blocked: " + blocked.AnswerText);
        }),

        Case("apps.questions-are-answered-through-the-host", async () =>
        {
            await using var device = await TestDevice.OpenAsync("apps-questions");
            var session = await device.CreateAsync("questions");
            await session.SendAsync(1, new SendRequest(
                """tool: ask_user_question {"questions":[{"question":"Which version?","header":"Version","options":[{"label":"1.2.0.2","description":"C# fixes"},{"label":"1.2.1","description":"Next Pi"}],"multiSelect":false}]}""",
                "q-1"));
            await WaitUntilAsync(async () => (await session.PendingInteractionsAsync(1)).Count == 1, "a question waits");
            var pending = (await session.PendingInteractionsAsync(1)).Single();
            Equal("Which version?", UserQuestions.QuestionsOf(pending.Details).Single().Question, "the question");
            await session.AnswerQuestionsAsync(1, pending.TaskId.Value, QuestionResponse.Answered(new QuestionAnswer(["1.2.0.2"])));
            var answered = await session.SendAsync(1, new SendRequest("ignored", "q-1") { Wait = Answer });
            Check(answered.AnswerText?.Contains("1.2.0.2") == true, "the answer reached the agent: " + answered.AnswerText);
            await ThrowsAsync<AppStateConflictException>(() => session.AnswerQuestionsAsync(1, pending.TaskId.Value, null), "answering a call that no longer waits");
        }),
    ];

    /// <summary>Every entry of an agent, oldest first.</summary>
    internal static async Task<List<EntryRecord>> AllEntriesAsync(HostedSession session, long agentId)
    {
        var conversation = (await session.GetHarness().ConversationAsync(new ConversationId(agentId), DurableContext.Background))!;
        var all = new List<EntryRecord>();
        JsonObjectValue? cursor = null;
        do
        {
            var page = await conversation.EntriesAsync(new EntryQueryBounds { Order = ScanOrder.Ascending }, 500, cursor, DurableContext.Background);
            all.AddRange(page.Items);
            cursor = page.Next;
        } while (cursor is not null);
        return all;
    }

    /// <summary>The kind of the entry that heads Main's active context.</summary>
    internal static async Task<string?> ContextHeadAsync(HostedSession session)
    {
        var conversation = (await session.GetHarness().ConversationAsync(ConversationId.Root, DurableContext.Background))!;
        return (await conversation.ContextAsync(DurableContext.Background)).Head?.Kind;
    }
}
