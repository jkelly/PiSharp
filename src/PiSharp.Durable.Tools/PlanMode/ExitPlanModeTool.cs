// PiSharp-only (decision 0005): no upstream counterpart. exit_plan_mode submits the plan for the user's review and waits durably for it
// (UserInteraction.cs). It lives in the user-interaction extension, so the model is always offered it; outside plan mode it returns an
// error result.
using System.Text;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Env;
using PiSharp.Durable.Internal;
using PiSharp.Durable.Internal.PiAi.Validation;

namespace PiSharp.Durable.Tools;

/// <summary>The <c>exit_plan_mode</c> tool.</summary>
internal static class ExitPlanModeTool
{
    /// <summary>The largest plan, in UTF-8 bytes.</summary>
    internal const int MaxPlanBytes = 256 * 1024;

    internal const string Description =
        "Submit your plan for the user's approval. Use only in plan mode, after writing the plan (by default plan.md in the plan directory). " +
        "The call waits for the user. Approved: plan mode ends and you get the full tool set, so implement the plan next. Rejected: you stay in " +
        "plan mode and the result has the user's feedback; revise the plan and call this again.";

    /// <summary>TypeBox <c>Type.Object({ plan: Type.Optional(Type.String(...)), planFile: Type.Optional(Type.String(...)) })</c>.</summary>
    internal static readonly JsonObjectValue Parameters = (JsonObjectValue)JsonValue.Parse(
        """{"type":"object","properties":{"plan":{"type":"string","description":"The plan in Markdown. Omit it when the plan is in planFile."},"planFile":{"type":"string","description":"The plan file, relative to the plan directory or an absolute path inside it. Default: plan.md."}}}""");

    internal const string NotInPlanMode = "Not in plan mode; there is no plan to approve.";

    public static ToolRegistration Create() => Define.Tool(new ToolRegistration
    {
        Name = PlanMode.ExitToolName,
        Description = Description,
        Parameters = Parameters,
        Callers = [ToolCaller.Model],
        Replay = ToolReplay.Safe,
        Execute = ExecuteAsync,
    });

    private static async Task<ToolExecutionResult> ExecuteAsync(JsonValue arguments, IToolExecutionApi api, DurableContext context)
    {
        var args = arguments as JsonObjectValue ?? JsonObjectValue.Empty;
        // A response first, before the mode check: a rerun after the approval (which ended plan mode) returns it.
        var doc = await api.SnapshotAsync(UserInteraction.InteractionDoc, api.TaskId, context).ConfigureAwait(false);
        if (UserInteraction.ResponseOf(doc) is not null) return Result(doc!);
        if (doc?.TryGetValue("kind", out var kind) != true || kind is not JsonStringValue { Value: PlanMode.DocKind })
        {
            var state = PlanMode.StateOf(await api.SnapshotAsync(PlanMode.StateDoc, api.ConversationId, context).ConfigureAwait(false));
            if (state is not { Active: true, Directory: { Length: > 0 } directory }) return Error(NotInPlanMode);
            var (plan, planFile, problem) = await PlanAsync(args, api, directory, context).ConfigureAwait(false);
            if (problem is not null) return Error(problem);
            doc = await api.CommitAsync(async tx =>
            {
                var current = await tx.DocAsync(PlanMode.StateDoc, api.ConversationId).ConfigureAwait(false);
                var draft = await tx.DocAsync(UserInteraction.InteractionDoc, api.TaskId).ConfigureAwait(false);
                if (!draft.ContainsKey("kind"))
                {
                    if (PlanMode.Mode(current) != "plan") return null;
                    var now = UserInteraction.Now();
                    draft.Set("kind", PlanMode.DocKind);
                    draft.Set("plan", plan!);
                    if (planFile is not null) draft.Set("planFile", planFile);
                    draft.Set("directory", directory);
                    draft.Set("createdAt", now);
                    PlanMode.AddSubmission(current, api.TaskId, api.CallId, planFile, now);
                }
                return (JsonObjectValue?)draft.ToValue();
            }, context).ConfigureAwait(false);
            if (doc is null) return Error(NotInPlanMode);
            if (UserInteraction.ResponseOf(doc) is not null) return Result(doc);
        }
        await api.DetailsAsync(Details(doc!, "pending", null), context).ConfigureAwait(false);
        return Result(await InteractionWait.WaitForResponseAsync(api, context).ConfigureAwait(false));
    }

    /// <summary>The plan: the <c>plan</c> argument, or the plan file (default <c>plan.md</c>, relative to the plan directory), which must lie
    /// inside the plan directory as write and edit's containment requires. Returns why there is none instead.</summary>
    private static async Task<(string? Plan, string? PlanFile, string? Problem)> PlanAsync(JsonObjectValue args, IToolExecutionApi api, string directory,
        DurableContext context)
    {
        string? Text(string key) => args.TryGetValue(key, out var value) && value is JsonStringValue text ? text.Value : null;
        if (Text("plan") is { } inline && Js.Trim(inline).Length > 0)
            return Encoding.UTF8.GetByteCount(inline) > MaxPlanBytes ? (null, null, $"The plan is larger than {MaxPlanBytes / 1024} KiB.") : (inline, null, null);
        var env = ToolEnv.Require(api);
        var name = Text("planFile") is { } given && Js.Trim(given).Length > 0 ? given : PlanMode.DefaultPlanFile;
        var joined = Path.IsPathRooted(name) ? name : (await env.JoinPathAsync([directory, name], context).ConfigureAwait(false)).GetOrThrow();
        var path = (await env.AbsolutePathAsync(joined, context).ConfigureAwait(false)).GetOrThrow();
        if (await new PlanScopedEnv(env, directory).CheckAsync(path, context).ConfigureAwait(false) is { } denied) return (null, null, denied.Message);
        var info = await env.FileInfoAsync(path, context).ConfigureAwait(false);
        if (!info.IsOk)
            return (null, null, info.Error.Code == FileErrorCode.NotFound
                ? $"The plan file {path} does not exist. Write the plan there first, or pass it as plan."
                : $"The plan file {path} cannot be read: {info.Error.Message}");
        if (info.Value.Kind != FileKind.File) return (null, null, $"The plan file {path} is not a file.");
        if (info.Value.Size > MaxPlanBytes) return (null, null, $"The plan file {path} is larger than {MaxPlanBytes / 1024} KiB.");
        var read = await env.ReadTextFileAsync(path, context).ConfigureAwait(false);
        if (!read.IsOk) return (null, null, $"The plan file {path} cannot be read: {read.Error.Message}");
        if (Js.Trim(read.Value).Length == 0) return (null, null, $"The plan file {path} is empty. Write the plan there first, or pass it as plan.");
        if (Encoding.UTF8.GetByteCount(read.Value) > MaxPlanBytes) return (null, null, $"The plan file {path} is larger than {MaxPlanBytes / 1024} KiB.");
        return (read.Value, path, null);
    }

    /// <summary>The result of a reviewed plan.</summary>
    private static ToolExecutionResult Result(JsonObjectValue doc)
    {
        var response = UserInteraction.ResponseOf(doc)!;
        var status = response.TryGetValue("status", out var value) && value is JsonStringValue text ? text.Value : "";
        var stop = response.TryGetValue("stop", out var stopValue) && stopValue is JsonBooleanValue { Value: true };
        var feedback = response.TryGetValue("feedback", out var feedbackValue) && feedbackValue is JsonStringValue given ? given.Value : null;
        var details = Details(doc, status, response);
        switch (status)
        {
            case "approved" when stop:
                return new ToolExecutionResult
                {
                    Output = ToolContent.Text("The user approved your plan and will tell you when to start. Do not change anything yet."),
                    Details = details,
                    Control = new ToolControl { Terminate = true },
                };
            case "approved":
                var approved = "The user approved your plan. Plan mode is off and you have the full tool set. Implement the approved plan now.";
                if (Field(doc, "planFile") is { } planFile) approved += $"\n\nApproved plan ({planFile}):\n{Field(doc, "plan")}";
                return new ToolExecutionResult { Output = ToolContent.Text(approved), Details = details };
            case "rejected":
                return new ToolExecutionResult
                {
                    Output = ToolContent.Text($"The user rejected the plan; you are still in plan mode. Feedback: {feedback ?? "none"}. Revise the plan and " +
                        $"call {PlanMode.ExitToolName} again."),
                    Details = details,
                };
            default:
                return new ToolExecutionResult
                {
                    Output = ToolContent.Text("The user left plan mode without reviewing the plan. Plan mode is off; wait for the user's instructions."),
                    Details = details,
                    Control = new ToolControl { Terminate = true },
                };
        }
    }

    /// <summary>The tool slot details: <c>{kind, version, state, plan, planFile?, directory, stop?, feedback?}</c>.</summary>
    private static JsonObjectValue Details(JsonObjectValue doc, string state, JsonObjectValue? response) => new RecordJson.Writer()
        .Add("kind", PlanMode.DetailsKind)
        .Number("version", 1)
        .Add("state", state)
        .Add("plan", Field(doc, "plan") ?? "")
        .Add("planFile", Field(doc, "planFile"))
        .Add("directory", Field(doc, "directory") ?? "")
        .Add("stop", response is not null && response.TryGetValue("stop", out var stop) ? stop : null)
        .Add("feedback", response is not null && response.TryGetValue("feedback", out var feedback) ? feedback : null)
        .Build();

    private static string? Field(JsonObjectValue doc, string key) => doc.TryGetValue(key, out var value) && value is JsonStringValue text ? text.Value : null;

    private static ToolExecutionResult Error(string message) => new() { Output = ToolContent.Text(message), IsError = true };
}
