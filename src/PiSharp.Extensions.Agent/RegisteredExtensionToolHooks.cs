using System.Text;
using System.Text.Json;
using PiSharp.Agent;
using PiSharp.Contracts;
using PiSharp.Extensions.Events;
using PiSharp.Extensions.Runtime;
using PiSharp.Extensions.Runtime.Dispatch;

namespace PiSharp.Extensions.Agent;

/// <summary>One binding's actual tools/hooks revision. No callback mirrors or per-invocation side maps.</summary>
internal sealed class RegisteredExtensionToolHooks : IPreparedToolHooks
{
    private readonly ExtensionRegistrySnapshot snapshot;
    private readonly RegisteredExtensionEventDispatcher dispatcher;
    private readonly ToolResultValueOptions resultValues;
    private readonly CancellationToken session;
    private readonly Func<ExtensionEventDiagnostic, CancellationToken, ValueTask>? reportDiagnostic;

    internal RegisteredExtensionToolHooks(ExtensionRegistry registry, ExtensionRegistrySnapshot snapshot,
        ToolResultValueOptions resultValues, ExtensionEventDispatchOptions? options, int maximumHandlers,
        CancellationToken session, Func<ExtensionEventDiagnostic, CancellationToken, ValueTask>? reportDiagnostic)
    {
        this.snapshot = snapshot;
        this.resultValues = resultValues;
        this.session = session;
        this.reportDiagnostic = reportDiagnostic;
        dispatcher = new(registry, value => { _ = ToolResultValueCodec.Read(value, resultValues); }, options, maximumHandlers);
    }

    public async ValueTask<PreparedToolCallHookResult> BeforeAsync(ToolInvocation invocation,
        PreparedToolAction validatedAction, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); session.ThrowIfCancellationRequested();
        if (snapshot.ToolCallHandlers.IsEmpty) return new();
        var reduced = await dispatcher.DispatchToolCallAsync(snapshot,
            new(invocation.Call.Name, invocation.Call.Id, validatedAction.Arguments, invocation.ParentToolCallId), cancellationToken, session).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested(); session.ThrowIfCancellationRequested();
        var terminate = reduced.Decision is not null && reduced.Decision.Value.TryGetProperty("terminate", out var flag) &&
            flag.ValueKind == JsonValueKind.True;
        var reason = reduced.Blocked && reduced.Decision!.Value.TryGetProperty("reason", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;
        return new(reduced.ArgumentsReplaced ? reduced.Event.Arguments : null, reduced.Blocked, reduced.Blocked && terminate) { Reason = reason };
    }

    public async ValueTask<JsonData?> AfterAsync(ToolInvocation invocation, PreparedToolAction finalAction,
        ToolResult result, bool isError, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); session.ThrowIfCancellationRequested();
        if (snapshot.ToolResultHandlers.IsEmpty) return null;
        // Runner's event has execution disposition, not a tool's opaque own result isError.
        // Keep the original result intact for the existing after-hook's separate result/outcome merge.
        var supplied = result.WithProperty("isError", JsonData.Parse(isError ? "true" : "false")).ToJson(resultValues);
        var reduced = await dispatcher.DispatchToolResultAsync(snapshot,
            new(invocation.Call.Name, invocation.Call.Id, finalAction.Arguments, supplied, invocation.ParentToolCallId, OutcomeIsError: isError),
            cancellationToken, session).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested(); session.ThrowIfCancellationRequested();
        if (reportDiagnostic is not null)
            foreach (var diagnostic in reduced.Diagnostics)
            {
                await reportDiagnostic(diagnostic, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested(); session.ThrowIfCancellationRequested();
            }
        if (!reduced.Modified) return null;

        // Whole Runner returns these five fields. Missing native fields remain missing instead of
        // becoming JSON null; its JS own-undefined ledger remains a separate compatibility concern.
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            foreach (var name in new[] { "content", "details", "structuredContent", "isError", "usage" })
            {
                var present = reduced.Event.Result.Value.TryGetProperty(name, out var value);
                if (name == "content")
                {
                    // agent-session's complete content projection uses hook.content ?? result.content ?? [].
                    // Apply this after every callback, so later callbacks still observe Runner's null.
                    var content = present && value.ValueKind != JsonValueKind.Null ? JsonData.FromElement(value) :
                        result.ContentValue;
                    writer.WritePropertyName(name); writer.WriteRawValue(content.ToString());
                }
                else if (name == "isError")
                { writer.WriteBoolean(name, present && value.ValueKind != JsonValueKind.Null ? value.GetBoolean() : isError); }
                else if (present)
                { writer.WritePropertyName(name); writer.WriteRawValue(value.GetRawText()); }
            }
            writer.WriteEndObject();
        }
        var patch = JsonData.Parse(Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length)));
        _ = ToolResultValueCodec.Read(patch, resultValues);
        return patch;
    }
}
