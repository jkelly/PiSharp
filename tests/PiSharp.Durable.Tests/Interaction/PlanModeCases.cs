// Authored for PiSharp (no upstream test file): the plan.* flow cases of the PiSharp-only plan mode (decision 0005, "PiSharp-only
// additions: ask_user_question and plan mode"): entering and leaving, the exit_plan_mode review, restart and storage-failure recovery, and
// forks. They run on memory (plan.*), JSONL (plan.jsonl.*) and SQLite (plan.sqlite.*). The containment cases are in PlanScopeCases.cs.
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Tools;
using static ChatTests;
using static InteractionTests;
using static Program;
using static SessionTests;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> PlanModeCases() =>
        InteractionTests.Backends().SelectMany(backend => PlanModeTests.Flows("plan." + backend.Prefix, backend.Location));
}

/// <summary>The plan.* flow cases.</summary>
internal static class PlanModeTests
{
    internal static JsonObjectValue Write(string path, string content, string id) =>
        Call(("write", Obj(("path", path), ("content", content)), id));

    internal static JsonObjectValue Exit(JsonObjectValue? args = null, string id = "x1") => Call((PlanMode.ExitToolName, args ?? JsonObjectValue.Empty, id));

    internal const string PlanText = "# Plan\n1. Change the button.\n2. Run the tests.\n";

    internal static string Approved(string? planFile, string? plan) =>
        "The user approved your plan. Plan mode is off and you have the full tool set. Implement the approved plan now." +
        (planFile is null ? "" : $"\n\nApproved plan ({planFile}):\n{plan}");

    /// <summary>The "plan" turn writes plan.md into the plan directory, then submits it; other turns answer "done".</summary>
    private static void PlanTurn(ChatSetup setup, Dirs dirs) => Script(setup, (text, results) => text == "plan"
        ? results.Count switch
        {
            0 => Write(Path.Combine(dirs.Plans, "plan.md"), PlanText, "w1"),
            1 => Exit(),
            _ => Done,
        }
        : Done);

    private static Task<PlanModeState> Enter(IConversation root, ChatSetup setup, Dirs dirs) => PlanMode.EnterAsync(root, setup.Registry, dirs.Plans, Ctx);

    private static async Task<PlanModeState?> State(IHarness harness, IConversation conversation) =>
        await PlanMode.StateAsync(harness, conversation.Id, Ctx);

    private static string PlanFile(Dirs dirs) => Path.Combine(Path.GetFullPath(dirs.Plans), "plan.md");

    internal static IEnumerable<(string, Func<Task>)> Flows(string prefix, Func<Func<IStorage>> location)
    {
        yield return Case(prefix + "enter", async () =>
        {
            var setup = NewSetup();
            var dirs = new Dirs("plan-enter");
            var requests = new SyncList<string>();
            Script(setup, (_, _) => Done);
            setup.Faux.SetResponses([.. Enumerable.Repeat(FauxStep.From(request =>
            {
                requests.Add(JsonValue.Array(request.Messages).ToJson());
                return Done;
            }), 4)]);
            var (harness, root) = await Open(location(), setup, dirs);
            var state = await Enter(root, setup, dirs);
            Check(state.Active, "active");
            Equal(Path.GetFullPath(dirs.Plans), state.Directory, "directory");
            var agentDoc = await AgentDoc(harness, root);
            Equal("""{"add":["plan-mode"]}""", agentDoc!["extensions"].ToJson(), "stored extensions");
            Equal("""["read","write","edit","bash","powershell","ask_user_question","exit_plan_mode"]""", agentDoc["tools"].ToJson(), "stored tools");
            Check(PlanMode.IsActive(agentDoc), "IsActive");
            MatchJson("""{"mode":"plan","enteredAt":"<number>","saved":{"extensions":null,"tools":null}}""",
                await harness.SnapshotAsync(PlanMode.StateDoc, root.Id, Ctx), "state document");
            var agent = await root.AgentAsync(Ctx);
            Seq(["read", "write", "edit", "bash", "ask_user_question", "exit_plan_mode"], agent.Tools.Select(tool => tool.Name), "offered tools");
            var installed = setup.Registry.Snapshot().Tools().ToDictionary(tool => tool.Tool.Name, tool => tool.Tool);
            Check(!ReferenceEquals(installed["write"], agent.Tools.Single(tool => tool.Name == "write")), "write is wrapped");
            Check(!ReferenceEquals(installed["edit"], agent.Tools.Single(tool => tool.Name == "edit")), "edit is wrapped");
            Check(ReferenceEquals(installed["read"], agent.Tools.Single(tool => tool.Name == "read")), "read is not wrapped");
            var section = agent.Sections.Single(item => item.Key == "plan_mode");
            Check(section.Tag == true, "tagged section");
            var text = await section.Render(new PromptInput
            {
                ConversationId = root.Id, Agent = agent, Env = dirs.Env, Shown = new Dictionary<string, string>(), Read = harness,
            }, Ctx);
            Equal(PlanPrompt.Render(Path.GetFullPath(dirs.Plans)), text, "section text");
            Check(text!.StartsWith("Plan mode is active: the user wants a plan before anything in the project changes.\n", StringComparison.Ordinal) &&
                text.Contains($"- You may create and edit files only inside the plan directory: {Path.GetFullPath(dirs.Plans)}\n", StringComparison.Ordinal) &&
                text.Contains($"Write the plan to {PlanFile(dirs)}.", StringComparison.Ordinal), "section text: " + text);
            // The model's request carries the section.
            await Settled(await Submit(root, "hello"));
            Check(requests.Snapshot().Any(request => request.Contains("Plan mode is active", StringComparison.Ordinal)), "request has the plan_mode section");
            await harness.CloseAsync(Ctx);
        });

        yield return Case(prefix + "enter-idempotent", async () =>
        {
            var setup = NewSetup();
            var dirs = new Dirs("plan-enter-idempotent");
            var other = new Dirs("plan-enter-idempotent-other");
            var (harness, root) = await Open(location(), setup, dirs);
            await Enter(root, setup, dirs);
            var before = await harness.SnapshotAsync(PlanMode.StateDoc, root.Id, Ctx);
            var agentBefore = await AgentDoc(harness, root);
            var again = await PlanMode.EnterAsync(root, setup.Registry, other.Plans, Ctx);
            Equal(Path.GetFullPath(dirs.Plans), again.Directory, "directory kept");
            Equal(before!.ToJson(), (await harness.SnapshotAsync(PlanMode.StateDoc, root.Id, Ctx))!.ToJson(), "state unchanged");
            Equal(agentBefore!.ToJson(), (await AgentDoc(harness, root))!.ToJson(), "agent unchanged");
            await PlanMode.ExitAsync(root, setup.Registry, Ctx);
            var agentAfter = await AgentDoc(harness, root);
            Check(!agentAfter!.ContainsKey("extensions") && !agentAfter.ContainsKey("tools"), "the first saved selection is restored: " + agentAfter.ToJson());
            await harness.CloseAsync(Ctx);
        });

        yield return Case(prefix + "exit-restores-saved", async () =>
        {
            var setup = NewSetup();
            var dirs = new Dirs("plan-exit-restores");
            var (harness, root) = await Open(location(), setup, dirs);
            // Unset selection and filter: cleared again on exit.
            await Enter(root, setup, dirs);
            await PlanMode.ExitAsync(root, setup.Registry, Ctx);
            var cleared = await AgentDoc(harness, root);
            Check(!cleared!.ContainsKey("extensions") && !cleared.ContainsKey("tools"), "cleared: " + cleared.ToJson());
            Check(!(await State(harness, root))!.Active, "mode default");
            Check(!(await harness.SnapshotAsync(PlanMode.StateDoc, root.Id, Ctx))!.ContainsKey("saved"), "saved dropped");
            // An exact selection and a removal filter: kept, narrowed while planning, and restored exactly.
            var snapshot = setup.Registry.Snapshot();
            await root.ConfigureAsync(new AgentChange
            {
                Extensions = ExtensionSelection.Only(snapshot.GetExtension("coding-tools")!, snapshot.GetExtension(UserInteraction.ExtensionName)!),
                Tools = ToolSelection.Without(ToolsNamed(setup, "bash")),
            }, Ctx);
            var configured = await AgentDoc(harness, root);
            await Enter(root, setup, dirs);
            var planning = await AgentDoc(harness, root);
            Equal("""["coding-tools","user-interaction","plan-mode"]""", planning!["extensions"].ToJson(), "extensions while planning");
            Equal("""["read","write","edit","powershell","ask_user_question","exit_plan_mode"]""", planning["tools"].ToJson(), "tools while planning");
            Seq(["read", "write", "edit", "ask_user_question", "exit_plan_mode"], (await root.AgentAsync(Ctx)).Tools.Select(tool => tool.Name), "offered while planning");
            await PlanMode.ExitAsync(root, setup.Registry, Ctx);
            var restored = await AgentDoc(harness, root);
            Equal(configured!["extensions"].ToJson(), restored!["extensions"].ToJson(), "extensions restored");
            Equal(configured["tools"].ToJson(), restored["tools"].ToJson(), "tools restored");
            // Leaving again does nothing.
            await PlanMode.ExitAsync(root, setup.Registry, Ctx);
            Equal(restored.ToJson(), (await AgentDoc(harness, root))!.ToJson(), "second exit");
            await harness.CloseAsync(Ctx);
        });

        yield return Case(prefix + "write-inside", async () =>
        {
            var setup = NewSetup();
            var dirs = new Dirs("plan-write-inside");
            var plan = Path.Combine(dirs.Plans, "plan.md");
            var notes = Path.Combine(dirs.Plans, "notes", "sketch.md");
            Script(setup, (_, results) => results.Count switch
            {
                0 => Write(plan, "# Plan\nfirst\n", "w1"),
                1 => Write(notes, "sketch", "w2"),
                2 => Call(("edit", Obj(("path", plan), ("edits", JsonValue.Array(Obj(("oldText", "first"), ("newText", "second"))))), "e1")),
                _ => Done,
            });
            var (harness, root) = await Open(location(), setup, dirs);
            await Enter(root, setup, dirs);
            await Settled(await Submit(root, "go"));
            var results = await Results(root);
            Equal(3, results.Count, "results");
            Check(results.All(result => !IsError(result)), "no errors: " + string.Join(" | ", results.Select(Text)));
            Equal("# Plan\nsecond\n", File.ReadAllText(plan), "plan.md");
            Equal("sketch", File.ReadAllText(notes), "notes");
            await harness.CloseAsync(Ctx);
        });

        yield return Case(prefix + "write-outside-denied", async () =>
        {
            var setup = NewSetup();
            var dirs = new Dirs("plan-write-outside");
            var existing = Path.Combine(dirs.Project, "existing.txt");
            File.WriteAllText(existing, "keep me");
            var separator = Path.DirectorySeparatorChar;
            var writes = new (string Path, string Created)[]
            {
                ("escape-relative.txt", Path.Combine(dirs.Project, "escape-relative.txt")),
                (Path.Combine(dirs.Outside, "absolute.txt"), Path.Combine(dirs.Outside, "absolute.txt")),
                (dirs.Plans + separator + ".." + separator + "dotdot.txt", Path.Combine(Path.GetDirectoryName(dirs.Plans)!, "dotdot.txt")),
                ("@" + Path.Combine(dirs.Outside, "at.txt"), Path.Combine(dirs.Outside, "at.txt")),
                (Path.Combine(dirs.Outside, "uni space.txt"), Path.Combine(dirs.Outside, "uni space.txt")),
                (dirs.Plans, dirs.Plans),
            };
            Script(setup, (_, results) => results.Count == 0
                ? Call([.. writes.Select((write, index) => ("write", Obj(("path", write.Path), ("content", "x")), $"w{index}")),
                    ("edit", Obj(("path", existing), ("edits", JsonValue.Array(Obj(("oldText", "keep"), ("newText", "lose"))))), "e1")])
                : Done);
            var (harness, root) = await Open(location(), setup, dirs);
            await Enter(root, setup, dirs);
            await Settled(await Submit(root, "go"));
            var results = await Results(root);
            Equal(writes.Length + 1, results.Count, "results");
            foreach (var result in results)
                Check(IsError(result) && Text(result).Contains("Plan mode: ", StringComparison.Ordinal) &&
                    Text(result).Contains("is outside the plan directory " + Path.GetFullPath(dirs.Plans), StringComparison.Ordinal),
                    $"{S(result["toolCallId"])} denied: {Text(result)}");
            foreach (var (_, created) in writes.SkipLast(1)) Check(!File.Exists(created), "not created: " + created);
            Check(!File.Exists(Path.Combine(dirs.Outside, "uni space.txt")), "not created with the original name");
            Check(Directory.Exists(dirs.Plans), "the plan directory is still a directory");
            Equal("keep me", File.ReadAllText(existing), "the edited file is unchanged");
            await harness.CloseAsync(Ctx);
        });

        yield return Case(prefix + "bash-available-in-plan-mode", async () =>
        {
            var setup = NewSetup();
            setup.Registry.Install(Define.Extension(new Extension { Name = "powershell", Tools = [CodingTools.CreatePowerShellTool()] }));
            DefaultSelection(setup);
            var dirs = new Dirs("plan-bash");
            var (harness, root) = await Open(location(), setup, dirs);
            await Enter(root, setup, dirs);
            var agent = await root.AgentAsync(Ctx);
            var installed = setup.Registry.Snapshot().Tools().ToDictionary(tool => tool.Tool.Name, tool => tool.Tool);
            // Owner decision: the shells stay available and are not confined, so plan mode does not stop them from changing files.
            foreach (var shell in new[] { "bash", "powershell" })
                Check(ReferenceEquals(installed[shell], agent.Tools.Single(tool => tool.Name == shell)), shell + " is offered unwrapped");
            Check(PlanPrompt.Render("/plans").Contains("never change files outside the plan directory, through write, edit or the shell", StringComparison.Ordinal),
                "the prompt asks the agent not to change files through the shell");
            await harness.CloseAsync(Ctx);
        });

        yield return Case(prefix + "exit-approve", async () =>
        {
            var setup = NewSetup();
            var dirs = new Dirs("plan-exit-approve");
            var built = Path.Combine(dirs.Outside, "built.txt");
            Script(setup, (text, results) => text switch
            {
                "plan" => results.Count switch { 0 => Write(Path.Combine(dirs.Plans, "plan.md"), PlanText, "w1"), 1 => Exit(), _ => Done },
                "build" => results.Count == 0 ? Write(built, "built", "w2") : Done,
                _ => Done,
            });
            var (harness, root) = await Open(location(), setup, dirs);
            await Enter(root, setup, dirs);
            var (submission, pending) = await UntilPending(root, "plan");
            Equal(PlanMode.DetailsKind, pending.Kind, "kind");
            Equal("x1", pending.CallId, "call");
            MatchExactly($$"""{"kind":"pisharp.plan-approval","version":1,"state":"pending","plan":{{Js(PlanText)}},"planFile":{{Js(PlanFile(dirs))}},"directory":{{Js(Path.GetFullPath(dirs.Plans))}}}""",
                pending.Details, "details");
            var waiting = await State(harness, root);
            Equal("pending", waiting!.Submissions.Single().Status, "submission pending");
            await PlanMode.ApproveAsync(root, setup.Registry, pending.TaskId, stop: false, Ctx);
            Equal(SubmissionStatus.Done, (await Settled(submission)).Status, "submission");
            var exit = (await Results(root, PlanMode.ExitToolName)).Single();
            Check(!IsError(exit), "not an error");
            Equal(Approved(PlanFile(dirs), PlanText), Text(exit), "result");
            MatchJson("""{"details":{"kind":"pisharp.plan-approval","state":"approved","stop":false}}""", exit, "result details");
            var state = await State(harness, root);
            Check(!state!.Active, "plan mode off");
            Equal(pending.TaskId, state.Approved!.TaskId, "approved call");
            Equal(PlanFile(dirs), state.Approved.PlanFile, "approved file");
            Equal("approved", state.Submissions.Single().Status, "submission approved");
            var agentDoc = await AgentDoc(harness, root);
            Check(!PlanMode.IsActive(agentDoc) && !agentDoc!.ContainsKey("tools"), "agent restored: " + agentDoc?.ToJson());
            Equal(3, setup.Faux.State.CallCount, "requests");
            // The full tool set: write is no longer confined.
            await Settled(await Submit(root, "build"));
            Equal("built", File.ReadAllText(built), "written outside after approval");
            await harness.CloseAsync(Ctx);
        });

        yield return Case(prefix + "exit-approve-stop", async () =>
        {
            var setup = NewSetup();
            var dirs = new Dirs("plan-exit-approve-stop");
            PlanTurn(setup, dirs);
            var (harness, root) = await Open(location(), setup, dirs);
            await Enter(root, setup, dirs);
            var (submission, pending) = await UntilPending(root, "plan");
            await PlanMode.ApproveAsync(root, setup.Registry, pending.TaskId, stop: true, Ctx);
            // Approving again the same way changes nothing (the call may have settled already, then it is no longer pending).
            try { await PlanMode.ApproveAsync(root, setup.Registry, pending.TaskId, stop: true, Ctx); }
            catch (InteractionNotPendingException) { }
            Equal(SubmissionStatus.Done, (await Settled(submission)).Status, "submission");
            var exit = (await Results(root, PlanMode.ExitToolName)).Single();
            Equal("The user approved your plan and will tell you when to start. Do not change anything yet.", Text(exit), "result");
            Equal(2, setup.Faux.State.CallCount, "no request after the stop");
            Check(!(await State(harness, root))!.Active, "plan mode off");
            Check(!PlanMode.IsActive(await AgentDoc(harness, root)), "agent restored");
            await harness.CloseAsync(Ctx);
        });

        yield return Case(prefix + "exit-reject-feedback", async () =>
        {
            var setup = NewSetup();
            var dirs = new Dirs("plan-exit-reject");
            Script(setup, (_, results) => results.Count switch
            {
                0 => Write(Path.Combine(dirs.Plans, "plan.md"), PlanText, "w1"),
                1 => Exit(),
                2 => Exit(Obj(("plan", "# Revised plan\n1. Add a rollback step.\n")), "x2"),
                _ => Done,
            });
            var (harness, root) = await Open(location(), setup, dirs);
            await Enter(root, setup, dirs);
            var (submission, first) = await UntilPending(root, "plan");
            await PlanMode.RejectAsync(root, first.TaskId, "  Add a rollback step.  ", Ctx);
            var second = (await WaitPending(root, pending => pending.Count == 1 && pending[0].TaskId != first.TaskId))[0];
            Equal("x2", second.CallId, "second call");
            MatchJson("""{"state":"pending","plan":"# Revised plan\n1. Add a rollback step.\n"}""", second.Details, "second details");
            Check(!second.Details.ContainsKey("planFile"), "an inline plan has no file");
            Check((await State(harness, root))!.Active, "still in plan mode after the rejection");
            Check(PlanMode.IsActive(await AgentDoc(harness, root)), "still planning");
            await ThrowsAsync<InteractionNotPendingException>(() => PlanMode.ApproveAsync(root, setup.Registry, first.TaskId, false, Ctx), "the rejected call");
            await PlanMode.ApproveAsync(root, setup.Registry, second.TaskId, stop: false, Ctx);
            Equal(SubmissionStatus.Done, (await Settled(submission)).Status, "submission");
            var exits = await Results(root, PlanMode.ExitToolName);
            Equal(2, exits.Count, "two submissions");
            Equal("The user rejected the plan; you are still in plan mode. Feedback: Add a rollback step.. Revise the plan and call exit_plan_mode again.",
                Text(exits[0]), "rejection");
            MatchJson("""{"details":{"state":"rejected","feedback":"Add a rollback step."}}""", exits[0], "rejection details");
            Equal(Approved(null, null), Text(exits[1]), "approval of an inline plan");
            var state = await State(harness, root);
            Seq(["rejected", "approved"], state!.Submissions.Select(item => item.Status), "submissions");
            Check(!state.Active, "plan mode off");
            await harness.CloseAsync(Ctx);
        });

        yield return Case(prefix + "exit-reject-without-feedback", async () =>
        {
            var setup = NewSetup();
            var dirs = new Dirs("plan-exit-reject-none");
            Script(setup, (_, results) => results.Count switch
            {
                0 => Write(Path.Combine(dirs.Plans, "plan.md"), PlanText, "w1"),
                1 => Exit(),
                _ => Done,
            });
            var (harness, root) = await Open(location(), setup, dirs);
            await Enter(root, setup, dirs);
            var (submission, pending) = await UntilPending(root, "plan");
            await PlanMode.RejectAsync(root, pending.TaskId, "   ", Ctx);
            await Settled(submission);
            Equal("The user rejected the plan; you are still in plan mode. Feedback: none. Revise the plan and call exit_plan_mode again.",
                Text((await Results(root, PlanMode.ExitToolName)).Single()), "rejection");
            Check((await State(harness, root))!.Active, "still in plan mode");
            await harness.CloseAsync(Ctx);
        });

        yield return Case(prefix + "exit-not-in-plan", async () =>
        {
            var setup = NewSetup();
            var dirs = new Dirs("plan-exit-not-in-plan");
            Script(setup, (_, results) => results.Count == 0 ? Exit() : Done);
            var (harness, root) = await Open(location(), setup, dirs);
            Check((await root.AgentAsync(Ctx)).Tools.Any(tool => tool.Name == PlanMode.ExitToolName), "exit_plan_mode is always offered");
            await Settled(await Submit(root, "go"));
            var exit = (await Results(root, PlanMode.ExitToolName)).Single();
            Check(IsError(exit), "an error");
            Equal("Not in plan mode; there is no plan to approve.", Text(exit), "result");
            Check(await State(harness, root) is null, "no plan-mode state");
            await harness.CloseAsync(Ctx);
        });

        yield return Case(prefix + "exit-planfile-outside", async () =>
        {
            var setup = NewSetup();
            var dirs = new Dirs("plan-exit-planfile");
            var outside = Path.Combine(dirs.Outside, "plan.md");
            File.WriteAllText(outside, "# Not the plan");
            Script(setup, (_, results) => results.Count switch
            {
                0 => Exit(Obj(("planFile", outside)), "x1"),
                1 => Exit(Obj(("planFile", ".." + Path.DirectorySeparatorChar + "escape.md")), "x2"),
                2 => Exit(null, "x3"),
                _ => Done,
            });
            var (harness, root) = await Open(location(), setup, dirs);
            await Enter(root, setup, dirs);
            await Settled(await Submit(root, "go"));
            var exits = await Results(root, PlanMode.ExitToolName);
            Equal(3, exits.Count, "results");
            Check(exits.All(IsError), "errors");
            Check(Text(exits[0]).Contains("is outside the plan directory", StringComparison.Ordinal), Text(exits[0]));
            Check(Text(exits[1]).Contains("is outside the plan directory", StringComparison.Ordinal), Text(exits[1]));
            Check(Text(exits[2]).Contains("does not exist. Write the plan there first, or pass it as plan.", StringComparison.Ordinal), Text(exits[2]));
            var state = await State(harness, root);
            Check(state!.Active && state.Submissions.IsEmpty, "nothing submitted");
            await harness.CloseAsync(Ctx);
        });

        yield return Case(prefix + "restart-awaiting-approval", async () =>
        {
            var open = location();
            var setup = NewSetup();
            var dirs = new Dirs("plan-restart");
            PlanTurn(setup, dirs);
            var (harness, root) = await Open(open, setup, dirs);
            await Enter(root, setup, dirs);
            var (submission, pending) = await UntilPending(root, "plan");
            var submissionId = submission.Id;
            await harness.CloseAsync(Ctx);

            (harness, root) = await Open(open, setup, dirs, first: false);
            var again = (await WaitPending(root))[0];
            Equal(pending.TaskId, again.TaskId, "same call");
            Equal(pending.Details.ToJson(), again.Details.ToJson(), "same details");
            await PlanMode.ApproveAsync(root, setup.Registry, again.TaskId, stop: false, Ctx);
            Equal(SubmissionStatus.Done, (await Settled((await harness.SubmissionAsync(submissionId, Ctx))!)).Status, "submission");
            Equal(1, (await Results(root, "write")).Count, "one write result");
            Equal(Approved(PlanFile(dirs), PlanText), Text((await Results(root, PlanMode.ExitToolName)).Single()), "one approval result");
            Equal(1, (await State(harness, root))!.Submissions.Length, "one submission");
            await NoLiveTasks(harness);
            await harness.CloseAsync(Ctx);
        });

        yield return Case(prefix + "approved-before-settle", async () =>
        {
            var open = location();
            var setup = NewSetup();
            var dirs = new Dirs("plan-approved-before-settle");
            PlanTurn(setup, dirs);
            // The approval lands; the exit call's result commit fails in Storage, which fails and closes the Session.
            var failing = new CrashStorage(open(), writes => writes.OfType<EntryWrite>().Any(write => write.Value.Kind == "pi.tool-result" &&
                write.Value.Model is [JsonObjectValue message, ..] && message["toolName"] is JsonStringValue { Value: PlanMode.ExitToolName }),
                after: false, "exit-settle-failed", fail: true);
            var (harness, root) = await Open(() => failing, setup, dirs);
            await Enter(root, setup, dirs);
            var (submission, pending) = await UntilPending(root, "plan");
            var submissionId = submission.Id;
            await PlanMode.ApproveAsync(root, setup.Registry, pending.TaskId, stop: false, Ctx);
            await ClosedByFailure(harness);

            (harness, root) = await Open(open, setup, dirs, first: false);
            // The rerun finds the approval although plan mode already ended, and settles once.
            Equal(SubmissionStatus.Done, (await Settled((await harness.SubmissionAsync(submissionId, Ctx))!)).Status, "submission");
            Equal(Approved(PlanFile(dirs), PlanText), Text((await Results(root, PlanMode.ExitToolName)).Single()), "one approval result");
            Equal(1, (await Results(root, "write")).Count, "one write result");
            var state = await State(harness, root);
            Check(!state!.Active && state.Approved?.TaskId == pending.TaskId, "approved");
            Check(!PlanMode.IsActive(await AgentDoc(harness, root)), "agent restored");
            await NoLiveTasks(harness);
            await harness.CloseAsync(Ctx);
        });

        yield return Case(prefix + "manual-exit-cancels-pending", async () =>
        {
            var setup = NewSetup();
            var dirs = new Dirs("plan-manual-exit");
            PlanTurn(setup, dirs);
            var (harness, root) = await Open(location(), setup, dirs);
            await Enter(root, setup, dirs);
            var (submission, pending) = await UntilPending(root, "plan");
            await PlanMode.ExitAsync(root, setup.Registry, Ctx);
            Equal(SubmissionStatus.Done, (await Settled(submission)).Status, "submission");
            var exit = (await Results(root, PlanMode.ExitToolName)).Single();
            Equal("The user left plan mode without reviewing the plan. Plan mode is off; wait for the user's instructions.", Text(exit), "result");
            MatchJson("""{"details":{"state":"cancelled"}}""", exit, "details");
            Equal(2, setup.Faux.State.CallCount, "the run ended");
            var state = await State(harness, root);
            Check(!state!.Active, "plan mode off");
            Equal("cancelled", state.Submissions.Single().Status, "submission cancelled");
            Check(!PlanMode.IsActive(await AgentDoc(harness, root)), "agent restored");
            await ThrowsAsync<InteractionNotPendingException>(() => PlanMode.ApproveAsync(root, setup.Registry, pending.TaskId, false, Ctx), "approve after exit");
            await harness.CloseAsync(Ctx);
        });

        yield return Case(prefix + "fork-as-of", async () =>
        {
            var setup = NewSetup();
            var dirs = new Dirs("plan-fork");
            Script(setup, (_, _) => Done);
            var (harness, root) = await Open(location(), setup, dirs);
            await Settled(await Submit(root, "one"));
            var before = (await AllEntries(root)).Last(entry => entry.Kind == "pi.assistant");
            await Enter(root, setup, dirs);
            await Settled(await Submit(root, "two"));
            var during = (await AllEntries(root)).Last(entry => entry.Kind == "pi.assistant");
            var early = await root.ForkAsync(before.Id, new ConversationCreateOptions(ConversationOwnership.Ownerless), Ctx);
            var late = await root.ForkAsync(during.Id, new ConversationCreateOptions(ConversationOwnership.Ownerless), Ctx);
            Check(await State(harness, early) is not { Active: true }, "a fork before plan mode is not in plan mode");
            Check(!PlanMode.IsActive(await AgentDoc(harness, early)), "its agent does not select plan-mode");
            Check(!(await early.AgentAsync(Ctx)).Extensions.Any(extension => extension.Name == PlanMode.ExtensionName), "early agent");
            var lateState = await State(harness, late);
            Check(lateState is { Active: true } && lateState.Directory == Path.GetFullPath(dirs.Plans), "a fork in plan mode keeps it");
            Check(PlanMode.IsActive(await AgentDoc(harness, late)), "its agent selects plan-mode");
            // Leaving plan mode in the late fork restores its own saved selection and leaves the parent planning.
            await PlanMode.ExitAsync(late, setup.Registry, Ctx);
            Check(!PlanMode.IsActive(await AgentDoc(harness, late)), "late fork left");
            Check(PlanMode.IsActive(await AgentDoc(harness, root)), "parent still planning");
            await harness.CloseAsync(Ctx);
        });

        yield return Case(prefix + "wrapped-write-after-approval-still-restricted", async () =>
        {
            var setup = NewSetup();
            var dirs = new Dirs("plan-wrapped-after");
            PlanTurn(setup, dirs);
            var (harness, root) = await Open(location(), setup, dirs);
            await Enter(root, setup, dirs);
            // A write resolved while planning, which executes only after the plan was approved.
            var wrapped = (await root.AgentAsync(Ctx)).Tools.Single(tool => tool.Name == "write");
            var (submission, pending) = await UntilPending(root, "plan");
            await PlanMode.ApproveAsync(root, setup.Registry, pending.TaskId, stop: false, Ctx);
            await Settled(submission);
            Check(!(await State(harness, root))!.Active, "plan mode ended");
            var api = new ReaderApi(dirs.Env, harness, root.Id);
            var outside = Path.Combine(dirs.Outside, "late.txt");
            var denied = await ThrowsAsync<PiSharp.Durable.Env.FileError>(() =>
                wrapped.Execute(Obj(("path", outside), ("content", "x")), api, Ctx), "outside");
            Check(denied.Message.Contains("is outside the plan directory", StringComparison.Ordinal), denied.Message);
            Check(!File.Exists(outside), "not written");
            var inside = Path.Combine(dirs.Plans, "late.md");
            await wrapped.Execute(Obj(("path", inside), ("content", "ok")), api, Ctx);
            Equal("ok", File.ReadAllText(inside), "still allowed inside");
            await harness.CloseAsync(Ctx);
        });
    }

    private static string Js(string text) => JsonValue.String(text).ToJson();
}
