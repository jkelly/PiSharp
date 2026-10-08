using System.Text.Json;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.CodingAgent.Export;
using PiSharp.Contracts;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Storage;

namespace PiSharp.Rpc.Protocol;

public sealed partial class RpcSessionDispatcher
{
    private async Task<JsonData> ExportHtmlAsync(RpcCommandEnvelope command, AgentSessionAttachment? attachment, CancellationToken token)
    {
        if (_exportHtmlWriter is null)
            throw new RpcCommandException(command.Id, command.Type, "HTML export requires an explicitly installed policy-mediated write invoker.");
        if (string.IsNullOrWhiteSpace(command.Message) || command.Message.Contains('\0') || !Path.IsPathFullyQualified(command.Message))
            throw new RpcCommandException(command.Id, command.Type, "HTML export requires an explicit absolute outputPath.");
        string path;
        try { path = Path.GetFullPath(command.Message); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new RpcCommandException(command.Id, command.Type, "HTML export outputPath is invalid."); }
        // Immutable acknowledged log/context are captured together. Never reread a live source file or substitute the physical leaf.
        if (attachment is not null) _sessionOwner!.ValidateAttachment(attachment);
        var session = attachment?.Session ?? _session; var snapshot = session.Snapshot;
        if (snapshot.IsDisposed || snapshot.IsRetired || snapshot.Fault is not null)
            throw new RpcCommandException(command.Id, command.Type, "HTML export session snapshot is unavailable.");
        if (snapshot.Log.StorageDurability == SessionLogStorageDurability.VolatileMemory)
            throw new RpcCommandException(command.Id, command.Type, "Cannot export in-memory session to HTML");
        if (!snapshot.Log.IsMaterialized)
            throw new RpcCommandException(command.Id, command.Type, "Nothing to export yet - start a conversation first");
        if (string.Equals(path, Path.GetFullPath(session.Path), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new RpcCommandException(command.Id, command.Type, "HTML export outputPath must differ from the session source.");
        using var work = CancellationTokenSource.CreateLinkedTokenSource(token, attachment?.LifetimeToken ?? CancellationToken.None);
        work.Token.ThrowIfCancellationRequested();
        var data = RpcCommandCodec.Build(writer => writer.WriteString("path", path), _options.MaximumOutputBytes);
        _ = RpcCommandCodec.Success(command, data, _options); // Complete acknowledgement must fit before any write admission/effect.
        var system = new SessionSystemReplay().Replay(snapshot.Context.Messages, work.Token);
        var tools = JsonData.Parse("[" + string.Join(',', system.Tools.Select(value => value.ToString())) + "]");
        SessionHtmlRenderResult rendered;
        try { rendered = _htmlRenderer.Render(new(snapshot.Log.Header, snapshot.Log.Entries, snapshot.Context.LeafId, system.Prompt, tools), work.Token); }
        catch (SessionHtmlRenderException error) { throw new RpcCommandException(command.Id, command.Type, error.Message); }
        if (attachment is not null) _sessionOwner!.ValidateAttachment(attachment);
        work.Token.ThrowIfCancellationRequested();
        // Host-created write envelope enters the same trusted adapter/final-action policy/owned I/O pipeline.
        // The invoker is borrowed; no grant, adapter, transform, model call, transcript entry or tool event is installed here.
        var call = new ToolCallContent("rpc-export-html", "write", JsonData.Parse(JsonSerializer.Serialize(new { path, content = rendered.Html })));
        var model = snapshot.Agent.Model;
        var assistant = new AssistantMessage(model.Api, model.Provider, model.Id, _clock(), [call], new(0, 0, 0, 0, 0, new(0, 0, 0, 0, 0)), StopReason.ToolUse);
        var outcome = await _exportHtmlWriter.ExecuteFinalizedAsync(new(assistant, call, 0),
            static (_, _) => ValueTask.CompletedTask, work.Token).ConfigureAwait(false);
        var result = outcome.Result;
        if (outcome.IsError || result.Failure is not null)
            throw new RpcCommandException(command.Id, command.Type, result.Failure?.Kind switch
            {
                ToolFailureKind.Blocked => "HTML export write was denied.",
                ToolFailureKind.Canceled => "HTML export write canceled; output may have changed.",
                _ => "HTML export write failed; output may have changed."
            });
        return data;
    }
}
