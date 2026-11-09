using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Protocols.PiMessages;
using PiSharp.Contracts;

internal static class IncrementalSnapshotTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("R5 interleaved nested tool snapshots retain history and malformed finals", History);
        yield return ("R5 Pi Messages deferred snapshots match every eager current view", Equivalence);
        yield return ("R5 Pi Messages nested snapshot and payload limits remain eager", Limits);
        yield return ("R5 tool snapshot allocation scales at 64KiB 256KiB 1MiB", Allocations);
    }
    private static readonly ChatRequest Request = new(new("offline", "pi-messages", "offline"), [], 0);
    private static PiMessagesEventMapper Mapper(PiMessagesOptions? options = null) => new(Request, options ?? new(JsonData.EmptyObject));
    private static JsonData Dto(object value) => JsonData.Parse(JsonSerializer.Serialize(value));
    private static object Start(int index) => new { type = "toolcall_start", contentIndex = index, id = "tool" + index, toolName = "lookup" };
    private static object Delta(int index, string text) => new { type = "toolcall_delta", contentIndex = index, delta = text };
    private static object End(int index) => new { type = "toolcall_end", contentIndex = index, toolCall = new { type = "toolCall" } };
    private static string Mistral(int index, string text) => "data: " + JsonSerializer.Serialize(new
    { choices = new[] { new { delta = new { tool_calls = new[] { new { index, id = "tool" + index, function = new { name = "lookup", arguments = text } } } } } } }) + "\n\n";
    private static string Finish => "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\ndata: [DONE]\n\n";
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task History()
    {
        var parts = new (int Index, string Text)[] { (0, "{\"nested\":{\"value\":\"a"), (1, "{\"other\":[1"), (0, "b"), (1, ",2]}"), (0, " trailing malformed") };
        var mapper = Mapper(); mapper.Convert(Dto(new { type = "start" }));
        mapper.Convert(Dto(Start(0))); mapper.Convert(Dto(Start(1)));
        var pi = parts.Select(part => mapper.Convert(Dto(Delta(part.Index, part.Text)))).ToArray();
        mapper.Convert(Dto(End(0))); mapper.Convert(Dto(End(1)));
        var error = mapper.Convert(Dto(new { type = "error", reason = "error", usage = PiWireJson.WriteMessage(new AssistantMessage("pi-messages", "offline", "offline", 0, [], TokenUsage.Zero, StopReason.Pending)).Value.GetProperty("usage"), errorMessage = "late failure" }));
        Check(error is StreamError { Message.Content.Length: 2 }, "Pi provider error lost partial tools.");
        var oldPi = pi[0].SourceEmissionSnapshot!.Value.GetProperty("value").GetProperty("partial").GetProperty("content");
        Check(oldPi[0].GetProperty("arguments").GetProperty("nested").GetProperty("value").GetString() == "a", "Pi history changed.");
        Check(!oldPi[1].GetProperty("arguments").EnumerateObject().Any(), "Pi interleaved history changed.");
        var mistral = await MistralStreamingPerformanceTests.Read(string.Concat(parts.Select(part => Mistral(part.Index, part.Text))) + Finish, true);
        var deltas = mistral.OfType<ToolCallDelta>().ToArray();
        var old = deltas[0].SourceEmissionSnapshot!.Value.GetProperty("content");
        Check(old[0].GetProperty("arguments").GetProperty("nested").GetProperty("value").GetString() == "a", "Mistral history changed.");
        Check(old.GetArrayLength() == 1, "Later interleaved tool leaked backward.");
        var final = (StreamDone)mistral[^1];
#pragma warning disable PISHARP_AI001 // Authored control verifies the experimental argument-error contract.
        Check(ToolCallArgumentsResult.ErrorOf((ToolCallContent)final.Message.Content[0]) is not null, "Malformed Mistral final lost its arguments error.");
#pragma warning restore PISHARP_AI001
        Check(((ToolCallContent)final.Message.Content[1]).Arguments.Value.GetProperty("other").GetArrayLength() == 2, "Interleaved final lost arguments.");
        var failed = await MistralStreamingPerformanceTests.Read(Mistral(0, "{\"nested\":{\"value\":\"kept\"}}") + "data: {\"choices\":[{\"delta\":{\"unexpected\":true}}]}\n\n", true);
        Check(failed[^1] is StreamError failure && failure.Message.Content.OfType<ToolCallContent>().Single().Arguments.Value.GetProperty("nested").GetProperty("value").GetString() == "kept", "Late failure lost accepted Mistral partial.");
        var clone = pi[0] with { SourceEmissionSnapshot = null };
        Check(clone.SourceEmissionSnapshot is null && pi[0].SourceEmissionSnapshot is not null, "Snapshot clearing changed original frame.");
    }
    private static Task Equivalence()
    {
        var mapper = Mapper();
        object[] events = [new { type = "start" }, new { type = "text_start", contentIndex = 0 },
            new { type = "text_delta", contentIndex = 0, delta = "\u03c0\\\"" },
            new { type = "text_end", contentIndex = 0, content = "\u03c0\\\"" },
            Start(1), Delta(1, "{\"a\":\"one"), Start(2), Delta(2, "{\"b\":[true]}"), Delta(1, " two\"}"), End(1), End(2),
            new { type = "thinking_start", contentIndex = 3 }, new { type = "thinking_delta", contentIndex = 3, delta = "thought" },
            new { type = "thinking_end", contentIndex = 3, content = "thought", contentSignature = "signed", redacted = true }];
        var observations = new List<(StreamEvent Frame, PiMessagesValueObservation Expected)>();
        foreach (var item in events) { var frame = mapper.Convert(Dto(item)); observations.Add((frame, mapper.Current)); }
        foreach (var (frame, expected) in observations)
        {
            var snapshot = frame.SourceEmissionSnapshot!.Value;
            Check(JsonElement.DeepEquals(snapshot.GetProperty("value").GetProperty("partial"), expected.Value.Value), "Deferred Pi snapshot differs from its eager current view.");
            Check(snapshot.GetProperty("ownUndefinedPaths").EnumerateArray().Select(path => path.GetString()).SequenceEqual(expected.OwnUndefinedPaths.Select(path => "/partial" + path)), "Undefined-path snapshot changed.");
        }
        return Task.CompletedTask;
    }
    private static async Task Limits()
    {
        var depth = Mapper(new(JsonData.EmptyObject) { MaximumJsonDepth = 5 });
        depth.Convert(Dto(new { type = "start" })); depth.Convert(Dto(Start(0)));
        Reject(() => depth.Convert(Dto(Delta(0, "{\"nested\":{\"value\":1}}"))));
        var size = Mapper(new(JsonData.EmptyObject) { MaximumPayloadBytes = 800 });
        size.Convert(Dto(new { type = "start" })); size.Convert(Dto(Start(0)));
        Reject(() => size.Convert(Dto(Delta(0, "{\"text\":\"" + new string('x', 450)))));
        var deep = string.Concat(Enumerable.Repeat("{\"x\":", 998)) + "1" + new string('}', 998);
        var rejected = await MistralStreamingPerformanceTests.Read(Mistral(0, deep) + Finish);
        Check(rejected[^1] is StreamError && !rejected.OfType<ToolCallStarted>().Any(), "Mistral deferred nesting failure escaped chunk admission.");
        static void Reject(Action action)
        {
            try { action(); } catch (PiMessagesException error) when (error.Failure == PiMessagesFailure.ResourceLimit) { return; }
            throw new InvalidOperationException("Expected eager snapshot admission failure.");
        }
    }
    private static async Task Allocations()
    {
        var sizes = new[] { 64 * 1024, 256 * 1024, 1024 * 1024 };
        await Measure(4096, false); await Measure(4096, true);
        foreach (var pi in new[] { false, true })
        {
            var bytes = new List<long>();
            foreach (var size in sizes) bytes.Add(await Measure(size, pi));
            Console.WriteLine($"R5 {(pi ? "PiMessages" : "Mistral")} allocation bytes: " + string.Join(", ", sizes.Zip(bytes, (size, value) => $"{size}={value}")));
            Check(bytes[1] < bytes[0] * 7 && bytes[2] < bytes[1] * 7, "Fourfold input caused near-quadratic snapshot allocations: " + string.Join(",", bytes));
        }
    }
    private static async Task<long> Measure(int size, bool pi)
    {
        const int chunkSize = 1024;
        var chunk = new string('x', chunkSize);
        var deltas = Enumerable.Repeat(chunk, size / chunkSize).Prepend("{\"text\":\"").Append("\"}").ToArray();
        var dtos = pi ? deltas.Select(text => Dto(Delta(0, text))).ToArray() : [];
        var wire = pi ? "" : string.Concat(deltas.Select(text => Mistral(0, text))) + Finish;
        var before = GC.GetTotalAllocatedBytes(true);
        if (pi)
        {
            var mapper = Mapper(); mapper.Convert(Dto(new { type = "start" })); mapper.Convert(Dto(Start(0)));
            var frames = dtos.Select(mapper.Convert).ToArray();
            mapper.Convert(Dto(new { type = "toolcall_end", contentIndex = 0,
                toolCall = new { type = "toolCall", arguments = new { text = new string('x', size) } } }));
            Check(frames.Length == deltas.Length && mapper.Current.Value.Value.GetProperty("content")[0].GetProperty("arguments").GetProperty("text").GetString()!.Length == size, "Pi allocation run lost content.");
            GC.KeepAlive(frames);
        }
        else
        {
            var frames = await MistralStreamingPerformanceTests.Read(wire, true);
            Check(frames[^1] is StreamDone done && ((ToolCallContent)done.Message.Content[0]).Arguments.Value.GetProperty("text").GetString()!.Length == size, "Mistral allocation run lost content.");
            GC.KeepAlive(frames);
        }
        return GC.GetTotalAllocatedBytes(true) - before;
    }
}
