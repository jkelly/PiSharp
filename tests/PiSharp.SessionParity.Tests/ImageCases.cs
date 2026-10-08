using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Agent;
using PiSharp.Cli.Extensions;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Tools.Images;
using static EventFixture;

// Pi v1.1.0 packages/coding-agent/src/core/sdk.ts (convertToLlmWithBlockImages) and core/agent-session.ts (_afterToolCall:
// normalizeToolResultImages after the tool_result hook), by reading.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> ImageCases() =>
    [
        Case("images.block-images-projection-placeholders-and-dedupe", BlockImagesProjection),
        Case("images.block-images-request-only-through-session", BlockImagesThroughSession),
        Case("images.tool-result-normalization-after-hook-and-structured-content", ToolResultNormalization),
    ];

    private static TranscriptEntry Wire(string role, string json) => new(role, JsonData.Parse(json));

    private static Task BlockImagesProjection()
    {
        var image = """{"type":"image","data":"AAAA","mimeType":"image/png"}""";
        ImmutableArray<TranscriptEntry> input =
        [
            Wire("system", """{"role":"system","content":"s"}"""),
            Wire("user", "{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"look\"}," + image + "," + image + ",{\"type\":\"text\",\"text\":\"end\"}],\"timestamp\":5}"),
            Wire("toolResult", "{\"role\":\"toolResult\",\"toolCallId\":\"c\",\"toolName\":\"read\",\"content\":[{\"type\":\"text\",\"text\":\"Image reading is disabled.\"}," + image + "],\"isError\":false,\"timestamp\":6}"),
            Wire("assistant", """{"role":"assistant","content":[{"type":"text","text":"keep"}],"stopReason":"stop"}"""),
            Wire("user", """{"role":"user","content":"plain","timestamp":7}""")
        ];
        var output = BlockedImages.Filter(input);
        Check(ReferenceEquals(input[0], output[0]) && ReferenceEquals(input[3], output[3]) && ReferenceEquals(input[4], output[4]), "Untouched messages were rebuilt.");
        Equal("""[{"type":"text","text":"look"},{"type":"text","text":"Image reading is disabled."},{"type":"text","text":"end"}]""",
            output[1].WireBody.Value.GetProperty("content").GetRawText(), "user content");
        Equal(5, output[1].WireBody.Value.GetProperty("timestamp").GetInt32(), "other fields retained");
        Equal("""[{"type":"text","text":"Image reading is disabled."}]""", output[2].WireBody.Value.GetProperty("content").GetRawText(), "tool result content");
        var untouched = ImmutableArray.Create(input[0], input[3]);
        Check(BlockedImages.Filter(untouched) == untouched, "A message list without images was copied.");
        return Task.CompletedTask;
    }

    private static async Task BlockImagesThroughSession()
    {
        var blocked = true;
        await using var f = await CreateWithOptionsAsync(false, new() { BlockImages = () => blocked }, Response(), Response());
        var prompt = Wire("user", """{"role":"user","content":[{"type":"text","text":"see"},{"type":"image","data":"AAAA","mimeType":"image/png"}],"timestamp":1}""");
        await f.Session.PromptAsync(prompt);
        var sent = f.Transport.Requests[^1].Messages.Last(message => message.Role == "user").WireBody.Value.GetProperty("content").GetRawText();
        Equal("""[{"type":"text","text":"see"},{"type":"text","text":"Image reading is disabled."}]""", sent, "blocked request content");
        var stored = f.Session.Snapshot.Log.Entries.Last(entry => entry.Type == "message" && entry.WireBody.Value.GetProperty("message").GetProperty("role").GetString() == "user");
        Check(stored.WireBody.Value.GetProperty("message").GetProperty("content")[1].GetProperty("type").GetString() == "image", "History lost the image.");
        blocked = false; // The setting is read per request.
        await f.Session.PromptAsync(Wire("user", """{"role":"user","content":"again","timestamp":2}"""));
        Check(f.Transport.Requests[^1].Messages.Any(message => message.Role == "user" && message.WireBody.Value.GetProperty("content").ValueKind == JsonValueKind.Array &&
            message.WireBody.Value.GetProperty("content").EnumerateArray().Any(block => block.GetProperty("type").GetString() == "image")), "Unblocked request still filtered images.");
    }

    /// <summary>Probes every image as 4000x100 and encodes a fixed small PNG, standing in for the Photon backend.</summary>
    private sealed class WideCodec : IImageCodec
    {
        public (int Width, int Height)? ProbeDimensions(ReadOnlySpan<byte> bytes, string mimeType) => (4000, 100);
        public byte[]? ConvertToPng(ReadOnlySpan<byte> bytes, string mimeType) => [9, 9];
        public ImmutableArray<(byte[] Bytes, string MimeType)>? Encode(ReadOnlySpan<byte> bytes, string mimeType, int width, int height, IReadOnlyList<int> jpegQualities) =>
            [([1, 2, 3], "image/png")];
    }
    private sealed class PatchHooks(JsonData? patch) : IPreparedToolHooks
    {
        public ValueTask<PreparedToolCallHookResult> BeforeAsync(ToolInvocation invocation, PreparedToolAction validatedAction, CancellationToken token) => ValueTask.FromResult(new PreparedToolCallHookResult());
        public ValueTask<JsonData?> AfterAsync(ToolInvocation invocation, PreparedToolAction finalAction, ToolResult result, bool isError, CancellationToken token) => ValueTask.FromResult(patch);
    }

    private static async Task ToolResultNormalization()
    {
        var invocation = new ToolInvocation(Response(StopReason.ToolUse) with { Content = [new ToolCallContent("c", "probe", JsonData.EmptyObject)] },
            new ToolCallContent("c", "probe", JsonData.EmptyObject), 0);
        var action = new PreparedToolAction("probe", "probe", PreparedToolActionKind.Path, "/authored", JsonData.EmptyObject, [], null, ImmutableDictionary<string, string>.Empty);
        var content = JsonData.Parse("""[{"type":"text","text":"shot"},{"type":"image","data":"AAAA","mimeType":"image/png"}]""");
        var result = new ToolResult([], JsonData.EmptyObject) { ContentValue = content, StructuredContent = JsonData.Parse("""{"k":1}""") };
        var hooks = new ImageNormalizingToolHooks(null, () => true, new WideCodec());
        var patch = (await hooks.AfterAsync(invocation, action, result, false, CancellationToken.None))!.Value;
        Equal("""[{"type":"text","text":"shot"},{"type":"image","data":"AQID","mimeType":"image/png"},{"type":"text","text":"[Image: original 4000x100, displayed at 2000x50. Multiply coordinates by 2.00 to map to original image.]"}]""",
            patch.GetProperty("content").GetRawText(), "normalized content");
        Equal("""{"k":1}""", patch.GetProperty("structuredContent").GetRawText(), "structured content retained without a hook");
        // The extension hook runs first; its replaced content is normalized and its structured-content decision stands.
        var hooked = new ImageNormalizingToolHooks(new PatchHooks(JsonData.Parse("""{"content":[{"type":"image","data":"BBBB","mimeType":"image/png"}]}""")), () => true, new WideCodec());
        var hookPatch = (await hooked.AfterAsync(invocation, action, result, false, CancellationToken.None))!.Value;
        Check(hookPatch.GetProperty("content")[0].GetProperty("data").GetString() == "AQID" && !hookPatch.TryGetProperty("structuredContent", out _), "Hook content was not normalized.");
        // Disabled resizing keeps a supported image unchanged and adds no patch.
        Check(await new ImageNormalizingToolHooks(null, () => false, new WideCodec()).AfterAsync(invocation, action, result, false, CancellationToken.None) is null,
            "An unchanged result produced a patch.");
    }
}
