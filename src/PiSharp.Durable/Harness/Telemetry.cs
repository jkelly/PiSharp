// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/src/harness/telemetry.ts (DURABLE_TELEMETRY_SCHEMA), ported in the
// Pi v1.5.0 sync (area telemetry, 2ec55d808). Its closed sets list what this port can raise so far: the report kinds and reasons of
// ReportKind and ReportReason, and the input and write submission types. Area restarts added the task_interrupted kind and the
// restart_limit and interrupted reasons (in upstream's order, as the enums list them). Two later areas of the sync extend them with what
// they bring: admission the continue type and submit(tx, draft)'s provisional admission span (whose part of the pi.durable.submit
// description is left out until then), vocabulary its renames. Every
// attribute addresses a durable record by ID or names a fixed category; none carries content.
using PiSharp.Telemetry;

namespace PiSharp.Durable;

/// <summary>Durable's content-free span, event and metric vocabulary (upstream <c>DURABLE_TELEMETRY_SCHEMA</c>). A Harness records it through
/// <see cref="HarnessOptions.Telemetry"/>; IDs address durable records, never content.</summary>
public static class DurableTelemetry
{
    /// <summary>The public submission admission span.</summary>
    public const string SubmitSpanName = "pi.durable.submit";

    /// <summary>One task phase invocation.</summary>
    public const string PhaseSpanName = "pi.durable.phase";

    /// <summary><c>DURABLE_TELEMETRY_SCHEMA</c>.</summary>
    public static TelemetrySchemaDefinition Schema { get; } = TelemetrySchema.Define(BuildSchema());

    /// <summary>The <c>pi.durable.report</c> event's attributes of <paramref name="info"/> (upstream <c>safeReport</c>): its fixed kind, and
    /// by kind the reason, the extension and the task ID. A field the info lacks is absent.</summary>
    internal static SpanAttributes ReportAttributes(ReportInfo info)
    {
        var attributes = new SpanAttributes { ["type"] = ReportInfo.KindName(info.Kind) };
        switch (info.Kind)
        {
            case ReportKind.TaskFaulted:
            case ReportKind.TaskInterrupted:
            case ReportKind.TaskOutdated:
                attributes["reason"] = info.Reason is { } reason ? ReportInfo.ReasonName(reason) : null;
                attributes["extension"] = info.Extension;
                attributes["task.id"] = info.Task?.Id.Value;
                break;
            case ReportKind.MigrationFailed:
            case ReportKind.TaskError:
            case ReportKind.ExtensionError:
                attributes["extension"] = info.Extension;
                attributes["task.id"] = info.Task?.Id.Value;
                break;
            case ReportKind.HostError:
                attributes["task.id"] = info.Task?.Id.Value;
                break;
        }
        return attributes;
    }

    private static TelemetrySchemaDefinition BuildSchema()
    {
        var entry = new TelemetryEventDefinition("A transcript entry was committed; content remains in the transcript.", Map(
            ("entry.id", new TelemetryAttributeDefinition(TelemetryAttributeType.Number, "Transcript entry ID.")
            { Required = true, Cardinality = TelemetryCardinality.High }),
            ("entry.kind", new TelemetryAttributeDefinition(TelemetryAttributeType.String, "Entry kind.") { Required = true })));
        var type = new TelemetryAttributeDefinition(TelemetryAttributeType.String, "Report kind (`Report.kind`).")
        {
            Required = true,
            Values = [.. Enum.GetValues<ReportKind>().Select(kind => (AttributeValue)ReportInfo.KindName(kind))],
        };
        var reportAttributes = Map(
            ("type", type),
            ("reason", new TelemetryAttributeDefinition(TelemetryAttributeType.String, "Fixed report reason, when present.")
            {
                Required = false,
                Values = [.. Enum.GetValues<ReportReason>().Select(reason => (AttributeValue)ReportInfo.ReasonName(reason))],
            }),
            ("extension", new TelemetryAttributeDefinition(TelemetryAttributeType.String, "Registered extension name, when present.") { Required = false }),
            ("task.id", new TelemetryAttributeDefinition(TelemetryAttributeType.Number, "Task ID, when present.")
            { Required = false, Cardinality = TelemetryCardinality.High }));
        var report = new TelemetryEventDefinition("A report was raised; no error payload is recorded.", reportAttributes);
        var submission = new TelemetryEventDefinition("A submission was settled.", Map(
            ("submission.id", new TelemetryAttributeDefinition(TelemetryAttributeType.Number, "Submission ID.")
            { Required = true, Cardinality = TelemetryCardinality.High }),
            ("status", new TelemetryAttributeDefinition(TelemetryAttributeType.String, "Settlement.") { Required = true, Values = ["done", "unanswered"] }),
            ("answer.entry_id", new TelemetryAttributeDefinition(TelemetryAttributeType.Number, "Answer entry ID.")
            { Required = false, Cardinality = TelemetryCardinality.High })));
        var submit = new TelemetrySpanDefinition(
            "Public submission admission with its commit, for submit(draft, context). Inputs start root traces linked to their callers; writes " +
            "are children. Internal admissions emit phase events only.",
            TelemetryParentDefinition.Any,
            Map(("submission.type", new TelemetryAttributeDefinition(TelemetryAttributeType.String, "Submission type.")
                { Required = true, Values = ["input", "write"] }),
                ("conversation.id", new TelemetryAttributeDefinition(TelemetryAttributeType.Number, "Conversation ID.")
                { Required = true, Cardinality = TelemetryCardinality.High })),
            Map(("submission.id", new TelemetryAttributeDefinition(TelemetryAttributeType.Number, "Admitted submission ID.")
                { Cardinality = TelemetryCardinality.High }),
                ("result", new TelemetryAttributeDefinition(TelemetryAttributeType.String, "Admission result; stale writes report done.")
                { Values = ["queued", "placed", "done"] })),
            new("Admission failed."))
        {
            Events = Map(("pi.durable.entry", entry), ("pi.durable.submission", submission), ("pi.durable.report", report)),
        };
        var phase = new TelemetrySpanDefinition(
            "One task phase invocation, including its decision commit, never a wait or restart.",
            TelemetryParentDefinition.Any,
            Map(("task.id", new TelemetryAttributeDefinition(TelemetryAttributeType.Number, "Task ID.") { Required = true, Cardinality = TelemetryCardinality.High }),
                ("task.name", new TelemetryAttributeDefinition(TelemetryAttributeType.String, "Registered task name.") { Required = true }),
                ("phase", new TelemetryAttributeDefinition(TelemetryAttributeType.String, "Invoked checkpoint phase.") { Required = true }),
                ("kind", new TelemetryAttributeDefinition(TelemetryAttributeType.String, "Invocation kind.") { Required = true, Values = ["run", "abort"] }),
                ("attempt", new TelemetryAttributeDefinition(TelemetryAttributeType.Number, "Checkpoint attempt, when present.") { Required = false }),
                ("resumed", new TelemetryAttributeDefinition(TelemetryAttributeType.Boolean, "First invocation of a task loaded in this process.")
                { Required = true })),
            Map(("outcome", new TelemetryAttributeDefinition(TelemetryAttributeType.String, "next=<phase>, waiting, sleeping, done, faulted, or aborted.")),
                ("tool.name", new TelemetryAttributeDefinition(TelemetryAttributeType.String, "Called tool name.")),
                ("tool.call_id", new TelemetryAttributeDefinition(TelemetryAttributeType.String, "Transcript tool call ID.") { Cardinality = TelemetryCardinality.High })),
            new("Faulted, failed, and orphaned outcomes use task_faulted, task_failed, and task_orphaned with a diagnostic capped at 500 " +
                "characters. Aborted outcomes keep status ok."))
        {
            Events = Map(
                ("pi.durable.entry", entry),
                ("pi.durable.submission", submission),
                ("pi.durable.report", report),
                ("pi.durable.checkpoint", new TelemetryEventDefinition("The running checkpoint's phase changed while the invocation continued.",
                    Map(("phase", new TelemetryAttributeDefinition(TelemetryAttributeType.String, "Committed checkpoint phase.") { Required = true })))),
                ("pi.durable.replay", new TelemetryEventDefinition("A nested tool call was answered from its stored result.",
                    Map(("tool.name", new TelemetryAttributeDefinition(TelemetryAttributeType.String, "Called tool name.") { Required = true }))))),
        };
        return new(1, Map((SubmitSpanName, submit), (PhaseSpanName, phase)))
        {
            Metrics = Map(
                ("pi.durable.reports", new TelemetryMetricDefinition(TelemetryMetricKind.Counter, "Reports raised, grouped by their fixed kind.",
                    Map(("type", type)))),
                ("pi.durable.phase.duration", new TelemetryMetricDefinition(TelemetryMetricKind.Histogram,
                    "Duration of a phase invocation and its decision commit.", Map(
                        ("task", new TelemetryAttributeDefinition(TelemetryAttributeType.String, "Registered task name.") { Required = true }),
                        ("phase", new TelemetryAttributeDefinition(TelemetryAttributeType.String, "Invoked checkpoint phase.") { Required = true }),
                        ("outcome", new TelemetryAttributeDefinition(TelemetryAttributeType.String, "Phase outcome.") { Required = true }))) { Unit = "ms" }),
                ("pi.durable.tasks.active", new TelemetryMetricDefinition(TelemetryMetricKind.UpDownCounter,
                    "Live tasks held by this Harness; decremented at terminal settlement or close.", Map(
                        ("task", new TelemetryAttributeDefinition(TelemetryAttributeType.String, "Registered task name.") { Required = true }))))),
        };
    }

    private static OrderedDictionary<string, T> Map<T>(params (string Name, T Value)[] items)
    {
        var map = new OrderedDictionary<string, T>(StringComparer.Ordinal);
        foreach (var (name, value) in items) map.Add(name, value);
        return map;
    }
}
