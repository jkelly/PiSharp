// Decision 0011 (run W4, integration fixes): the fixes after the W4 review that are not request options (RunOptionCases has those), each
// with the case that would have caught it. RunQuery filters top-level runs and stop reasons, so History's plain counts are the store's; a
// ledger row of a later send (the Spend Guard's op~n) opens the op's run, and an agent compare model's ledger rows open their runs.db rows
// through /runs/{op} in a running app (faux). Every account route shares one provider handler when none is registered (R0's choice,
// confirmed): redirects off, no cookies, pooled connections renewed every 5 minutes. A faux prompt "error: <message>" ends the answer with a provider error, so the run engine's
// error path (status, message, the released reservation) runs offline. No case calls a provider.
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using PiSharp.Durable.Hosting.Testing;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Routing;
using PiSharp.WebAgent.Runs;
using PiSharp.WebAgent.Spend;

internal static partial class Program
{
    internal static IEnumerable<(string, Func<Task>)> W4IntegrationFixCases() =>
    [
        Case("w4.fix.run-query-filters-top-level-runs-and-stop-reasons", () =>
        {
            var root = Temp("w4-fix-run-query");
            try
            {
                var store = new RunStore(new RunsDb(Path.Join(root, RunsDb.FileName)));
                var now = DateTimeOffset.UtcNow;
                store.Insert(RunsRow("solo", now.AddMinutes(-5)) with { StopReason = "stop" });
                store.Insert(RunsRow("long", now.AddMinutes(-4)) with { StopReason = "length", Pinned = true });
                store.Insert(RunsRow("failed", now.AddMinutes(-3), status: RunStatus.Error) with { StopReason = "error" });
                store.Insert(AgentCompareRow("ac1", now.AddMinutes(-2), null));
                store.Insert(AgentCompareRow("ac1-a", now.AddMinutes(-2), "ac1") with { StopReason = "finished" });
                store.Insert(RunsRow("ac1-a-call1", now.AddMinutes(-1)) with { Source = "agent-compare", ParentId = "ac1-a", StopReason = "stop" });

                var top = store.List(new RunQuery { TopLevelOnly = true });
                Equal("ac1,failed,long,solo", string.Join(",", top.Items.Select(run => run.Id)), "top-level runs only, newest first");
                Equal(4L, top.Total, "counted the same way");
                Equal(4L, store.Totals(new RunQuery { TopLevelOnly = true }).Count, "Totals takes the filter");
                Equal(6L, store.Totals(new RunQuery()).Count, "without it, every row");
                Equal("ac1-a-call1,solo", string.Join(",", store.List(new RunQuery { StopReason = "stop" }).Items.Select(run => run.Id)), "a stop reason at any level");
                Equal("solo", string.Join(",", store.List(new RunQuery { StopReason = "stop", TopLevelOnly = true }).Items.Select(run => run.Id)), "both filters");
                Equal("long", string.Join(",", store.List(new RunQuery { StopReason = "length" }).Items.Select(run => run.Id)), "length");
                Equal(1L, store.Totals(new RunQuery { TopLevelOnly = true, Pinned = true }).Count, "History's pinned count");
                var errors = store.Totals(new RunQuery { TopLevelOnly = true, StopReason = "error" });
                Equal((1L, 1L), (errors.Count, errors.Errors), "an error's totals");
            }
            finally
            {
                SqliteClearPools();
                DeleteTemp(root);
            }
        }),

        Case("w4.fix.a-later-send-of-an-op-opens-its-run", () =>
        {
            var root = Temp("w4-fix-later-send");
            try
            {
                Equal("abc", RunHistoryText.SendOf("abc~2"), "the guard's second send");
                Equal("abc", RunHistoryText.SendOf("abc~12"), "any later send");
                Equal("abc~x", RunHistoryText.SendOf("abc~x"), "not a send number");
                Equal("abc~", RunHistoryText.SendOf("abc~"), "no number");
                Equal("~2", RunHistoryText.SendOf("~2"), "no op before it");
                Equal("abc", RunHistoryText.SendOf("abc"), "a first send");

                Reservation Row(string op, SpendSource source) => new()
                {
                    Op = op, Provider = "faux", Model = "echo", Source = source, Billing = BillingKind.Metered, State = ReservationState.Settled,
                    Created = DateTimeOffset.UtcNow,
                };
                Equal("/runs/r1", RunHistoryText.LedgerRunHref(Row("run-r1~3", SpendSource.Playground)), "a run engine op's later send");
                Equal("/runs/op-ac", RunHistoryText.LedgerRunHref(Row("op-ac~2", SpendSource.AgentCompare)), "an agent compare turn's later send");
                Equal("/runs/op-ac", RunHistoryText.LedgerRunHref(Row("op-ac", SpendSource.AgentCompare)), "its first send");
                Check(RunHistoryText.LedgerRunHref(Row("op-chat~2", SpendSource.Chat)) is null, "a chat's row opens the chat");

                var store = new RunStore(new RunsDb(Path.Join(root, RunsDb.FileName)));
                store.Insert(RunsRow("call1", DateTimeOffset.UtcNow) with { Op = "op-ac", Source = "agent-compare" });
                using var services = new ServiceCollection().AddSingleton(store).BuildServiceProvider();
                var inspector = new RunInspectorPages(services);
                Equal("call1", inspector.Find("op-ac~2")?.Id, "/runs/{op~n} finds the op's run");
                Equal("call1", inspector.Find("op-ac")?.Id, "and /runs/{op}");
                Check(inspector.Find("nope~2") is null, "an unknown op is still unknown");
            }
            finally
            {
                SqliteClearPools();
                DeleteTemp(root);
            }
        }),

        Case("w4.fix.every-route-shares-one-cookieless-provider-handler", () =>
        {
            using (var inner = RunCaptureHandler.CreateDefaultInner())
            {
                var sockets = inner as SocketsHttpHandler ?? throw new InvalidOperationException("a SocketsHttpHandler: " + inner.GetType().Name);
                Check(!sockets.AllowAutoRedirect, "redirects off, as the routes made their own");
                Check(!sockets.UseCookies, "no cookies, so one account's never reach another's requests");
                Check(sockets.PooledConnectionLifetime > TimeSpan.Zero && sockets.PooledConnectionLifetime <= TimeSpan.FromMinutes(5),
                    "pooled connections are renewed, so a provider's DNS change is picked up: " + sockets.PooledConnectionLifetime);
            }

            // With no handler registered, the hook makes the shared one and owns it; a registered handler is wrapped and never disposed.
            var hook = new RunCaptureHook();
            var shared = hook.Wrap(null);
            Check(ReferenceEquals(shared, hook.Wrap(null)), "one hook, so every route shares one handler");
            Check(shared.InnerHandler is SocketsHttpHandler { UseCookies: false }, "over the cookieless default");
            hook.Dispose();
            Throws<ObjectDisposedException>(() => hook.Wrap(null), "the app's end disposes it");

            using var registered = new AnswerOk();
            using var outer = new RunCaptureHook();
            var wrapped = outer.Wrap(registered);
            Check(ReferenceEquals(wrapped.InnerHandler, registered), "a registered handler is the hook's inner one");
            outer.Dispose();
            using var still = new HttpMessageInvoker(registered, disposeHandler: false);
            using var answer = still.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://localhost/"), default).GetAwaiter().GetResult();
            Check(answer.IsSuccessStatusCode, "and stays usable after the hook is disposed");
        }),

        Case("w4.fix.the-worktree-chip-names-the-chosen-base", () =>
        {
            var inspection = new PiSharp.WebAgent.AgentCompare.Workspaces.ProjectInspection
            {
                ProjectPath = "C:\\project", GitAvailable = true, IsGitRepo = true, Kind = PiSharp.WebAgent.AgentCompare.Workspaces.WorkspaceKind.Worktree,
                CurrentBranch = "main", HeadCommit = "4f9c2e1aa0b1c2d3e4f5a6b7c8d9e0f1a2b3c4d5", ShortCommit = "4f9c2e1", Branches = ["main", "release"],
                WarnAboveBytes = 1, WorkspacesRoot = "C:\\data\\workspaces",
            };
            PiSharp.WebAgent.AgentCompare.Pages.AgentProjectView View(string? baseRef) => new()
            {
                ProjectPath = "C:\\project",
                Inspection = inspection,
                Workspace = new PiSharp.WebAgent.AgentCompare.Pages.AgentWorkspaceView { ProjectPath = "C:\\project", Directory = "C:\\data\\workspaces", BaseRef = baseRef },
            };
            Equal("git worktree from main @ 4f9c2e1", View(null).ChipLabel, "the current branch by default");
            Equal("git worktree from main @ 4f9c2e1", View("main").ChipLabel, "the current branch chosen");
            Equal("git worktree from release", View("release").ChipLabel, "another branch");
            Equal("git worktree from 1a2b3c4", View("1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d7e8f9a0b").ChipLabel, "a commit, abbreviated");
            Equal("temporary copy", (View("release") with { Inspection = inspection with { Kind = PiSharp.WebAgent.AgentCompare.Workspaces.WorkspaceKind.Copy } }).ChipLabel,
                "a copy has no base ref");
        }),

        Case("w4.fix.a-faux-error-prompt-fails-the-run-and-releases-its-reservation", async () =>
        {
            await using var host = RunsHost.Faux("w4-fix-faux-error");
            var start = await host.Engine.StartAsync(new RunSpec
            {
                Provider = "faux",
                Model = "echo",
                Messages = [new RunMessage { Text = "error: The upstream said no." }],
            });
            Check(start.Posted, "posted: " + start.Refusal?.Message);
            var run = await RunsFinishedAsync(host.Store, start.Id!);
            Equal(RunStatus.Error, run.Status, "the run failed");
            Equal("The upstream said no.", run.Error, "with the provider's message");
            Equal("error", run.StopReason, "stop reason error");
            Check(string.IsNullOrEmpty(run.OutputText), "no answer: " + run.OutputText);
            var ledger = (await host.Services.GetRequiredService<ISpendLedger>().GetAsync(run.Op))!;
            Equal(ReservationState.Released, ledger.State, "a failed answer with no usage releases its reservation: " + ledger);

            var plain = await host.Engine.StartAsync(new RunSpec { Provider = "faux", Model = "echo", Messages = [new RunMessage { Text = "errors: are echoed" }] });
            Equal("Echo: errors: are echoed", (await RunsFinishedAsync(host.Store, plain.Id!)).OutputText, "only the exact prefix fails");
            Equal(FauxDurableModels.ErrorPrefix, "error:", "the documented prefix");
        }),

        Case("w4.fix.agent-compare-ledger-rows-open-their-runs", async () =>
        {
            await using var app = await CompareApp.StartAsync("w4-fix-ac-ledger", ToolApprovalMode.Auto, metered: true);
            var run = await app.CreateAsync(new JsonObject
            {
                ["requestId"] = "ledger-1",
                ["task"] = "!echo hi > hi.txt",
                ["projectPath"] = app.Project,
                ["models"] = Models("echo"),
                ["approval"] = "auto",
            });
            var finished = await app.WaitAsync(run.Id(), status => status.Str("state") == "finished", "the run finished");
            Equal("finished", finished.Models().Single().Str("outcome"), "the model finished");
            var ops = ReadDb(Path.Join(app.Data, "webagent.db"), "SELECT op FROM spend_reservations WHERE source = 'agent-compare' ORDER BY created_at", row => row.GetString(0));
            Check(ops.Count >= 2, "the model's turns are in the ledger: " + string.Join(", ", ops));

            // History › Spend links each of them to the inspector, which redirects the op to the call's own address.
            var spend = (await app.App.Get("/history/spend")).Text;
            var model = run.Id() + "-a";
            foreach (var op in ops)
            {
                var link = Regex.Match(spend, $"href=\"(/runs/{Regex.Escape(Uri.EscapeDataString(op))})\"[^>]*data-ledger-run");
                Check(link.Success, $"the ledger row of {op} links to its run");
                var opened = await app.App.Get(link.Groups[1].Value);
                Equal(200, opened.Status, $"GET {link.Groups[1].Value}");
                var id = opened.Response.RequestMessage!.RequestUri!.AbsolutePath["/runs/".Length..];
                var row = ReadDb(Path.Join(app.Data, "runs.db"), $"SELECT op, parent_id, kind, source FROM runs WHERE id = '{id}'",
                    reader => (reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3))).Single();
                Equal((op, model, "call", "agent-compare"), row, "the op opens the model's call");
            }
            Check(!app.App.Log.Contains("fail:", StringComparison.Ordinal), "no server errors: " + app.App.Log);
        }),
    ];

    /// <summary>A provider handler that answers 200 at once.</summary>
    private sealed class AnswerOk : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }
}
