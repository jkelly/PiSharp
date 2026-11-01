// Decision 0011 (after the W5 live test): fixes for what the walkthrough on real models noted and left. A custom provider whose models.json
// key is an environment variable or none keeps no account, so suites read "Active account", presets "the provider's active account" and
// agent compare named nothing: ProviderDirectory.CustomKeyLabel names its key instead (env · VAR, no key needed), as the Playground does, and
// leaves a stored key (an account), an unset variable and a built-in provider alone. Agent compare's note says the label is a key.
// Agent compare counted the __pycache__ and .pytest_cache a check command (pytest) wrote into a workspace when the project has no
// .gitignore: WorkspaceManagerOptions.DefaultExcludes skips Python's caches as it skips bin and obj, so the summary, the diff modal, the kept
// patch and Keep hold the model's changes only.
using PiSharp.WebAgent.AgentCompare.Pages;
using PiSharp.WebAgent.AgentCompare.Runner;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.CustomProviders;
using PiSharp.WebAgent.Settings;

internal static partial class Program
{
    internal static IEnumerable<(string, Func<Task>)> LiveTestFixCases() =>
    [
        Case("live.fixes.a-keyless-custom-provider-names-its-key-not-an-account", () =>
        {
            static ProviderEntry Entry(CustomKeySource source, string? variable, bool set, ProviderState state) => new()
            {
                Id = "gw", Name = "Gateway", Description = "A custom provider", State = state,
                Custom = new CustomProviderFacts(source, variable, set, "http://127.0.0.1:9/v1", "openai-completions", true, true, null),
            };
            Equal("env · GW_KEY", ProviderDirectory.CustomKeyLabel(Entry(CustomKeySource.Environment, "GW_KEY", true, ProviderState.Environment)), "an environment key");
            Equal("no key needed", ProviderDirectory.CustomKeyLabel(Entry(CustomKeySource.None, null, false, ProviderState.Connected)), "no key");
            Equal<string?>(null, ProviderDirectory.CustomKeyLabel(Entry(CustomKeySource.Stored, null, false, ProviderState.Connected)), "a stored key is an account");
            Equal<string?>(null, ProviderDirectory.CustomKeyLabel(Entry(CustomKeySource.Environment, "GW_KEY", false, ProviderState.NotConfigured)),
                "an unset variable is not connected");
            Equal<string?>(null, ProviderDirectory.CustomKeyLabel(new ProviderEntry { Id = "openai", Name = "OpenAI", Description = "", State = ProviderState.Environment }),
                "a built-in provider has accounts");

            var keyed = new AgentSetupColumnView { Slot = "A", Provider = "gw", Model = "m1", Name = "m1", ProviderName = "Gateway", Known = true, AccountLabel = "no key needed", CustomKey = true };
            Check(keyed.OverrideNote.EndsWith(" Runs on no key needed, the custom provider's key (models.json).", StringComparison.Ordinal), "the note names a key: " + keyed.OverrideNote);
            var account = keyed with { AccountLabel = "Work key", CustomKey = false };
            Check(account.OverrideNote.EndsWith(" Runs on Work key, the account that's active when the run starts.", StringComparison.Ordinal), "an account stays an account: " + account.OverrideNote);
        }),

        Case("live.fixes.b-agent-compare-leaves-out-the-caches-a-check-writes", async () =>
        {
            // The host's project is a folder outside git with no .gitignore, as on the live test; the check writes what pytest would.
            await using var host = await RunnerHost.StartAsync("live-check-caches", TimeProvider.System);
            var store = host.Runner.Store;
            var check = OperatingSystem.IsWindows()
                ? @"mkdir __pycache__ src\__pycache__ .pytest_cache\v & echo x> __pycache__\app.cpython-313.pyc & echo x> src\__pycache__\app.cpython-313.pyc & echo x> .pytest_cache\v\nodeids & echo checked"
                : "mkdir -p __pycache__ src/__pycache__ .pytest_cache/v && echo x > __pycache__/app.cpython-313.pyc && echo x > src/__pycache__/app.cpython-313.pyc && echo x > .pytest_cache/v/nodeids && echo checked";
            var run = await host.Runner.CreateAsync(new AgentCompareCreateRequest
            {
                RequestId = "caches-1",
                Task = "!echo t > t.txt",
                ProjectPath = host.Project,
                Models = [new AgentCompareModelRequest("faux", "echo")],
                Approval = ToolApprovalMode.Auto,
                CheckCommand = check,
            });
            await WaitUntilAsync(() => Task.FromResult(store.Get(run.Id)?.State == AgentRunState.Finished), "the run finished", TimeSpan.FromSeconds(60));
            var model = store.Get(run.Id)!.Models.Single();
            Equal(AgentCheckState.Passed, model.Check?.State, "the check ran: " + model.Check?.Tail);
            Check(File.Exists(Path.Join(model.WorkingDirectory!, "__pycache__", "app.cpython-313.pyc")) &&
                File.Exists(Path.Join(model.WorkingDirectory!, "src", "__pycache__", "app.cpython-313.pyc")) &&
                File.Exists(Path.Join(model.WorkingDirectory!, ".pytest_cache", "v", "nodeids")), "and wrote its caches into the workspace");

            Equal("t.txt", string.Join(",", model.Diff!.Items.Select(file => file.Path)), "the summary holds the model's change only");
            Equal(1, model.Diff.Files, "one file changed");
            Equal("t.txt", string.Join(",", (await host.Runner.DiffAsync(run.Id, model.Slot)).Files.Select(file => file.Path)), "so does the diff modal");
            Equal("t.txt", string.Join(",", host.Runner.StoredDiff(store.Get(run.Id)!, model.Slot)!.Files.Select(file => file.Path)), "and the patch kept with the run");
            Equal("t.txt", string.Join(",", (await host.Runner.PreviewKeepAsync(run.Id, model.Slot)).Files), "Keep would bring the change only");
        }),
    ];
}
