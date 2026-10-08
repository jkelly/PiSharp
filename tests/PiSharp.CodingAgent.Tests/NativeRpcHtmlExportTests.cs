using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Interactive;
using PiSharp.Contracts;

internal static class NativeRpcHtmlExportTests
{
    internal const string Prefix = "native RPC HTML export ";
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
        [(Prefix + "actual CLI uses existing explicit write grant after materialization and denies ungranted target", ActualHost)];
    private static async Task ActualHost()
    {
        using var fixture = new StartupSettingsTests.Fixture(); using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var records = Channel.CreateUnbounded<JsonData>(new() { SingleReader = true, SingleWriter = false });
        await using var connection = new BoundedRpcConnection((record, _) => { records.Writer.TryWrite(record); return ValueTask.CompletedTask; });
        using var error = new StringWriter();
        var source = Path.Combine(fixture.Root, "session.jsonl"); var output = Path.Combine(fixture.Root, "export.html");
        var denied = Path.Combine(fixture.Root, "denied.html");
        var run = RpcSessionCommand.RunAsync(["session", "rpc", "--session", source, "--workspace", fixture.Root,
            "--offline-script", fixture.Script, "--session-mode", "new-lazy", "--allow-write", output, "--tools", ""],
            connection.Input, connection.Output, error, stop.Token);
        try
        {
            Check((await Response(new { id = "ready", type = "get_state" })).GetProperty("success").GetBoolean(), "Actual CLI startup failed.");
            Check(!(await Response(new { id = "lazy", type = "export_html", outputPath = output })).GetProperty("success").GetBoolean() && !File.Exists(output),
                "Lazy export acquired a write before conversation materialization.");
            await connection.SendAsync(JsonData.Parse(JsonSerializer.Serialize(new { id = "materialize", type = "prompt", message = "HTML export fixture" })), stop.Token);
            var accepted = false; var ended = false;
            while (!accepted || !ended)
            {
                var record = (await records.Reader.ReadAsync(stop.Token)).Value; var type = record.GetProperty("type").GetString();
                if (type == "response" && record.GetProperty("id").GetString() == "materialize")
                { Check(record.GetProperty("success").GetBoolean(), "Prompt admission failed."); accepted = true; }
                if (type == "agent_end") ended = true;
            }
            var original = await SourceBytes(source, stop.Token);
            var reply = await Response(new { id = "export", type = "export_html", outputPath = output });
            Check(reply.GetProperty("success").GetBoolean() && reply.GetProperty("data").GetProperty("path").GetString() == Path.GetFullPath(output),
                "Actual host did not install its explicit existing write invoker.");
            var html = await File.ReadAllBytesAsync(output, stop.Token);
            Check(html.Length > 0 && !(html.Length >= 3 && html[0] == 0xef && html[1] == 0xbb && html[2] == 0xbf) &&
                System.Text.Encoding.UTF8.GetString(html).Contains("HTML export fixture", StringComparison.Ordinal), "Host output bytes/content changed.");
            Check(!(await Response(new { id = "denied", type = "export_html", outputPath = denied })).GetProperty("success").GetBoolean() && !File.Exists(denied),
                "Host exporter created an implicit destination grant.");
            var after = await SourceBytes(source, stop.Token);
            Check(SHA256.HashData(original).SequenceEqual(SHA256.HashData(after)), "Export appended or changed source session state.");
            connection.CompleteInput(); Check(await run == 0, "Actual CLI failed to settle: " + error);
        }
        finally
        {
            connection.CompleteInput(); if (!run.IsCompleted) stop.Cancel();
            await run;
        }
        async Task<JsonElement> Response(object command)
        {
            var data = JsonData.Parse(JsonSerializer.Serialize(command)); var id = data.Value.GetProperty("id").GetString();
            await connection.SendAsync(data, stop.Token);
            while (true)
            {
                var record = (await records.Reader.ReadAsync(stop.Token)).Value;
                if (record.GetProperty("type").GetString() == "response" && record.GetProperty("id").GetString() == id) return record;
            }
        }
    }
    private static async Task<byte[]> SourceBytes(string path, CancellationToken token)
    {
        // The actual CLI retains its read/write session handle through both snapshots.
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
        Check(input.Length is > 0 and <= 1_048_576, "Fixture source exceeds the bounded snapshot size.");
        var bytes = new byte[checked((int)input.Length)];
        await input.ReadExactlyAsync(bytes, token);
        Check(input.Length == bytes.Length && input.Position == bytes.Length, "Fixture source changed during snapshot read.");
        return bytes;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
