using System.Runtime.CompilerServices;
using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Events;

namespace PublishedCliFixture;

/// <summary>Authored published consumer. Marker/gate environment variables exist only in this test package.</summary>
public sealed class Entry : IPiSharpExtension
{
    public Entry() => Signals.Mark("constructor");
    public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); Signals.Mark("initialize");
        registry.RegisterTool(new("echo", "fixture.cli.echo", "Explicit published CLI echo",
            JsonData.Parse("{\"type\":\"object\",\"properties\":{\"text\":{\"type\":\"string\"}},\"required\":[\"text\"],\"additionalProperties\":false}"),
            async (arguments, _, token) =>
            {
                var text = arguments.Value.GetProperty("text").GetString()!;
                Signals.Mark("tool:" + (text is "hold" or "fault" ? text : "called"));
                try
                {
                    if (text == "hold") await Signals.HoldAsync("tool", token).ConfigureAwait(false);
                    if (text == "fault") throw new InvalidOperationException("private published callback payload");
                    token.ThrowIfCancellationRequested();
                    return JsonData.Parse(JsonSerializer.Serialize(new
                    {
                        content = new[] { new { type = "text", text = "plugin:" + text } },
                        details = new { argument = text }, structuredContent = new { original = text },
                        future = (object?)null, opaque = new { ordered = new[] { 2, 1 } }
                    }));
                }
                finally { Signals.Mark("tool-closed"); }
            }));
        registry.RegisterBeforeAgentStartHandler(new("before-start", (input, _, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (!input.Prompt.StartsWith("before-start:", StringComparison.Ordinal))
                return ValueTask.FromResult<ExtensionBeforeAgentStartPatch?>(null);
            Signals.Mark("before-agent-start");
            return ValueTask.FromResult<ExtensionBeforeAgentStartPatch?>(new(
                new("fixture-before-start", false, JsonData.Parse("\"before-start-custom\""), JsonData.Parse("{\"retained\":true}")),
                "before-start-forced"));
        }));
        registry.RegisterContextHandler(new("request-context", (input, _, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (!input.Messages.Any(message => message.WireBody.ToString().Contains("context-request:", StringComparison.Ordinal)))
                return ValueTask.FromResult<ExtensionContextMessagesPatch?>(null);
            Signals.Mark("context");
            return ValueTask.FromResult<ExtensionContextMessagesPatch?>(new(input.Messages.Select(message =>
                message.Role == "user" && message.WireBody.ToString().Contains("context-request:", StringComparison.Ordinal)
                    ? new TranscriptEntry("user", JsonData.Parse("{\"role\":\"user\",\"content\":\"request-context\",\"timestamp\":123}")) : message).ToImmutableArray()));
        }));
        registry.RegisterContextWithSystemHandler(new("request-full-context", (input, _, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (!input.Messages.Any(message => message.WireBody.ToString().Contains("request-context", StringComparison.Ordinal)))
                return ValueTask.FromResult<ExtensionContextMessagesPatch?>(null);
            Signals.Mark("context-with-system");
            return ValueTask.FromResult<ExtensionContextMessagesPatch?>(new(input.Messages.Select(message =>
                message.Role == "user" && message.WireBody.ToString().Contains("request-context", StringComparison.Ordinal)
                    ? new TranscriptEntry("user", JsonData.Parse("{\"role\":\"user\",\"content\":\"request-context|with-system\",\"timestamp\":123}")) : message).ToImmutableArray()));
        }));
        registry.RegisterInputHandler(new("input", async (input, _, token) =>
        {
            Signals.Mark("input");
            try
            {
                if (input.Text == "hold-input") await Signals.HoldAsync("input", token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (input.Text == "handled") return new(ExtensionInputAction.Handled);
                if (input.Text.StartsWith("transform:", StringComparison.Ordinal))
                    return new(ExtensionInputAction.Transform, input.Text[10..] + "|cli", input.Images);
                return null;
            }
            finally { Signals.Mark("input-closed"); }
        }));
        registry.RegisterToolCallHandler(new("call", (call, _, token) =>
        {
            token.ThrowIfCancellationRequested(); Signals.Mark("call");
            if (call.ToolName == "write" && call.Arguments.Value.GetProperty("content").GetString() == "extension-block")
                return ValueTask.FromResult<ExtensionToolCallPatch?>(new(Decision: JsonData.Parse("{\"block\":true}")));
            if (call.ToolName == "fixture.cli.echo")
            {
                var text = call.Arguments.Value.GetProperty("text").GetString();
                if (text == "replace") return ValueTask.FromResult<ExtensionToolCallPatch?>(new(JsonData.Parse("{\"text\":\"transformed\"}")));
                if (text == "invalid-replacement") return ValueTask.FromResult<ExtensionToolCallPatch?>(new(JsonData.Parse("{\"text\":1}")));
                if (text == "fault-call") throw new InvalidOperationException("private published call failure");
            }
            return ValueTask.FromResult<ExtensionToolCallPatch?>(null);
        }));
        registry.RegisterToolResultHandler(new("result", (result, _, token) =>
        {
            token.ThrowIfCancellationRequested(); Signals.Mark("result");
            return ValueTask.FromResult<ExtensionToolResultPatch?>(result.ToolName == "fixture.cli.echo" &&
                result.Arguments.Value.GetProperty("text").GetString() == "redact"
                ? new(JsonData.Parse("{\"content\":[{\"type\":\"text\",\"text\":\"redacted\"}],\"details\":null}")) : null);
        }));
        return ValueTask.CompletedTask;
    }
    public ValueTask DisposeAsync() { Signals.Mark("dispose"); return ValueTask.CompletedTask; }
}

internal static class Signals
{
    [ModuleInitializer] internal static void Module() => Mark("module");
    internal static void Mark(string stage)
    {
        var root = Environment.GetEnvironmentVariable("PISHARP_NATIVE_FIXTURE_MARKERS");
        if (root is not null) File.AppendAllText(Path.Combine(OwnedRoot(root), "PublishedFixture.Cli.markers"), stage + "\n");
    }
    internal static async Task HoldAsync(string stage, CancellationToken token)
    {
        var root = OwnedRoot(Environment.GetEnvironmentVariable("PISHARP_NATIVE_CLI_FIXTURE_GATE_ROOT") ??
            throw new InvalidOperationException("Authored fixture gate root is absent."));
        var release = Path.Combine(root, stage + ".release");
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcher(root) { Filter = stage + ".release", NotifyFilter = NotifyFilters.FileName };
        watcher.Created += (_, _) => settled.TrySetResult();
        watcher.Renamed += (_, _) => settled.TrySetResult();
        watcher.Error += (_, _) => settled.TrySetException(new IOException("Authored fixture watcher failed."));
        watcher.EnableRaisingEvents = true;
        try
        {
            var temporary = Path.Combine(root, stage + ".entered.part");
            await File.WriteAllTextAsync(temporary, "entered\n", token).ConfigureAwait(false);
            File.Move(temporary, Path.Combine(root, stage + ".entered"));
            if (File.Exists(release)) settled.TrySetResult();
            await settled.Task.WaitAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
        }
        finally
        {
            watcher.EnableRaisingEvents = false;
            await File.WriteAllTextAsync(Path.Combine(root, stage + ".closed"), "closed\n", CancellationToken.None).ConfigureAwait(false);
        }
    }
    private static string OwnedRoot(string path)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
        if (!Path.IsPathFullyQualified(path) || root.Length > 4096 || !root.StartsWith(temporary, StringComparison.Ordinal) ||
            !Directory.Exists(root) || path.Any(char.IsControl)) throw new InvalidOperationException("Authored fixture root is invalid.");
        return root;
    }
}
