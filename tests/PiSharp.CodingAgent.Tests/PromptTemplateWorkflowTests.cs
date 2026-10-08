using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Interactive;
using PiSharp.Contracts;
using static PromptTemplateDiscoveryTests;

internal static class PromptTemplateWorkflowTests
{
    public const string Prefix = "prompt template workflow ";
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        (Prefix + "RPC loads YAML lists prompts expands idle queues and reaches offline request", Rpc),
        (Prefix + "RPC expansion preserves canonical images through durable submission", Images),
        (Prefix + "RPC cancellation joins its original held read before returning", Cancellation),
        (Prefix + "RPC duplicate template names retain first selection and stderr collision", Collision),
        (Prefix + "one-shot prompt expands YAML template into actual offline request", OneShot),
        (Prefix + "RPC repeated missing paths report once on stderr without stdout pollution", Missing)
    ];

    private static Task Rpc() => RpcCore(false);
    private static Task Images() => RpcCore(true);
    private static async Task RpcCore(bool withImage)
    {
        using var fixture = new Fixture(); await fixture.Create();
        await File.WriteAllTextAsync(fixture.Template, "---\ndescription: 'Review code'\nargument-hint: '[focus]'\n---\nReview $1");
        await fixture.Script("Review the change");
        await using var host = new Host(fixture, fixture.Template);
        await host.Send(new { id = "commands", type = "get_commands" });
        var commands = (await host.Response("commands")).Value.GetProperty("data").GetProperty("commands");
        Equal(1, commands.GetArrayLength()); Equal("prompt", commands[0].GetProperty("source").GetString());
        Equal("review", commands[0].GetProperty("name").GetString()); Equal("Review code", commands[0].GetProperty("description").GetString());
        Equal(false, commands[0].TryGetProperty("argumentHint", out _));
        await host.Send(new { id = "steer", type = "steer", message = "/review steer" });
        var steering = await host.Wait(record => Type(record) == "queue_update" && record.Value.GetProperty("steering").GetArrayLength() == 1);
        Equal("Review steer", steering.Value.GetProperty("steering")[0].GetString());
        Equal("queued", (await host.Response("steer")).Value.GetProperty("data").GetProperty("disposition").GetString());
        await host.Send(new { id = "follow", type = "follow_up", message = "/review follow" });
        var follow = await host.Wait(record => Type(record) == "queue_update" && record.Value.GetProperty("followUp").GetArrayLength() == 1);
        Equal("Review follow", follow.Value.GetProperty("followUp")[0].GetString()); await host.Response("follow");
        await host.Send(new { id = "clear", type = "clear_queue" }); await host.Response("clear");
        if (withImage)
            await host.Send(new { id = "prompt", type = "prompt", message = "/review 'the change'", streamingBehavior = "steer",
                images = new[] { new { type = "image", mimeType = "image/png", data = "AA==" } } });
        else await host.Send(new { id = "prompt", type = "prompt", message = "/review 'the change'", streamingBehavior = "steer" });
        await host.Response("prompt"); await host.Wait(record => Type(record) == "agent_settled");
        await host.Send(new { id = "messages", type = "get_messages" });
        var messages = (await host.Response("messages")).Value.GetProperty("data").GetProperty("messages");
        var user = messages.EnumerateArray().Single(message => message.GetProperty("role").GetString() == "user");
        Equal("Review the change", user.GetProperty("content")[0].GetProperty("text").GetString());
        if (withImage)
        {
            var image = user.GetProperty("content")[1]; Equal("image", image.GetProperty("type").GetString());
            Equal("image/png", image.GetProperty("mimeType").GetString()); Equal("AA==", image.GetProperty("data").GetString());
        }
        Equal(0, await host.Finish()); Equal("", host.Error.ToString());
    }

    private static async Task Cancellation()
    {
        using var fixture = new Fixture(); await fixture.Create();
        await File.WriteAllTextAsync(fixture.Template, "---\ndescription: Review\n---\nReview $1"); await fixture.Script("unused");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async ValueTask HeldRead(CancellationToken _) { entered.TrySetResult(); await release.Task; }
        await using var host = new Host(fixture, HeldRead, fixture.Template);
        try
        {
            await Task.WhenAny(entered.Task, host.Completion);
            Equal(true, entered.Task.IsCompletedSuccessfully);
            host.Cancel(); Equal(false, host.Completion.IsCompleted);
        }
        finally { release.TrySetResult(); }
        // Await the actual host task; never abandon its original read/lease cleanup through WaitAsync.
        Equal(1, await host.Completion);
        Equal(true, host.Error.ToString().Contains("Canceled", StringComparison.Ordinal));
    }

    private static async Task Collision()
    {
        using var fixture = new Fixture(); await fixture.Create(); await fixture.Script("Review change");
        await File.WriteAllTextAsync(fixture.Template, "---\ndescription: First\n---\nReview $1");
        var subdirectory = Path.Combine(fixture.Root, "second"); Directory.CreateDirectory(subdirectory);
        var second = Path.Combine(subdirectory, "review.md");
        await File.WriteAllTextAsync(second, "---\ndescription: Second\n---\nWrong $1");
        await using var host = new Host(fixture, fixture.Template, second);
        await host.Send(new { id = "commands", type = "get_commands" });
        var commands = (await host.Response("commands")).Value.GetProperty("data").GetProperty("commands");
        Equal(1, commands.GetArrayLength()); Equal("First", commands[0].GetProperty("description").GetString());
        await host.Send(new { id = "prompt", type = "prompt", message = "/review change" });
        await host.Response("prompt"); await host.Wait(record => Type(record) == "agent_settled");
        Equal(0, await host.Finish());
        var diagnostic = JsonData.Parse(host.Error.ToString()).Value;
        Equal("prompt_template_diagnostic", diagnostic.GetProperty("type").GetString());
        Equal(second, diagnostic.GetProperty("path").GetString());
        Equal("collision", diagnostic.GetProperty("severity").GetString());
    }

    private static async Task OneShot()
    {
        using var fixture = new Fixture(); await fixture.Create();
        await File.WriteAllTextAsync(fixture.Template, "---\ndescription: |\n  Review code\n---\nReview $1");
        await fixture.Script("Review the change"); using var output = new StringWriter(); using var error = new StringWriter();
        var result = await SessionCommands.RunAsync(["session", "prompt", "--session", fixture.Session, "--workspace", fixture.Root,
            "--offline-script", fixture.ScriptPath, "--message", "/review 'the change'", "--prompt-template", fixture.Template], output, error);
        Equal(0, result); Equal("", error.ToString()); var report = JsonData.Parse(output.ToString()).Value;
        Equal(1, report.GetProperty("usedScriptTurns").GetInt32());
        Equal(true, report.GetProperty("requests")[0].GetProperty("historyRequirementsSatisfied").GetBoolean());
        // The requirement is checked by the real authored HTTP handler against its projected input,
        // not by a standalone expansion helper or an assertion over the raw submitted slash command.
        using var inspect = new StringWriter(); using var inspectError = new StringWriter();
        Equal(0, await SessionCommands.RunAsync(["session", "inspect", "--session", fixture.Session], inspect, inspectError));
        Equal("", inspectError.ToString());
        Equal(true, inspect.ToString().Contains("Review the change", StringComparison.Ordinal));
    }

    private static async Task Missing()
    {
        using var fixture = new Fixture(); await fixture.Create(); await fixture.Script("unused");
        var missing = Path.Combine(fixture.Root, "missing.md"); await using var host = new Host(fixture, missing, missing);
        await host.Send(new { id = "commands", type = "get_commands" });
        Equal(0, (await host.Response("commands")).Value.GetProperty("data").GetProperty("commands").GetArrayLength());
        Equal(0, await host.Finish());
        var diagnostics = host.Error.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Equal(1, diagnostics.Length); var diagnostic = JsonData.Parse(diagnostics[0]).Value;
        Equal("prompt_template_diagnostic", diagnostic.GetProperty("type").GetString());
        Equal("error", diagnostic.GetProperty("severity").GetString()); Equal(missing, diagnostic.GetProperty("path").GetString());
    }

    private static string? Type(JsonData record) => record.Value.GetProperty("type").GetString();
    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "pisharp-prompt-workflow-" + Guid.NewGuid().ToString("N"));
        internal string Session => Path.Combine(Root, "session.jsonl");
        internal string Template => Path.Combine(Root, "review.md");
        internal string ScriptPath => Path.Combine(Root, "script.json");
        internal Fixture() => Directory.CreateDirectory(Root);
        internal async Task Create()
        {
            using var output = new StringWriter(); using var error = new StringWriter();
            Equal(0, await SessionCommands.RunAsync(["session", "create", "--session", Session, "--workspace", Root], output, error));
            Equal("", error.ToString());
        }
        internal Task Script(string required) => File.WriteAllTextAsync(ScriptPath, JsonSerializer.Serialize(new { schemaVersion = 1, turns = new[] { new
        {
            requiredInputTexts = new[] { required }, events = new object[] {
                new { type = "response.output_item.added", output_index = 0, item = new { type = "message", id = "answer", content = Array.Empty<object>() } },
                new { type = "response.output_text.delta", output_index = 0, item_id = "answer", delta = "done" },
                new { type = "response.output_item.done", output_index = 0, item = new { type = "message", id = "answer", content = new[] { new { type = "output_text", text = "done" } } } },
                new { type = "response.completed", response = new { status = "completed", output = Array.Empty<object>(), usage = new { input_tokens = 8, output_tokens = 4, total_tokens = 12 } } }
            }
        } } }), new UTF8Encoding(false));
        public void Dispose()
        {
            var resolved = Path.GetFullPath(Root); var tempRoot = Path.GetFullPath(Path.GetTempPath());
            if (!resolved.StartsWith(tempRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
                !Path.GetFileName(resolved).StartsWith("pisharp-prompt-workflow-", StringComparison.Ordinal))
                throw new InvalidOperationException("Fixture cleanup target is outside its owned temporary root.");
            Directory.Delete(resolved, recursive: true);
        }
    }

    private sealed class Host : IAsyncDisposable
    {
        private readonly Channel<JsonData> records = Channel.CreateUnbounded<JsonData>(new UnboundedChannelOptions { SingleReader = true });
        private readonly CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15));
        private readonly BoundedRpcConnection connection;
        private readonly Task<int> run;
        internal StringWriter Error { get; } = new();
        internal Task<int> Completion => run;
        internal void Cancel() => deadline.Cancel();
        internal Host(Fixture fixture, params string[] paths) : this(fixture, null, paths) { }
        internal Host(Fixture fixture, Func<CancellationToken, ValueTask>? afterReadStarted, params string[] paths)
        {
            connection = new((record, _) => { records.Writer.TryWrite(record); return ValueTask.CompletedTask; }, afterReadStarted: afterReadStarted);
            string[] args = ["session", "rpc", "--session", fixture.Session, "--workspace", fixture.Root, "--offline-script", fixture.ScriptPath,
                .. paths.SelectMany(path => new[] { "--prompt-template", path })];
            run = RpcSessionCommand.RunAsync(args, connection.Input, connection.Output, Error, deadline.Token);
        }
        internal Task Send(object command) => connection.SendAsync(JsonData.Parse(JsonSerializer.Serialize(command)), deadline.Token);
        internal async Task<JsonData> Wait(Func<JsonData, bool> predicate)
        { while (true) { var record = await records.Reader.ReadAsync(deadline.Token); if (predicate(record)) return record; } }
        internal async Task<JsonData> Response(string id)
        {
            var record = await Wait(value => Type(value) == "response" && value.Value.GetProperty("id").GetString() == id);
            Equal(true, record.Value.GetProperty("success").GetBoolean()); return record;
        }
        internal async Task<int> Finish() { connection.CompleteInput(); return await run; }
        public async ValueTask DisposeAsync()
        {
            connection.CompleteInput(); if (!run.IsCompleted) deadline.Cancel();
            try { await run; }
            finally { await connection.DisposeAsync(); deadline.Dispose(); Error.Dispose(); }
        }
    }
}
