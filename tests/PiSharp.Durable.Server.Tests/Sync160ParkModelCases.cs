using System.Text.Json.Nodes;
using PiSharp.Durable.Client;

// PiSharp-only (decision 0008): the client models over the agent events of a tool call that parks (Pi Durable 1.6.0; the events are
// PiSharp.Durable.Hosting's, AgentEvents.cs). A slot's status may be `sleeping` or `waiting` in a snapshot, on the start of a call parked at
// its gates, and on the update of a park; the update of its wake says `running` and may carry the arguments. The frames here are the wire
// JSON those events have, so the cases need no server. Also the task list's state of a task that sleeps for signals.
internal static partial class Program
{
    private static JsonObject Batch(params string[] events) =>
        new() { ["type"] = "batch", ["events"] = new JsonArray([.. events.Select(text => JsonNode.Parse(text))]) };

    private static JsonObject SnapshotFrame(string snapshot) => new() { ["type"] = "snapshot", ["snapshot"] = JsonNode.Parse(snapshot), ["inbox"] = new JsonArray() };

    private const string EmptySnapshot = """{"type":"snapshot","entries":[],"tools":[],"nestedTools":[],"compactions":[],"inbox":[],"agent":{},"usage":{}}""";

    // An assistant message that calls one tool, as the entry of its message_end.
    private static string CallEntry(string callId, string name) =>
        $$$"""{"type":"message_end","entry":{"id":5,"conversationId":1,"kind":"pi.assistant","model":[{"role":"assistant","content":[{"type":"toolCall","id":"{{{callId}}}","name":"{{{name}}}","arguments":{"topic":"x"}}],"api":"faux","provider":"faux","model":"faux-1","usage":{},"stopReason":"toolUse","timestamp":1}]}}""";

    private static ToolCallView OnlyCall(AgentModel model) =>
        model.Items.OfType<AssistantItem>().SelectMany(item => item.Blocks).OfType<ToolBlockView>().Single().Call;

    internal static IEnumerable<(string, Func<Task>)> Sync160ParkModelCases() =>
    [
        Case("sync160.park-model.a-park-and-its-wake-change-the-tools-status-and-keep-one-row", () =>
        {
            var model = new AgentModel();
            model.Apply(SnapshotFrame(EmptySnapshot));
            model.Apply(Batch("""{"type":"run_start","inputs":[3]}""", CallEntry("c1", "park")));
            model.Apply(Batch("""{"type":"tool_execution_start","toolCallId":"c1","toolName":"park","taskId":12,"args":{"topic":"x"}}"""));
            model.Apply(Batch("""{"type":"tool_execution_update","toolCallId":"c1","toolName":"park","taskId":12,"output":{"append":"working\n"}}"""));
            Equal(ToolStatus.Running, OnlyCall(model).Status, "running");
            Equal("working\n", OnlyCall(model).Output, "output while running");
            // The park clears the window and says the call sleeps.
            model.Apply(Batch("""{"type":"tool_execution_update","toolCallId":"c1","toolName":"park","taskId":12,"output":{"set":""},"status":"sleeping"}"""));
            Equal(ToolStatus.Sleeping, OnlyCall(model).Status, "parked");
            Equal(null, OnlyCall(model).Output, "no output while parked");
            Equal("sleeping", Transcript.StatusText(OnlyCall(model).Status), "status text");
            // An update that names no status leaves a parked call parked.
            model.Apply(Batch("""{"type":"tool_execution_update","toolCallId":"c1","toolName":"park","taskId":12,"details":{"step":2}}"""));
            Equal(ToolStatus.Sleeping, OnlyCall(model).Status, "still parked after a details update");
            model.Apply(Batch("""{"type":"tool_execution_update","toolCallId":"c1","toolName":"park","taskId":12,"status":"waiting"}"""));
            Equal(ToolStatus.Waiting, OnlyCall(model).Status, "waiting");
            Equal("waiting", Transcript.StatusText(OnlyCall(model).Status), "waiting text");
            // The wake.
            model.Apply(Batch("""{"type":"tool_execution_update","toolCallId":"c1","toolName":"park","taskId":12,"status":"running","args":{"topic":"x"}}"""));
            Equal(ToolStatus.Running, OnlyCall(model).Status, "running again");
            Equal(1, model.Items.OfType<AssistantItem>().SelectMany(item => item.Blocks).OfType<ToolBlockView>().Count(), "one row for the call");
            Check(model.Running, "the run goes on");
        }),

        Case("sync160.park-model.a-call-parked-at-its-gates-starts-sleeping-and-takes-its-arguments-from-its-wake", () =>
        {
            var model = new AgentModel();
            model.Apply(SnapshotFrame(EmptySnapshot));
            // No assistant entry yet in this model: the live tool alone carries the call.
            model.Apply(Batch("""{"type":"tool_execution_start","toolCallId":"c1","toolName":"bash","taskId":12,"args":{},"status":"sleeping"}"""));
            Equal(0, model.Pending.Count, "a parked call is not a question waiting for a person");
            model.Apply(Batch(CallEntry("c1", "bash")));
            Equal(ToolStatus.Sleeping, OnlyCall(model).Status, "gated");
            model.Apply(Batch("""{"type":"tool_execution_update","toolCallId":"c1","toolName":"bash","taskId":12,"status":"running","args":{"topic":"approved"}}"""));
            Equal(ToolStatus.Running, OnlyCall(model).Status, "running");
        }),

        Case("sync160.park-model.a-snapshot-shows-parked-slots-and-the-answer-under-review", () =>
        {
            var model = new AgentModel();
            model.Apply(SnapshotFrame(
                """{"type":"snapshot","entries":[],"run":{"inputs":[3],"yield":{"answer":9}},"tools":[{"callId":"c1","name":"park","taskId":12,"status":"sleeping"},{"callId":"c2","name":"both","taskId":13,"status":"waiting"}],"nestedTools":[{"callId":"c2/a","parentCallId":"c2","parentTaskId":13,"name":"research","taskId":14,"arguments":{"topic":"a"},"status":"sleeping"}],"compactions":[],"inbox":[],"agent":{},"usage":{}}"""));
            Check(model.Running, "running");
            Equal<long?>(9, model.YieldAnswer, "the answer under review");
            model.Apply(Batch("""{"type":"run_yield","inputs":[3]}"""));
            Equal<long?>(null, model.YieldAnswer, "the tasks continued the run");
            model.Apply(Batch("""{"type":"run_yield","inputs":[3],"answer":11}"""));
            Equal<long?>(11, model.YieldAnswer, "the next answer");
            model.Apply(Batch("""{"type":"run_end","inputs":[3]}"""));
            Equal<long?>(null, model.YieldAnswer, "over with the run");
            Check(!model.Running, "ended");
        }),

        Case("sync160.park-model.live-status-of-a-slot", () =>
        {
            Equal(ToolStatus.Running, Transcript.LiveStatus("running"), "running");
            Equal(ToolStatus.Sleeping, Transcript.LiveStatus("sleeping"), "sleeping");
            Equal(ToolStatus.Waiting, Transcript.LiveStatus("waiting"), "waiting");
            Equal(ToolStatus.Pending, Transcript.LiveStatus("pending"), "pending");
            Equal(ToolStatus.Pending, Transcript.LiveStatus(null), "no slot");
        }),

        Case("sync160.park-model.a-sleeping-tasks-state-carries-its-signals-and-an-optional-deadline", () =>
        {
            // A sleep for signals only has no `until` (Pi Durable 1.6.0): the field is left out, as every null field of the wire is.
            var signals = new TaskNodeState { Status = "sleeping", Phase = "answered", Signals = [new TaskSignalKey("example.answer", "answer")] };
            Equal("""{"status":"sleeping","phase":"answered","signals":[{"signal":"example.answer","key":"answer"}]}""", DurableWire.Serialize(signals), "signals only");
            var both = signals with { Until = 1794229200000 };
            Equal("""{"status":"sleeping","phase":"answered","until":1794229200000,"signals":[{"signal":"example.answer","key":"answer"}]}""",
                DurableWire.Serialize(both), "signals and a deadline");
            var read = DurableWire.Read<TaskNodeState>(JsonNode.Parse(DurableWire.Serialize(both)))!;
            Equal(1794229200000d, read.Until, "until read back");
            Equal(new TaskSignalKey("example.answer", "answer"), read.Signals![0], "signals read back");
            // A v1.5.2 server's sleeping task has `until` and no signals; a time-only sleep of a 1.6.0 server looks the same.
            var old = DurableWire.Read<TaskNodeState>(JsonNode.Parse("""{"status":"sleeping","phase":"retry","until":5}"""))!;
            Equal<double?>(5, old.Until, "time only");
            Check(old.Signals is null, "no signals");
        }),

        Case("sync160.park-model.tool-end-carries-image-unsupported-warning-into-transcript-and-markdown", () =>
        {
            var model = new AgentModel();
            model.Apply(SnapshotFrame(EmptySnapshot));
            model.Apply(Batch(
                """{"type":"run_start","inputs":[3]}""",
                CallEntry("c1", "read"),
                """{"type":"tool_execution_start","toolCallId":"c1","toolName":"read","taskId":12,"args":{"path":"img.png"}}""",
                """{"type":"tool_execution_end","toolCallId":"c1","toolName":"read","entry":{"id":6,"conversationId":1,"kind":"pi.tool-result","model":[{"role":"toolResult","toolCallId":"c1","toolName":"read","content":[{"type":"text","text":"Read image"}],"details":{"imageUnsupportedByModel":"faux/text-only"},"isError":false,"timestamp":2}]},"imageUnsupportedByModel":"faux/text-only"}"""));
            var call = OnlyCall(model);
            Equal("faux/text-only", call.ImageUnsupportedByModel, "ToolCallView warning");
            var json = Transcript.ToJson(model.Items);
            Equal("faux/text-only", json[0]!["blocks"]![0]!["call"]!["imageUnsupportedByModel"]!.GetValue<string>(), "Transcript JSON warning");
            var md = Transcript.ToMarkdown(model.Items);
            Check(md.Contains("Image not sent to `faux/text-only`", StringComparison.Ordinal), "Markdown warning: " + md);
        }),
    ];
}
