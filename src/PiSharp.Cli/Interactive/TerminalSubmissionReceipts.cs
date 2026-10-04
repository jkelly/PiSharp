using System.Text.Json;
using System.Threading.Channels;
using PiSharp.Contracts;

namespace PiSharp.Cli.Interactive;

internal readonly record struct TerminalSubmissionIdentity(Guid InputLifetime, long Sequence);
internal sealed record TerminalSubmittedLine(TerminalSubmissionIdentity Identity, string Text);
internal sealed record TerminalAcceptedLine(TerminalSubmissionIdentity Identity, string Text, string Command, bool SessionReplaced)
{
    private readonly TaskCompletionSource applied = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task Applied => applied.Task;
    internal void FinishApplying(Exception? error)
    { if (error is null) applied.TrySetResult(); else applied.TrySetException(error); }
}

/// <summary>Bounded acknowledgments. Only the input consumer claims them and edits recall state.</summary>
internal sealed class TerminalSubmissionReceipts
{
    internal const int MaximumPending = 32, MaximumRetainedUtf16 = 65_536;
    private sealed record Pending(TerminalSubmittedLine Line, string? RpcId = null, string? Command = null);
    private readonly object gate = new();
    private readonly Guid lifetime = Guid.NewGuid();
    private readonly Dictionary<TerminalSubmissionIdentity, Pending> pending = [];
    private readonly Dictionary<string, TerminalSubmissionIdentity> correlations = new(StringComparer.Ordinal);
    private readonly Queue<TerminalAcceptedLine> accepted = new();
    // A coalesced wake-up carries no payload. The bounded authoritative queue never loses an acknowledgment.
    private readonly Channel<bool> changed = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite, AllowSynchronousContinuations = false });
    private long sequence;
    private int retained;
    private bool completed;
    internal bool IsCompleted { get { lock (gate) return completed; } }
    internal (int Pending, int Accepted, int RetainedUtf16) Snapshot { get { lock (gate) return (pending.Count, accepted.Count, retained); } }

    internal TerminalSubmittedLine Reserve(string text)
    {
        ChatEditor.Validate(text);
        lock (gate)
        {
            if (completed) throw new InvalidOperationException("Terminal submission lifetime has ended.");
            if (sequence == long.MaxValue || pending.Count + accepted.Count >= MaximumPending || text.Length > MaximumRetainedUtf16 - retained)
                throw new InvalidOperationException("Terminal pending submissions exceed their bounded profile.");
            var line = new TerminalSubmittedLine(new(lifetime, ++sequence), text);
            pending.Add(line.Identity, new(line)); retained += text.Length; return line;
        }
    }

    internal void Correlate(TerminalSubmittedLine line, string rpcId, string command)
    {
        ArgumentException.ThrowIfNullOrEmpty(rpcId); ArgumentException.ThrowIfNullOrEmpty(command);
        lock (gate)
        {
            var item = RequirePending(line);
            if (item.RpcId is not null || correlations.ContainsKey(rpcId)) throw new InvalidOperationException("Terminal submission was already correlated.");
            correlations.Add(rpcId, line.Identity); pending[line.Identity] = item with { RpcId = rpcId, Command = command };
        }
    }

    internal void ValidatePending(TerminalSubmittedLine line) { lock (gate) RequirePending(line); }

    internal void FinishLocal(TerminalSubmittedLine line, bool acknowledged, string command = "local")
    {
        lock (gate)
        {
            var item = RequirePending(line);
            if (item.RpcId is not null) throw new InvalidOperationException("A correlated command requires its actual RPC response.");
            Finish(item, acknowledged, command, false);
        }
    }

    internal void RejectUnacknowledged(TerminalSubmittedLine line)
    {
        lock (gate)
        {
            // The response may have committed before a sender/output callback faulted. Never roll it back.
            if (pending.TryGetValue(line.Identity, out var item)) { RequirePending(line); Finish(item, false, item.Command ?? "local", false); }
        }
    }

    internal Task ObserveResponse(JsonData record)
    {
        var body = record.Value;
        if (body.GetProperty("type").GetString() != "response" || !body.TryGetProperty("id", out var idValue) || idValue.ValueKind != JsonValueKind.String) return Task.CompletedTask;
        var id = idValue.GetString()!;
        lock (gate)
        {
            if (!correlations.TryGetValue(id, out var identity)) return Task.CompletedTask; // Other host records and duplicate responses have no owning receipt.
            var item = pending[identity];
            if (body.GetProperty("command").GetString() != item.Command) throw new InvalidOperationException("Terminal response command does not match its submission.");
            var success = body.GetProperty("success").GetBoolean(); var replaced = false;
            if (success)
            {
                if (item.Command is "prompt" or "steer" or "follow_up")
                {
                    var disposition = body.GetProperty("data").GetProperty("disposition").GetString();
                    if (disposition is not ("started" or "queued" or "handled")) throw new InvalidOperationException("Terminal prompt response has no accepted disposition.");
                }
                else if (body.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("cancelled", out var cancellation))
                    success = !cancellation.GetBoolean();
                if (item.Command is "switch_session" or "new_session" or "clone" or "fork" or "pisharp_resume_session")
                {
                    success = !body.GetProperty("data").GetProperty("cancelled").GetBoolean(); replaced = success;
                }
            }
            return Finish(item, success, item.Command!, replaced)?.Applied ?? Task.CompletedTask;
        }
    }

    internal bool TryClaim(out TerminalAcceptedLine? line)
    {
        lock (gate)
        {
            if (!accepted.TryDequeue(out line)) return false;
            retained -= line.Text.Length; return true; // Commit claim before a potentially throwing editor observer; no replay.
        }
    }

    internal async ValueTask WaitForChangeAsync(CancellationToken token, CancellationToken inputOwnerToken = default)
    {
        try { await changed.Reader.WaitToReadAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException canceled) when (token.IsCancellationRequested && canceled.CancellationToken == token)
        {
            // This channel wait invokes no user callback. Snapshot contributors here;
            // later cancellation must not give a different owner authority over this cause.
            throw new TerminalReceiptWaitCanceledException(this, canceled, token, inputOwnerToken,
                inputOwnerToken.IsCancellationRequested);
        }
        while (changed.Reader.TryRead(out _)) { }
    }

    internal void Complete()
    {
        lock (gate)
        {
            if (completed) return;
            completed = true;
            foreach (var item in pending.Values) retained -= item.Line.Text.Length;
            pending.Clear(); correlations.Clear(); changed.Writer.TryComplete();
        }
    }

    private Pending RequirePending(TerminalSubmittedLine line)
    {
        if (line.Identity.InputLifetime != lifetime || !pending.TryGetValue(line.Identity, out var item) || item.Line.Text != line.Text)
            throw new InvalidOperationException("Terminal submission is foreign, stale, or modified.");
        return item;
    }
    private TerminalAcceptedLine? Finish(Pending item, bool acknowledge, string command, bool replaced)
    {
        pending.Remove(item.Line.Identity);
        if (item.RpcId is not null) correlations.Remove(item.RpcId);
        TerminalAcceptedLine? receipt = null;
        if (acknowledge) { receipt = new(item.Line.Identity, item.Line.Text, command, replaced); accepted.Enqueue(receipt); }
        else retained -= item.Line.Text.Length;
        changed.Writer.TryWrite(true);
        return receipt;
    }
}

// Local wait proof only. Callback OCEs do not acquire this classification.
internal sealed class TerminalReceiptWaitCanceledException : OperationCanceledException
{
    internal TerminalReceiptWaitCanceledException(TerminalSubmissionReceipts owner, OperationCanceledException original,
        CancellationToken waitToken, CancellationToken inputOwnerToken, bool inputOwnerRequested)
        : base("Owned terminal receipt wait canceled.", original, waitToken)
    { Owner = owner; Original = original; WaitToken = waitToken; InputOwnerToken = inputOwnerToken;
        InputOwnerRequested = inputOwnerRequested; }
    internal TerminalSubmissionReceipts Owner { get; }
    internal OperationCanceledException Original { get; }
    internal CancellationToken WaitToken { get; }
    internal CancellationToken InputOwnerToken { get; }
    internal bool InputOwnerRequested { get; }
}
