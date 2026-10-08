using System.Collections.Immutable;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Sessions.Serialization;

namespace PiSharp.Rpc.Protocol;

public sealed partial class RpcSessionDispatcher
{
    // One bounded chooser retains the real core revision. The wire ID only looks it up;
    // it never reconstructs an attachment or authorizes a different selected state.
    private sealed record NavigationView(string Id, string CaptureId, string Mode, SessionTreeNavigationView Core,
        ImmutableArray<(string Id, string Label)> Choices);
    private NavigationView? _navigation;

    private async Task<JsonData> CaptureNavigationAsync(RpcCommandEnvelope command, CancellationToken token)
    {
        await _transitions.WaitAsync(token).ConfigureAwait(false);
        try
        {
            CheckOpen();
            if (_sessionOwner is null) throw new RpcCommandException(command.Id, command.Type, "Navigation is unavailable.");
            var expected = _sessionOwner.Current;
            if (expected.Generation != command.ExpectedGeneration || !ReferenceEquals(expected.Session, _session))
                throw new RpcCommandException(command.Id, command.Type, "Navigation generation is stale.");
            if (_session.Snapshot.IsCompacting || _session.Snapshot.Agent.IsRunning)
                throw new RpcCommandException(command.Id, command.Type, "Navigation requires an idle session.");
            var core = _sessionOwner.CaptureTree(expected, token);
            var choices = ImmutableArray.CreateBuilder<(string Id, string Label)>();
            // Traverse actual tree order. Fork retains actual user entry IDs and starts at newest.
            var stack = new Stack<string>();
            var orderedIds = command.Mode == "fork" ? core.Tree.Entries.Select(entry => entry.Id).ToImmutableArray() : core.Tree.RootIds;
            for (var i = orderedIds.Length - 1; i >= 0; i--) stack.Push(orderedIds[i]);
            var characters = 0;
            while (stack.TryPop(out var entryId))
            {
                token.ThrowIfCancellationRequested();
                var node = core.Tree.ById[entryId]; var entry = node.Entry;
                string? role = null; var text = "";
                if (entry.Kind == SessionEntryKind.Message)
                {
                    var message = entry.WireBody.Value.GetProperty("message");
                    role = message.GetProperty("role").GetString();
                    text = RpcCommandCodec.Text(new(role!, JsonData.FromElement(message)));
                }
                if (command.Mode == "tree" || role == "user" && text.Length != 0)
                {
                    var label = (role ?? entry.Kind.ToString()) + ": " + text;
                    if (label.Length > 256) label = label[..(char.IsHighSurrogate(label[255]) ? 255 : 256)];
                    characters += label.Length;
                    if (choices.Count == 512 || characters > 65_536)
                        throw new RpcDispatchException(RpcDispatchFailure.ResourceLimit);
                    choices.Add((entry.Id, label));
                }
                if (command.Mode == "fork") continue;
                var children = core.Tree.GetChronologicalChildren(entryId, token);
                if (children.Status != PiSharp.Sessions.Tree.SessionTreeOrderStatus.Completed)
                    throw new RpcCommandException(command.Id, command.Type, "Navigation order is unavailable.");
                for (var i = children.Entries.Length - 1; i >= 0; i--) stack.Push(children.Entries[i].Id);
            }
            var view = new NavigationView(Guid.NewGuid().ToString("N"), command.Id!, command.Mode!, core, choices.ToImmutable());
            var data = RpcCommandCodec.Build(writer =>
            {
                writer.WriteString("viewId", view.Id); writer.WriteString("mode", view.Mode);
                writer.WriteNumber("generation", core.Generation); writer.WriteString("leafId", core.LeafId);
                var initial = view.Mode == "fork" ? view.Choices.Length - 1 :
                    Array.FindIndex(view.Choices.ToArray(), choice => choice.Id == core.LeafId);
                writer.WriteNumber("initialIndex", Math.Max(0, initial));
                writer.WritePropertyName("choices"); writer.WriteStartArray();
                foreach (var choice in view.Choices)
                { writer.WriteStartObject(); writer.WriteString("entryId", choice.Id); writer.WriteString("label", choice.Label); writer.WriteEndObject(); }
                writer.WriteEndArray();
            }, _options.MaximumOutputBytes);
            _ = RpcCommandCodec.Success(command, data, _options);
            _navigation = view; return data;
        }
        finally { _transitions.Release(); }
    }

    private async Task<JsonData> RetireNavigationAsync(RpcCommandEnvelope command, CancellationToken token)
    {
        var response = RpcCommandCodec.Build(writer => writer.WriteBoolean("retired", true), _options.MaximumOutputBytes);
        _ = RpcCommandCodec.Success(command, response, _options);
        await _transitions.WaitAsync(token).ConfigureAwait(false);
        try
        {
            CheckOpen();
            if (_navigation is { } view && view.CaptureId == command.Since && view.Core.Generation == command.ExpectedGeneration && view.Mode == command.Mode)
                _navigation = null;
            // Idempotent retirement never clears a different workflow or replacement.
            return response;
        }
        finally { _transitions.Release(); }
    }

    private async Task<JsonData> SelectNavigationAsync(RpcCommandEnvelope command, CancellationToken token)
    {
        NavigationView view;
        await _transitions.WaitAsync(token).ConfigureAwait(false);
        try
        {
            CheckOpen();
            view = _navigation ?? throw new RpcCommandException(command.Id, command.Type, "Navigation view is retired.");
            // Fence further tree effects after a delivery fault. Concurrent commands
            // admitted before this fence remain owned and retain their own originals.
            lock (_gate) if (view.Mode == "tree" && !_sessionTreePublicationFailures.IsEmpty)
                throw new RpcCommandException(command.Id, command.Type,
                    "Session tree selection committed; lifecycle publication failed. Inspect the committed state before reopening navigation.");
            if (view.Id != command.Message || view.Mode != command.Mode || view.Core.Generation != command.ExpectedGeneration ||
                _sessionOwner is null || !ReferenceEquals(_sessionOwner.Current, view.Core.Attachment) ||
                !ReferenceEquals(_session, view.Core.Attachment.Session) || !view.Choices.Any(choice => choice.Id == command.TargetId))
                throw new RpcCommandException(command.Id, command.Type, "Navigation selection is stale or foreign.");
            _navigation = null; // Exactly one selection per captured view, even on failure.
        }
        finally { _transitions.Release(); }
        JsonData Response(string disposition, long generation, string? text, string? leaf, string sessionId) =>
            RpcCommandCodec.Build(writer =>
            {
                writer.WriteString("viewId", view.Id); writer.WriteString("mode", view.Mode);
                writer.WriteString("disposition", disposition); writer.WriteNumber("generation", generation);
                writer.WriteString("editorText", text); writer.WriteString("leafId", leaf); writer.WriteString("sessionId", sessionId);
            }, _options.MaximumOutputBytes);
        void Preflight(string? text, string? leaf, string sessionId, long generation)
        {
            if (text is { Length: > 65_536 }) throw new RpcDispatchException(RpcDispatchFailure.ResourceLimit);
            if (text is not null)
                for (var i = 0; i < text.Length; i++)
                    if (char.IsSurrogate(text[i]) && (!char.IsHighSurrogate(text[i]) || ++i == text.Length || !char.IsLowSurrogate(text[i])))
                        throw new RpcCommandException(command.Id, command.Type, "Navigation editor text is invalid UTF-16.");
            if (text is not null) RpcCommandCodec.Strict(JsonData.Parse(System.Text.Json.JsonSerializer.Serialize(new { text })),
                _options.MaximumOutputBytes, _options.MaximumJsonDepth);
            _ = RpcCommandCodec.Success(command, Response("Selected", generation, text, leaf, sessionId), _options);
        }
        if (view.Mode == "tree")
        {
            var receipt = await _sessionOwner!.NavigateTreeAsync(view.Core.Attachment,
                new(command.TargetId, view.Core.Revision), token, (preview, cancellation) =>
                {
                    cancellation.ThrowIfCancellationRequested();
                    Preflight(preview.EditorText, preview.NewLeafId, preview.SessionId, view.Core.Generation);
                    if (_selectedTreePublisher is not null)
                        _ = TreePublicationFailureResponse(command, preview.SessionId, view.Core.Generation, preview.NewLeafId);
                    return ValueTask.FromResult(true);
                }).ConfigureAwait(false);
            if (receipt.Disposition == SessionTreeNavigationDisposition.Selected && _selectedTreePublisher is not null)
            {
                Task? original = null;
                var priorCallback = _inCallback.Value;
                _inCallback.Value = true;
                try
                {
                    // Acquire once and join without the caller's postcommit cancellation.
                    // The accepted chooser supplies oldLeaf, never a later owner read.
                    original = _selectedTreePublisher(receipt, view.Core.LeafId).AsTask();
                    await original.ConfigureAwait(false);
                }
                catch (Exception direct)
                {
                    var failure = new RpcSessionTreePublicationException(receipt, original,
                        original is { IsFaulted: true } ? original.Exception! : direct, direct);
                    lock (_gate) _sessionTreePublicationFailures = _sessionTreePublicationFailures.Add(failure);
                    throw failure;
                }
                finally { _inCallback.Value = priorCallback; }
            }
            return Response(receipt.Disposition.ToString(), receipt.View.Generation, receipt.EditorText, receipt.LeafId, receipt.SessionId);
        }
        if (!_sessionOwner!.CanCreateSessions) throw new RpcCommandException(command.Id, command.Type, "Fork is unavailable.");
        var replacement = await _sessionOwner.CreateAsync(view.Core.Attachment,
            new(AgentSessionCreationKind.ForkBefore, command.TargetId), (target, text, cancellation) =>
            {
                cancellation.ThrowIfCancellationRequested();
                Preflight(text, target.Snapshot.Context.LeafId, target.Snapshot.Log.Header.Id, checked(view.Core.Generation + 1));
                PreflightReplacement(command, target, checked(view.Core.Generation + 1));
                return ValueTask.CompletedTask;
            }, token).ConfigureAwait(false);
        return replacement is null ? Response("Vetoed", view.Core.Generation, null, view.Core.LeafId, view.Core.SessionId) :
            Response("Selected", replacement.Current.Generation, replacement.SelectedText,
                replacement.Current.Session.Snapshot.Context.LeafId, replacement.Current.Session.Snapshot.Log.Header.Id);
    }
}
