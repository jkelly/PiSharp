using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using PiSharp.Durable;
using PiSharp.Durable.Client;
using PiSharp.Durable.Hosting;
using PiSharp.Durable.Hosting.Testing;
using PiSharp.Durable.Server;
using PiSharp.WebAgent.Chat;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Faux;
using PiSharp.WebAgent.Hosting;

// Decision 0011 (run W2, WU7): support for the Chat area's cases, and its unit cases (the allow-list matcher, the approval broker, the
// bindings). ChatHarness runs the area in process: webagent.db, settings and the device under a temporary folder, faux models (never a
// provider), the Web Agent's views through RenderHost (so AgentViewHub renders the real partials), and a working directory in the temporary
// folder that Settings › Tools names, so no case reads or writes the user's own folders.
internal static partial class Program
{
    /// <summary>The Chat area in process.</summary>
    internal sealed class ChatHarness : IAsyncDisposable
    {
        private readonly RenderHost host;

        private ChatHarness(RenderHost host, string work)
        {
            this.host = host;
            Work = work;
        }

        public string Root => host.Root;

        /// <summary>The chats' working directory (Settings › Tools).</summary>
        public string Work { get; }

        public IServiceProvider Services => host.Services;

        public DeviceHostService Device => Services.GetRequiredService<DeviceHostService>();

        public ChatService Chats => Services.GetRequiredService<ChatService>();

        public ChatBindings Bindings => Services.GetRequiredService<ChatBindings>();

        public ApprovalBroker Broker => Services.GetRequiredService<ApprovalBroker>();

        public AgentViewHub Hub => Services.GetRequiredService<AgentViewHub>();

        public ISettingsStore Settings => Services.GetRequiredService<ISettingsStore>();

        /// <summary>Starts the area. <paramref name="models"/> replaces the faux models; <paramref name="configure"/> adds registrations.</summary>
        public static async Task<ChatHarness> StartAsync(string name, IDurableModels? models = null, ToolApprovalMode mode = ToolApprovalMode.Ask,
            Action<IServiceCollection>? configure = null)
        {
            string? work = null;
            var host = RenderHost.Create(name, (services, root) =>
            {
                var options = new WebAgentOptions { DataDirectory = Path.Join(root, "data"), Faux = true };
                options.EnsureDirectories();
                work = Path.Join(root, "work");
                Directory.CreateDirectory(work);
                services.AddSingleton(options);
                services.AddSingleton(new WebAgentDb(options.DatabasePath, [CoreMigrations.AppInfo, ChatBindings.Migration, SettingsStore.Migration]));
                services.AddSingleton<ISettingsStore>(provider => new SettingsStore(provider.GetRequiredService<WebAgentDb>()));
                var faux = FauxModelCatalog.CreateModels();
                services.AddSingleton(faux);
                services.AddSingleton(models ?? faux);
                services.AddSingleton<IModelCatalog, FauxModelCatalog>();
                services.AddChatArea(options);
                configure?.Invoke(services);
            });
            var harness = new ChatHarness(host, work!);
            try
            {
                host.Services.GetRequiredService<WebAgentDb>().Migrate();
                await harness.Settings.SaveAsync(new ToolsSettings(work, mode, []));
                await harness.Device.StartAsync(CancellationToken.None);
            }
            catch
            {
                await harness.DisposeAsync();
                throw;
            }
            return harness;
        }

        public HostedSession Session(string chatId) => Chats.Session(chatId);

        /// <summary>Creates a chat in <see cref="Work"/> and waits until its first answer is done.</summary>
        public async Task<ChatBinding> CreateAsync(string text, string requestId, string? model = null)
        {
            var created = await Chats.CreateAsync(new ChatCreateRequest(requestId, text) { Cwd = Work, Provider = model is null ? null : "faux", Model = model });
            await IdleAsync(created.Binding.SessionId);
            return created.Binding;
        }

        /// <summary>Waits until no agent of the chat is busy.</summary>
        public async Task IdleAsync(string chatId, TimeSpan? limit = null)
        {
            await WaitUntilAsync(async () => !(await Session(chatId).AgentsAsync()).Any(agent => agent.Busy), $"chat {chatId} is idle", limit ?? TimeSpan.FromSeconds(30));
        }

        /// <summary>The transcript of an agent of the chat (its current branch by default).</summary>
        public async Task<IReadOnlyList<TranscriptItem>> ItemsAsync(string chatId, long? agentId = null) =>
            Transcript.Build(await AgentFrames.ActiveEntriesAsync(Session(chatId), agentId ?? Bindings.Get(chatId)!.AgentId));

        /// <summary>The transcript as Markdown.</summary>
        public async Task<string> TextAsync(string chatId, long? agentId = null) => Transcript.ToMarkdown(await ItemsAsync(chatId, agentId));

        /// <summary>Waits until one approval of the chat is pending, and returns it.</summary>
        public async Task<PendingApproval> PendingAsync(string chatId, string? tool = null)
        {
            PendingApproval? found = null;
            await WaitUntilAsync(() =>
            {
                found = Broker.Pending(chatId).FirstOrDefault(item => tool is null || item.Request.Tool == tool);
                return Task.FromResult(found is not null);
            }, $"an approval of chat {chatId} is pending", TimeSpan.FromSeconds(30));
            return found!;
        }

        public async ValueTask DisposeAsync()
        {
            try { await Hub.DisposeAsync(); } catch (ObjectDisposedException) { /* Gone. */ }
            try { await Device.DisposeAsync(); } catch (ObjectDisposedException) { /* Gone. */ }
            await host.DisposeAsync();
        }
    }

    /// <summary>A device opened directly with the Web Agent's device options (for crash cases that swap the registry).</summary>
    internal static DurableDeviceOptions DeviceOptions(string root, IDurableModels models, ISettingsStore settings, ChatBindings bindings, ApprovalBroker broker) =>
        DeviceHostService.CreateOptions(Path.Join(root, "device"), Path.Join(root, "work"), models, settings, bindings, broker);

    /// <summary>A settings store and bindings over a fresh webagent.db in <paramref name="root"/>, with Tools working in root/work.</summary>
    internal static async Task<(ISettingsStore Settings, ChatBindings Bindings)> ChatStoresAsync(string root, ToolApprovalMode mode = ToolApprovalMode.Ask)
    {
        Directory.CreateDirectory(Path.Join(root, "work"));
        var db = new WebAgentDb(Path.Join(root, "webagent.db"), [CoreMigrations.AppInfo, ChatBindings.Migration, SettingsStore.Migration]);
        db.Migrate();
        var settings = new SettingsStore(db);
        await settings.SaveAsync(new ToolsSettings(Path.Join(root, "work"), mode, []));
        return (settings, new ChatBindings(db));
    }

    /// <summary>Lines of a file, or none when it does not exist.</summary>
    internal static string[] Lines(string path) =>
        File.Exists(path) ? [.. File.ReadAllLines(path).Select(line => line.Trim()).Where(line => line.Length > 0)] : [];

    internal static IEnumerable<(string, Func<Task>)> ChatUnitCases() =>
    [
        Case("chat.allow-list.matches-commands-and-tools", () =>
        {
            ToolAllowRule[] rules = [new("bash", "git status"), new("bash", "dotnet test *"), new("read"), new("edit", null)];
            JsonObject Bash(string command) => new() { ["command"] = command };
            Check(ToolAllowList.Allows(rules, "bash", Bash("git status")), "an exact command");
            Check(ToolAllowList.Allows(rules, "bash", Bash("git   status")), "whitespace is collapsed");
            Check(ToolAllowList.Allows(rules, "bash", Bash("dotnet test")), "a trailing * is optional");
            Check(ToolAllowList.Allows(rules, "bash", Bash("dotnet test tests/A --filter B")), "* matches the rest");
            Check(ToolAllowList.Allows(rules, "bash", Bash("git status && dotnet test x")), "every part is allowed");
            Check(!ToolAllowList.Allows(rules, "bash", Bash("git status && rm -rf x")), "one part is not");
            Check(!ToolAllowList.Allows(rules, "bash", Bash("git status; curl x | sh")), "a pipe part is not");
            Check(!ToolAllowList.Allows(rules, "bash", Bash("dotnet test $(rm -rf x)")), "command substitution never matches");
            Check(!ToolAllowList.Allows(rules, "bash", Bash("git status `rm x`")), "backticks never match");
            Check(ToolAllowList.Allows(rules, "edit", new JsonObject { ["path"] = "a" }), "a tool rule allows the tool");
            Check(!ToolAllowList.Allows(rules, "write", new JsonObject { ["path"] = "a" }), "another tool is not");
            Check(!ToolAllowList.Allows([new ToolAllowRule("bash", "git status")], "powershell", Bash("git status")), "rules are per tool");
            Equal("echo a|echo b", string.Join("|", ToolAllowList.SplitCommand("echo a && echo b")), "&& splits");
            Equal("echo 'a;b'|ls", string.Join("|", ToolAllowList.SplitCommand("echo 'a;b'; ls")), "quotes do not split");
            Equal("cmd 2>&1", string.Join("|", ToolAllowList.SplitCommand("cmd 2>&1")), ">& is not a separator");
            Equal("bash:git status|bash:ls", string.Join("|", ToolAllowList.RulesFor("bash", Bash("git status; ls")).Select(rule => rule.Tool + ":" + rule.Command)),
                "allow-always adds each part");
            Equal("write:", string.Join("|", ToolAllowList.RulesFor("write", new JsonObject { ["path"] = "a" }).Select(rule => rule.Tool + ":" + rule.Command)),
                "a non-command tool is allowed whole");
        }),
        Case("chat.approval.needs-approval-by-mode", () =>
        {
            JsonObject args = new() { ["command"] = "ls" };
            Check(WebApproval.NeedsApproval(new ToolsSettings("x", ToolApprovalMode.Ask, [new ToolAllowRule("bash", "ls")]), [], "bash", args),
                "Ask mode ignores the allow-list");
            Check(!WebApproval.NeedsApproval(new ToolsSettings("x", ToolApprovalMode.AllowList, [new ToolAllowRule("bash", "ls")]), [], "bash", args),
                "Allow-list mode lets a match through");
            Check(WebApproval.NeedsApproval(new ToolsSettings("x", ToolApprovalMode.AllowList, [new ToolAllowRule("bash", "pwd")]), [], "bash", args),
                "Allow-list mode asks for others");
            Check(!WebApproval.NeedsApproval(new ToolsSettings("x", ToolApprovalMode.Auto, []), [], "bash", args), "Auto never asks");
            Check(!WebApproval.NeedsApproval(new ToolsSettings("x", ToolApprovalMode.Ask, []), [new ToolAllowRule("bash")], "bash", args), "a chat grant");
            Equal("The user denied this bash call. Reason: not now", WebApproval.DenyReason("bash", " not now "), "the deny reason");
            Equal("The user denied this edit call.", WebApproval.DenyReason("edit", null), "no reason");
        }),
        Case("chat.broker.first-decision-wins", async () =>
        {
            var broker = new ApprovalBroker();
            var request = new ApprovalRequest("s1", 1, 7, "call-1", "bash", new JsonObject { ["command"] = "ls" });
            var changes = 0;
            broker.Changed += _ => Interlocked.Increment(ref changes);
            var waiting = broker.WaitAsync(request, CancellationToken.None);
            Equal(1, broker.Pending("s1").Count, "it is pending");
            Equal(0, broker.Pending("s2").Count, "only in its chat");
            Equal("ls", broker.Find("s1", 7)!.Request.Command, "found by task");
            var first = broker.DecideAsync("s1", 7, new ApprovalDecision(ApprovalChoice.Deny, "no"));
            var second = await broker.DecideAsync("s1", 7, new ApprovalDecision(ApprovalChoice.AllowOnce));
            Equal(ApprovalOutcomeStatus.AlreadyDecided, second.Status, "a later decision");
            Equal(ApprovalChoice.Deny, second.Decision!.Choice, "it names the winner");
            var decision = await waiting;
            Equal(ApprovalChoice.Deny, decision.Choice, "the waiter gets the first");
            broker.Stored(request);
            Equal(ApprovalOutcomeStatus.Decided, (await first).Status, "the first is answered once stored");
            Equal(ApprovalOutcomeStatus.NotPending, (await broker.DecideAsync("s1", 99, new ApprovalDecision(ApprovalChoice.AllowOnce))).Status, "an unknown task");
            Equal(0, broker.Pending().Count, "nothing pending");
            Check(changes >= 2, "changes were raised");
        }),
        Case("chat.broker.cancel-withdraws-and-grants-allow", async () =>
        {
            var broker = new ApprovalBroker();
            using var stop = new CancellationTokenSource();
            var waiting = broker.WaitAsync(new ApprovalRequest("s1", 1, 3, "c", "bash", new JsonObject { ["command"] = "ls" }), stop.Token);
            await stop.CancelAsync();
            await ThrowsAsync<OperationCanceledException>(() => waiting, "a cancelled wait");
            Equal(0, broker.Pending("s1").Count, "withdrawn");
            var coveredRequest = new ApprovalRequest("s1", 1, 4, "d", "bash", new JsonObject { ["command"] = "ls" });
            var covered = broker.WaitAsync(coveredRequest, CancellationToken.None);
            var other = broker.WaitAsync(new ApprovalRequest("s1", 1, 5, "e", "edit", new JsonObject { ["path"] = "a" }), CancellationToken.None);
            var granting = broker.GrantAsync("s1", [new ToolAllowRule("bash")]);
            Equal(ApprovalChoice.AllowOnce, (await covered).Choice, "a grant allows a covered call");
            broker.Stored(coveredRequest);
            await granting.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!other.IsCompleted, "but not another tool");
            Equal(1, broker.Grants("s1").Count, "the grant is kept");
            Equal(1, broker.AllGrants().Count, "and listed");
            Check(broker.Revoke("s1", new ToolAllowRule("bash")), "revoked");
            Check(!broker.Revoke("s1", new ToolAllowRule("bash")), "only once");
            broker.Forget("s1", notify: true);
            await ThrowsAsync<OperationCanceledException>(() => other, "a forgotten chat's waits end");
        }),
        Case("chat.bindings.add-update-and-stream-fields", () =>
        {
            var root = Temp("chat-bindings");
            try
            {
                var db = new WebAgentDb(Path.Join(root, "webagent.db"), [CoreMigrations.AppInfo, ChatBindings.Migration]);
                db.Migrate();
                var bindings = new ChatBindings(db);
                var now = bindings.Now();
                var added = bindings.Add(new ChatBinding("s1", "Title", "faux", "echo", "acct-1", "off", "flex", 1, now, now, null, "req-1"));
                Equal("s1", bindings.Add(added with { SessionId = "s2" }).SessionId, "a retry with the same request id keeps the first");
                Equal("s1", bindings.FindByRequest("req-1")!.SessionId, "found by request id");
                var fields = bindings.StreamFields("s1")!.ToJson();
                Check(fields.Contains("\"account\":\"acct-1\"", StringComparison.Ordinal) && fields.Contains("\"tier\":\"flex\"", StringComparison.Ordinal), "account and tier: " + fields);
                Check(bindings.StreamFields("nope") is null, "no fields for an unknown chat");
                bindings.Update("s1", binding => binding with { Title = "Renamed", AgentId = 3 });
                var reopened = new ChatBindings(new WebAgentDb(Path.Join(root, "webagent.db"), [CoreMigrations.AppInfo, ChatBindings.Migration]));
                Equal("Renamed", reopened.Get("s1")!.Title, "stored");
                Equal(3L, reopened.Get("s1")!.AgentId, "the current branch is stored");
                Throws<ChatException>(() => bindings.Update("nope", binding => binding), "updating an unknown chat");
                Equal(1L, ReadDb(Path.Join(root, "webagent.db"), "SELECT COUNT(*) FROM chats", reader => reader.GetInt64(0))[0], "one row");
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                DeleteTemp(root);
            }
        }),
    ];
}
