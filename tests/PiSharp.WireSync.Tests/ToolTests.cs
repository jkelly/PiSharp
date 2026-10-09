using System.Collections.Immutable;
using PiSharp.Agent;
using PiSharp.Contracts;
using PiSharp.Tools.Files;
using PiSharp.Tools.Processes;
using static Assert;

// tools.read-structured (1.0.4) and tools.bash-schema-and-spill (1.0.3/1.0.4, utils/output-files.ts).
internal static class ToolTests
{
    public static IEnumerable<(string, Func<Task>)> Cases()
    {
        yield return ("tools.read.output-schema-structured-text-and-image-block", Read);
        yield return ("tools.bash.output-schema-descriptions", () => { BashSchema(); return Task.CompletedTask; });
        yield return ("tools.bash.spill-files-exclusive-and-owner-only", Spill);
    }

    private sealed class Allow : IToolActionPolicy
    {
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction finalAction, CancellationToken token) =>
            ValueTask.FromResult(new ToolActionAuthorization(true));
    }

    private static string Temp(string prefix)
    {
        var directory = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); return directory;
    }

    private static async Task Read()
    {
        Equal("""{"anyOf":[{"type":"string"},{"type":"object","properties":{"type":{"const":"image","type":"string"},"data":{"type":"string"},"mimeType":{"type":"string"},"note":{"type":"string"}},"required":["type","data","mimeType","note"]}]}""",
            ReadWriteTools.ReadOutputSchema.ToString());
        // toReadOutput: the first text for text content; an image block with the first text as its note.
        Equal("\"line <1> & é\"", ReadWriteTools.ToReadOutput(JsonData.Parse("""[{"type":"text","text":"line <1> & é"}]""")).ToString());
        Equal("""{"type":"image","data":"iVBORw0=","mimeType":"image/png","note":"Read image file [image/png]"}""",
            ReadWriteTools.ToReadOutput(JsonData.Parse("""[{"type":"text","text":"Read image file [image/png]"},{"type":"image","data":"iVBORw0=","mimeType":"image/png"}]""")).ToString());
        Equal("""{"type":"image","data":"AA==","mimeType":"image/jpeg","note":""}""",
            ReadWriteTools.ToReadOutput(JsonData.Parse("""[{"type":"image","data":"AA==","mimeType":"image/jpeg"}]""")).ToString());
        Equal("\"\"", ReadWriteTools.ToReadOutput(JsonData.Parse("[]")).ToString());

        var directory = Temp("PiSharp-wire-read-");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "notes.txt"), "alpha\nbeta");
            var tools = new ReadWriteTools(directory, directory);
            var scheduler = new ToolBatchScheduler(tools.CreateDefinitions(tools.CreateInvoker(new Allow())));
            var message = new AssistantMessage("api", "provider", "model", 1, [
                new ToolCallContent("read-ok", "read", JsonData.Parse("""{"path":"notes.txt"}""")),
                new ToolCallContent("read-past", "read", JsonData.Parse("""{"path":"notes.txt","offset":9}"""))], TokenUsage.Zero, StopReason.ToolUse);
            var batch = await scheduler.RunAsync(message, new DurationTests.Collect());
            var ok = batch.Outcomes.Single(outcome => outcome.Invocation.Call.Id == "read-ok");
            Check(!ok.IsError, "read failed");
            Equal("\"alpha\\nbeta\"", ok.Result.StructuredContent?.ToString());
            // Failed reads carry no structured result, and structured content never enters the model transcript.
            Check(batch.Outcomes.Single(outcome => outcome.Invocation.Call.Id == "read-past").Result.StructuredContent is null, "failed read had output");
            var transcript = ToolResultMessageMaterializer.ToTranscript(batch.Messages.Single(row => row.ToolCallId == "read-ok"), 5).WireBody.ToString();
            // read.ts resolves details: undefined for an untruncated read, which JSON.stringify leaves out.
            Equal("""{"role":"toolResult","toolCallId":"read-ok","toolName":"read","content":[{"type":"text","text":"alpha\nbeta"}],"isError":false,"timestamp":5}""", transcript);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void BashSchema() => Equal(
        """{"type":"object","properties":{"output":{"type":"string","description":"Combined stdout and stderr, possibly truncated"},"truncated":{"type":"boolean"},"full_output_path":{"type":"string","description":"Full output, when truncated"},"exit_code":{"type":"number"},"wall_time_seconds":{"type":"number"}},"required":["output","truncated","exit_code","wall_time_seconds"]}""",
        BashTool.OutputSchema.ToString());

    private static async Task Spill()
    {
        var directory = Temp("PiSharp-wire-spill-");
        try
        {
            var storage = new LocalProcessOutputStorage();
            var path = Path.Combine(directory, "pi-bash-0123456789abcdef.log");
            await using (var stream = await storage.CreateNewAsync(path)) await stream.WriteAsync("spilled"u8.ToArray());
            Equal("spilled", await File.ReadAllTextAsync(path));
            if (!OperatingSystem.IsWindows())
                Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path), "spill mode");
            // Exclusive creation: an existing file is never reused or truncated.
            await Throws<IOException>(async () => { await using var _ = await storage.CreateNewAsync(path); });
            Equal("spilled", await File.ReadAllTextAsync(path));
            // Exclusive creation never follows a link someone placed at the path (when this host may create links).
            var target = Path.Combine(directory, "victim.txt"); await File.WriteAllTextAsync(target, "victim");
            var link = Path.Combine(directory, "pi-bash-fedcba9876543210.log");
            var linked = false;
            try { File.CreateSymbolicLink(link, target); linked = true; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            if (linked)
            {
                await Throws<IOException>(async () => { await using var _ = await storage.CreateNewAsync(link); });
                Equal("victim", await File.ReadAllTextAsync(target));
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
