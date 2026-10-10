// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/modes/rpc/rpc-mode.ts (export_html) and
// core/agent-session.ts (exportToHtml) over core/export-html/index.ts (exportSessionToHtml).
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
    private static readonly JsonSerializerOptions ExportWriteJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// export_html as Pi: the upstream page (template, vendored scripts, theme CSS variables, base64 session data). An omitted or empty
    /// outputPath defaults to <c>pi-session-&lt;session file basename&gt;.html</c>; a relative path is resolved against the process
    /// working directory and returned as given. The page is written through the host's policy-mediated write invoker, and the
    /// session file itself stays protected (owner decision 0004).
    /// </summary>
    private async Task<JsonData> ExportHtmlAsync(RpcCommandEnvelope command, AgentSessionAttachment? attachment, CancellationToken token)
    {
        if (_exportHtmlWriter is null)
            throw new RpcCommandException(command.Id, command.Type, "HTML export requires an explicitly installed policy-mediated write invoker.");
        if (command.Message?.Contains('\0') == true)
            throw new RpcCommandException(command.Id, command.Type, "HTML export outputPath is invalid.");
        // Immutable acknowledged log/context are captured together. Never reread a live source file or substitute the physical leaf.
        if (attachment is not null) _sessionOwner!.ValidateAttachment(attachment);
        var session = attachment?.Session ?? _session; var snapshot = session.Snapshot;
        if (snapshot.IsDisposed || snapshot.IsRetired || snapshot.Fault is not null)
            throw new RpcCommandException(command.Id, command.Type, "HTML export session snapshot is unavailable.");
        if (snapshot.Log.StorageDurability == SessionLogStorageDurability.VolatileMemory)
            throw new RpcCommandException(command.Id, command.Type, "Cannot export in-memory session to HTML");
        if (!snapshot.Log.IsMaterialized)
            throw new RpcCommandException(command.Id, command.Type, "Nothing to export yet - start a conversation first");
        var outputPath = SessionHtmlExport.OutputPath(command.Message, session.Path);
        string path;
        try { path = Path.GetFullPath(Path.Combine(_htmlExport.WorkingDirectory ?? Environment.CurrentDirectory, outputPath)); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new RpcCommandException(command.Id, command.Type, "HTML export outputPath is invalid."); }
        if (string.Equals(path, Path.GetFullPath(session.Path), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new RpcCommandException(command.Id, command.Type, "HTML export outputPath must differ from the session source.");
        using var work = CancellationTokenSource.CreateLinkedTokenSource(token, attachment?.LifetimeToken ?? CancellationToken.None);
        work.Token.ThrowIfCancellationRequested();
        var data = RpcCommandCodec.Build(writer => writer.WriteString("path", outputPath), _options.MaximumOutputBytes);
        _ = RpcCommandCodec.Success(command, data, _options); // Complete acknowledgement must fit before any write admission/effect.
        var system = new SessionSystemReplay().Replay(snapshot.Context.Messages, work.Token);
        string html;
        try
        {
            var source = SessionExportSource.FromLog(snapshot.Log.Header, snapshot.Log.Entries, snapshot.Context.LeafId, session.Path);
            // exportToHtml(this.state): the transcript's prompt (agent.state.systemPrompt) and the in-memory loadout (agent.state.tools).
            var state = new SessionExportAgentState(system.Prompt, session.GetActiveToolDeclarations(work.Token).Select(tool => SessionExportTool.FromJson(tool.ToString())).ToList());
            html = SessionHtmlExport.GenerateHtml(SessionHtmlExport.BuildSessionData(source, state, _htmlExport.CreateToolRenderer?.Invoke()),
                _htmlExport.Themes, _htmlExport.ResolveThemeName());
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or FormatException or IOException)
        { throw new RpcCommandException(command.Id, command.Type, error.Message); }
        if (attachment is not null) _sessionOwner!.ValidateAttachment(attachment);
        work.Token.ThrowIfCancellationRequested();
        // Host-created write envelope enters the same trusted adapter/final-action policy/owned I/O pipeline.
        // The invoker is borrowed; no grant, adapter, transform, model call, transcript entry or tool event is installed here.
        var call = new ToolCallContent("rpc-export-html", "write", JsonData.Parse(JsonSerializer.Serialize(new { path, content = html }, ExportWriteJson)));
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
