// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/modes/print-mode.ts (runPrintMode),
// packages/coding-agent/src/modes/json-event.ts and packages/coding-agent/src/modes/rpc/rpc-client.ts (waiting for agent_settled).
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Interactive;
using PiSharp.Contracts;

namespace PiSharp.Cli.Pi;

/// <summary>Print mode over the session host: the prompts go in as RPC commands and wait for <c>agent_settled</c>, as
/// <c>session.prompt</c> resolves upstream. Text mode prints the final assistant text; JSON mode prints the session header and then
/// every session event line exactly as the host serialized it (command responses are not events and are left out).</summary>
internal static class PiPrintMode
{
    internal static async Task<int> RunAsync(string[] rpcArgs, bool json, PiSessionPlan plan, PiEntryOptions options, PiHost host,
        PiSharp.Cli.Mcp.McpSessionHost? mcpHost, CancellationToken token, Func<bool>? userShutdown = null)
    {
        var stdout = host.Stdout; var stderr = host.Stderr;
        if (json)
        {
            // Source: JSON.stringify(sessionManager.getHeader()) before any event.
            var header = plan.Mode == "open" ? PiSessions.ReadHeader(plan.SessionPath) : new JsonObject
            {
                ["type"] = "session", ["version"] = PiSessions.CurrentSessionVersion, ["id"] = plan.HeaderId, ["timestamp"] = plan.HeaderTimestamp, ["cwd"] = plan.Cwd
            };
            if (header is not null) await PiCommand.Line(stdout, PiJson.Stringify(header)).ConfigureAwait(false);
        }
        var records = Channel.CreateUnbounded<JsonData>(new() { SingleReader = true });
        var outputGate = new SemaphoreSlim(1, 1);
        var connection = new BoundedRpcConnection(async (record, recordToken) =>
        {
            var type = record.Value.TryGetProperty("type", out var kind) ? kind.GetString() : null;
            if (type == "extension_error")
            {
                // print-mode.ts bindExtensions onError: `Extension error (<path>): <error>` on stderr; RPC mode alone emits the record.
                var path = record.Value.TryGetProperty("extensionPath", out var extensionPath) ? extensionPath.GetString() : null;
                var message = record.Value.TryGetProperty("error", out var error) ? error.GetString() : null;
                await PiCommand.Line(stderr, $"Extension error ({path}): {message}").ConfigureAwait(false);
                return;
            }
            // Session events only: command responses and PiSharp's own RPC records (pisharp_*) are not part of Pi's JSON stream.
            if (json && type != "response" && type?.StartsWith("pisharp_", StringComparison.Ordinal) != true)
            {
                await outputGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                // print-mode.ts writes JSON.stringify(event): non-ASCII and ' raw, JavaScript number text.
                var line = PiSharp.AI.StreamingJson.JsonReformat(record.ToString()) + "\n";
                try { await stdout.WriteAsync(line.AsMemory(), CancellationToken.None).ConfigureAwait(false); await stdout.FlushAsync(CancellationToken.None).ConfigureAwait(false); }
                finally { outputGate.Release(); }
            }
            records.Writer.TryWrite(record);
        });
        using var hostStop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var completion = Run();
        async Task<int> Run()
        {
            try
            {
                return await RpcSessionCommand.RunWithPresentationAsync(rpcArgs, connection.Input, connection.Output, stderr, null!, hostStop.Token,
                    userShutdown: userShutdown, mcpHost: mcpHost).ConfigureAwait(false);
            }
            finally { records.Writer.TryComplete(); }
        }
        var exitCode = 0; var sequence = 0;
        try
        {
            if (options.InitialMessage is { } initial)
                exitCode = await PromptAsync(initial, options.InitialImages.IsDefaultOrEmpty ? null : options.InitialImages).ConfigureAwait(false);
            foreach (var message in options.InitialMessages)
                if (exitCode == 0) exitCode = await PromptAsync(message, null).ConfigureAwait(false);
            if (exitCode == 0 && !json)
            {
                // print-mode.ts reads session.state.messages directly; a host that cannot answer is a failure, never a silent success.
                var (messages, readError) = await CommandAsync(new JsonObject { ["type"] = "get_messages" }).ConfigureAwait(false);
                if (messages is null) { await PiCommand.Line(stderr, readError).ConfigureAwait(false); exitCode = 1; }
                else if (messages is { } data && data.TryGetProperty("messages", out var list) && list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0)
                {
                    var last = list[list.GetArrayLength() - 1];
                    if (last.TryGetProperty("role", out var role) && role.GetString() == "assistant")
                    {
                        var stop = last.TryGetProperty("stopReason", out var reason) ? reason.GetString() : null;
                        if (stop is "error" or "aborted")
                        {
                            var error = last.TryGetProperty("errorMessage", out var text) && text.ValueKind == JsonValueKind.String && text.GetString() is { Length: > 0 } value ? value : $"Request {stop}";
                            await PiCommand.Line(stderr, error).ConfigureAwait(false);
                            exitCode = 1;
                        }
                        else if (last.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var block in content.EnumerateArray())
                                if (block.TryGetProperty("type", out var blockType) && blockType.GetString() == "text" && block.TryGetProperty("text", out var blockText))
                                    await stdout.WriteAsync((blockText.GetString() + "\n").AsMemory(), token).ConfigureAwait(false);
                            await stdout.FlushAsync(token).ConfigureAwait(false);
                        }
                    }
                }
            }
        }
        catch (PromptFailure failure) { await PiCommand.Line(stderr, failure.Message).ConfigureAwait(false); exitCode = 1; }
        catch (HostEnded) { exitCode = 1; }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { exitCode = 1; }
        finally
        {
            connection.CompleteInput();
            while (await records.Reader.WaitToReadAsync(CancellationToken.None).ConfigureAwait(false)) while (records.Reader.TryRead(out _)) { }
        }
        var hostExit = await completion.ConfigureAwait(false);
        await connection.DisposeAsync().ConfigureAwait(false);
        await stdout.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        return exitCode != 0 ? exitCode : hostExit;

        async Task<int> PromptAsync(string message, System.Collections.Immutable.ImmutableArray<JsonData>? images)
        {
            var command = new JsonObject { ["type"] = "prompt", ["message"] = message };
            if (images is { } attached) command["images"] = new JsonArray([.. attached.Select(image => JsonNode.Parse(image.ToString()))]);
            var id = "pi-print-" + ++sequence; command["id"] = id;
            await connection.SendAsync(JsonData.Parse(command.ToJsonString()), token).ConfigureAwait(false);
            var responded = false; var waitSettled = true;
            while (true)
            {
                var record = await NextAsync().ConfigureAwait(false);
                var type = record.Value.GetProperty("type").GetString();
                if (type == "response" && record.Value.TryGetProperty("id", out var responseId) && responseId.GetString() == id)
                {
                    if (!record.Value.GetProperty("success").GetBoolean())
                        throw new PromptFailure(record.Value.TryGetProperty("error", out var error) ? error.GetString() ?? "Prompt failed" : "Prompt failed");
                    responded = true;
                    waitSettled = !(record.Value.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object &&
                        data.TryGetProperty("disposition", out var disposition) && disposition.GetString() == "handled");
                    if (!waitSettled) return 0;
                }
                else if (type == "agent_settled" && responded && waitSettled) return 0;
            }
        }
        async Task<(JsonElement? Data, string Error)> CommandAsync(JsonObject command)
        {
            var id = "pi-print-" + ++sequence; command["id"] = id;
            await connection.SendAsync(JsonData.Parse(command.ToJsonString()), token).ConfigureAwait(false);
            while (true)
            {
                var record = await NextAsync().ConfigureAwait(false);
                if (record.Value.GetProperty("type").GetString() != "response" || !record.Value.TryGetProperty("id", out var responseId) || responseId.GetString() != id) continue;
                if (!record.Value.GetProperty("success").GetBoolean())
                    return (null, record.Value.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String ? error.GetString()! : "Request failed");
                return (record.Value.TryGetProperty("data", out var data) ? data.Clone() : JsonDocument.Parse("{}").RootElement.Clone(), "");
            }
        }
        async Task<JsonData> NextAsync()
        {
            try { return await records.Reader.ReadAsync(token).ConfigureAwait(false); }
            catch (ChannelClosedException) { throw new HostEnded(); }
        }
    }

    private sealed class PromptFailure(string message) : Exception(message);
    private sealed class HostEnded : Exception;
}
