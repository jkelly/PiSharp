using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using PiSharp.Agent;
using PiSharp.Contracts;
using PiSharp.Extensions.Runtime;

namespace PiSharp.Extensions.Agent;

/// <summary>Executes only the captured extension registration through its registry's owned dispatch.</summary>
internal sealed class ExtensionToolAdapter(ExtensionRegistry registry, ExtensionRegistrySnapshot snapshot,
    ExtensionToolRegistrationInfo tool, ExtensionToolArgumentValidator validateArguments,
    ToolResultValueOptions resultValues, CancellationToken sessionToken) : IInvocationPreparedToolAdapter, IInitialToolArgumentPreparationAdapter,
    IToolArgumentSchemaAdapter
{
    private readonly string target = tool.OwnerId + "/" + tool.OwnerGeneration.ToString(CultureInfo.InvariantCulture) + "/" + tool.RegistrationId;
    public string Name => tool.Name;
    /// <summary>Source validateToolArguments against the registration's parameters, after its prepareArguments.</summary>
    public ToolArgumentSchema? ArgumentSchema { get; } = new(tool.ValidationParameters ?? tool.Parameters, tool.ParametersOrigin);
    public async ValueTask<JsonData> PrepareInitialArgumentsAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); sessionToken.ThrowIfCancellationRequested();
        // Legacy registrations retain their original failure staging and do not gain a callback admission.
        if (!tool.HasInitialArgumentPreparation) return invocation.Call.Arguments;
        try { return await registry.PrepareToolArgumentsAsync(snapshot, Name, invocation.Call.Arguments, cancellationToken, sessionToken).ConfigureAwait(false); }
        catch (ExtensionToolArgumentPreparationException error) { throw new ToolArgumentPreparationException(error.Message, error); }
    }
    public ValueTask<PreparedToolAction> PrepareAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); sessionToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new PreparedToolAction(Name, "invoke", PreparedToolActionKind.Extension,
            target, invocation.Call.Arguments, [], null, ImmutableDictionary<string, string>.Empty));
    }
    public async ValueTask<bool> ValidateAsync(PreparedToolAction action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); sessionToken.ThrowIfCancellationRequested();
        if (action.ToolName != Name || action.Operation != "invoke" || action.Kind != PreparedToolActionKind.Extension ||
            action.Target != target || action.WorkingDirectory is not null || action.CommandArguments.IsDefault ||
            !action.CommandArguments.IsEmpty || action.Environment is null || !action.Environment.IsEmpty ||
            action.Arguments.Value.ValueKind != JsonValueKind.Object) return false;
        var valid = await validateArguments(tool, action.Arguments, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested(); sessionToken.ThrowIfCancellationRequested();
        return valid;
    }
    public async ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action, CancellationToken cancellationToken)
    {
        try
        {
            var result = await registry.InvokeToolAsync(snapshot, Name, action.Arguments,
                cancellationToken, sessionToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested(); sessionToken.ThrowIfCancellationRequested();
            return ToolResultValueCodec.Read(result, resultValues);
        }
        catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested ||
            sessionToken.IsCancellationRequested || error.CancellationToken.IsCancellationRequested)
        {
            // Extension-lifetime cancellation is independent of the Agent work token.
            // Registry invocation has already joined the actual callback's finally before returning here.
            return ToolResult.Error(ToolFailureKind.Canceled, "Extension tool invocation was cancelled.");
        }
    }

    public async ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, PreparedToolAction action,
        ToolProgressCallback onProgress, CancellationToken cancellationToken)
    {
        try
        {
            ValueTask Update(JsonData partial, CancellationToken token) => ToolProgressDelivery.ReportAndWaitAsync(onProgress,
                ToolResultValueCodec.Read(partial, resultValues), token);
            ExtensionToolBroker? broker = null;
            if (invocation.Context is { } context)
                broker = new(context.Tools, context.SessionGeneration, context.ParentToolCallId, context.CallDepth,
                    async (name, arguments, options) =>
                    {
                        async ValueTask NestedUpdate(ToolResult partial, CancellationToken token)
                        {
                            if (options.OnUpdate is { } report) await report(partial.ToJson(resultValues), token).ConfigureAwait(false);
                        }
                        var outcome = await context.ExecuteToolAsync(name, arguments, NestedUpdate, options.CancellationToken).ConfigureAwait(false);
                        return new(outcome.Invocation.Call.Id, outcome.Invocation.Call.Name, outcome.Result.ToJson(resultValues),
                            outcome.IsError, outcome.Invocation.ParentToolCallId ?? context.ToolCallId);
                    });
            var result = broker is null
                ? await registry.InvokeToolAsync(snapshot, Name, action.Arguments, invocation.Call.Id, Update,
                    cancellationToken, sessionToken).ConfigureAwait(false)
                : await registry.InvokeToolWithBrokerAsync(snapshot, Name, action.Arguments, invocation.Call.Id, Update, broker,
                    cancellationToken, sessionToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested(); sessionToken.ThrowIfCancellationRequested();
            return ToolResultValueCodec.Read(result, resultValues);
        }
        catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested ||
            sessionToken.IsCancellationRequested || error.CancellationToken.IsCancellationRequested)
        {
            return ToolResult.Error(ToolFailureKind.Canceled, "Extension tool invocation was cancelled.");
        }
    }
}
