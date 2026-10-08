using System.Text;
using System.Text.Json;
using PiSharp.Cli.Interactive;

namespace PiSharp.Cli.Commands;

/// <summary>Native cooked-input client over the unchanged actual offline durable RPC host.</summary>
public static class InteractiveSessionCommand
{
    public const string Usage = "session chat --session <existing absolute JSONL> --workspace <existing absolute directory> --offline-script <absolute JSON> [existing session rpc options]; cooked commands /edit /save /cancel /compact [focus] /compact-all /branch-summary <entryId|root> /auto-compact on|off /abort /state /steer <text> /follow-up <text> /clear-queue /quit";
    public static Task<int> RunAsync(string[] args, TextReader input, TextWriter output, TextWriter error, CancellationToken token = default) =>
        RunHostedAsync(args, input, output, error, null, token);

    /// <summary>The production entry: <paramref name="mcpHost"/> supplies the session's MCP servers (the global mcp.json).</summary>
    internal static async Task<int> RunHostedAsync(string[] args, TextReader input, TextWriter output, TextWriter error,
        PiSharp.Cli.Mcp.McpSessionHost? mcpHost, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(input); ArgumentNullException.ThrowIfNull(output); ArgumentNullException.ThrowIfNull(error);
        if (args is not ["session", "chat", ..])
        { await ErrorAsync("InvalidArguments", "Interactive command requires session chat.", error).ConfigureAwait(false); return 2; }
        using var hostCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var inputCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var frontend = new InteractiveSessionFrontend(output, nativePresentation: true);
        frontend.BindLogin(PiSharp.Cli.Authentication.ProviderLoginHost.CreateDefault());
        await using var connection = new BoundedRpcConnection(frontend.ObserveAsync); frontend.Bind(connection.SendAsync);
        var rpcArgs = args.ToArray(); rpcArgs[1] = "rpc";
        var host = RpcSessionCommand.RunWithPresentationAsync(rpcArgs, connection.Input, connection.Output, error, frontend, hostCancellation.Token, mcpHost: mcpHost);
        Task? reading = null; Exception? failure = null; var result = 1;
        try
        {
            await frontend.StartAsync(inputCancellation.Token).ConfigureAwait(false);
            reading = ReadAsync(input, frontend, connection, inputCancellation.Token);
            var startup = await Task.WhenAny(host, frontend.Ready, reading).ConfigureAwait(false);
            if (startup == host) return await host.ConfigureAwait(false);
            if (startup == reading) { await reading.ConfigureAwait(false); connection.CompleteInput(); return await host.ConfigureAwait(false); }
            await frontend.Ready.ConfigureAwait(false);
            await frontend.HistoryAsync(inputCancellation.Token).ConfigureAwait(false);
            if (await Task.WhenAny(host, reading).ConfigureAwait(false) == reading)
            { await reading.ConfigureAwait(false); connection.CompleteInput(); }
            else inputCancellation.Cancel();
            result = await host.ConfigureAwait(false);
        }
        catch (Exception errorValue)
        {
            failure = errorValue;
            try { hostCancellation.Cancel(); } catch (Exception) { }
            connection.CompleteInput();
            try { result = await host.ConfigureAwait(false); } catch (Exception) { }
        }
        finally
        {
            try { inputCancellation.Cancel(); } catch (Exception errorValue) { failure ??= errorValue; }
            connection.CompleteInput();
            if (reading is not null)
                try { await reading.ConfigureAwait(false); } catch (OperationCanceledException) when (inputCancellation.IsCancellationRequested) { } catch (Exception errorValue) { failure ??= errorValue; }
            if (!host.IsCompleted)
            { try { hostCancellation.Cancel(); } catch (Exception errorValue) { failure ??= errorValue; } try { await host.ConfigureAwait(false); } catch (Exception errorValue) { failure ??= errorValue; } }
        }
        if (failure is not null)
        {
            // The actual host already emits one sanitized failure receipt when our shutdown cancels it.
            if (host.IsCompletedSuccessfully && result != 0) return result;
            await ErrorAsync(token.IsCancellationRequested ? "Canceled" : "InteractiveFailed", "Interactive frontend failed after owned RPC cleanup; inspect durable state before retrying.", error).ConfigureAwait(false); return 1;
        }
        return result;
    }
    private static async Task ReadAsync(TextReader input, InteractiveSessionFrontend frontend, BoundedRpcConnection connection, CancellationToken token)
    {
        var buffer = new char[1024]; var line = new StringBuilder();
        while (true)
        {
            var count = await input.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (count == 0)
            {
                if (line.Length != 0) await frontend.LineAsync(Line(line), token).ConfigureAwait(false);
                connection.CompleteInput(); return;
            }
            for (var index = 0; index < count; index++)
            {
                var value = buffer[index];
                if (value == '\n')
                { if (!await frontend.LineAsync(Line(line), token).ConfigureAwait(false)) { connection.CompleteInput(); return; } }
                else
                { if (line.Length == ChatEditor.MaximumCharacters) throw new InvalidOperationException("Interactive input line exceeds its bounded profile."); line.Append(value); }
            }
        }
        static string Line(StringBuilder buffer)
        { var text = buffer.ToString(); buffer.Clear(); return text.EndsWith('\r') ? text[..^1] : text; }
    }
    private static async Task ErrorAsync(string code, string message, TextWriter error)
    {
        await error.WriteAsync(JsonSerializer.Serialize(new { schemaVersion = 1, status = "failed", code, message, effectsMayHaveCompleted = true }) + "\n").ConfigureAwait(false);
        await error.FlushAsync().ConfigureAwait(false);
    }
}
