using System.Collections.Immutable;
using PiSharp.Contracts;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Storage;

namespace PiSharp.CodingAgent;

/// <summary>Affine staged manager. It owns no independent file/process/session capability.</summary>
public sealed class SessionCreationSetupWriter
{
    private static readonly AsyncLocal<SessionCreationSetupWriter?> Current = new();
    private readonly object gate = new();
    private readonly Func<SessionSetupEntryDraft, CancellationToken, Task<SessionSetupAppendReceipt>> append;
    private readonly Func<(SessionLogStoreSnapshot Log, SessionContextProjection Context)> read;
    private readonly Action<string?> select;
    private readonly SessionBoundaryOriginals originals;
    private readonly List<TaskCompletionSource<Task>> slots = [];
    private bool closed;
    private Task? settlement;
    internal SessionCreationSetupWriter(Func<SessionSetupEntryDraft, CancellationToken, Task<SessionSetupAppendReceipt>> append,
        Func<(SessionLogStoreSnapshot, SessionContextProjection)> read, Action<string?> select, SessionBoundaryOriginals originals)
    { this.append = append; this.read = read; this.select = select; this.originals = originals; }
    private void Check()
    {
        lock (gate) if (closed || !ReferenceEquals(Current.Value, this))
            throw new InvalidOperationException("Setup manager belongs to its exact live staged callback.");
    }
    public SessionLogStoreSnapshot CaptureLog() { Check(); return read().Log; }
    public SessionContextProjection BuildSessionContext() { Check(); return read().Context; }
    public string GetSessionId() => CaptureLog().Header.Id;
    public string GetCwd() => CaptureLog().Header.WireBody.Value.GetProperty("cwd").GetString()!;
    public string? GetLeafId() => BuildSessionContext().LeafId;
    public PiSharp.Sessions.Serialization.SessionEntry? GetEntry(string id) => CaptureLog().ById.GetValueOrDefault(id);
    public ImmutableArray<PiSharp.Sessions.Serialization.SessionEntry> GetEntries() => CaptureLog().Entries;
    public ImmutableArray<PiSharp.Sessions.Serialization.SessionEntry> GetBranch() => BuildSessionContext().Ancestry;
    public void Branch(string entryId) { ArgumentException.ThrowIfNullOrEmpty(entryId); Check(); select(entryId); }
    public void ResetLeaf() { Check(); select(null); }
    public Task<SessionSetupAppendReceipt> AppendAsync(SessionSetupEntryDraft draft, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var slot = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate) { Check(); if (slots.Count == 128) throw new InvalidOperationException("Setup append bound exceeded."); slots.Add(slot); }
        try { var original = append(draft, token); _ = originals.Capture(original, "setup-append"); slot.SetResult(original); return original; }
        catch (Exception error) { slot.SetException(error); throw; }
    }
    public Task<SessionSetupAppendReceipt> AppendMessageAsync(JsonData message, CancellationToken token = default)
        => AppendAsync(new("message", JsonData.Parse(System.Text.Json.JsonSerializer.Serialize(new { message = message.Value }))), token);
    public Task<SessionSetupAppendReceipt> AppendThinkingLevelChangeAsync(string thinkingLevel, CancellationToken token = default)
        => AppendAsync(new("thinking_level_change", JsonData.Parse(System.Text.Json.JsonSerializer.Serialize(new { thinkingLevel }))), token);
    public Task<SessionSetupAppendReceipt> AppendModelChangeAsync(string provider, string modelId, CancellationToken token = default)
        => AppendAsync(new("model_change", JsonData.Parse(System.Text.Json.JsonSerializer.Serialize(new { provider, modelId }))), token);
    public Task<SessionSetupAppendReceipt> AppendCustomEntryAsync(string customType, JsonData? data = null, CancellationToken token = default)
        => AppendAsync(new("custom", JsonData.Parse(System.Text.Json.JsonSerializer.Serialize(new { customType, data = data?.Value }))), token);
    public Task<SessionSetupAppendReceipt> AppendSessionInfoAsync(string name, CancellationToken token = default)
        => AppendAsync(new("session_info", JsonData.Parse(System.Text.Json.JsonSerializer.Serialize(new { name }))), token);
    public Task<SessionSetupAppendReceipt> AppendLabelChangeAsync(string targetId, string? label, CancellationToken token = default)
        => AppendAsync(new("label", JsonData.Parse(System.Text.Json.JsonSerializer.Serialize(new { targetId, label }))), token);
    public ImmutableArray<SessionBoundaryOriginalEvidence> CaptureOriginals() => originals.Snapshot();
    internal async Task Run(SessionCreationSetupCallback callback, CancellationToken token)
    {
        var previous = Current.Value; Current.Value = this;
        Task? callbackOriginal = null; var errors = new List<Exception>();
        try { callbackOriginal = callback(this, token).AsTask(); await originals.Join(callbackOriginal, "setup-callback").ConfigureAwait(false); }
        catch (Exception error) { SessionBoundaryOriginals.Add(errors, error); }
        finally { Current.Value = previous; }
        try { await Close().ConfigureAwait(false); } catch (Exception error) { SessionBoundaryOriginals.Add(errors, error); }
        SessionBoundaryOriginals.Throw(errors, originals.Snapshot());
    }
    internal Task Close()
    {
        lock (gate) { if (settlement is not null) return settlement; closed = true; return settlement = Settle(slots.ToArray()); }
    }
    private async Task Settle(TaskCompletionSource<Task>[] captured)
    {
        var errors = new List<Exception>();
        foreach (var slot in captured)
            try { await originals.Join(await slot.Task.ConfigureAwait(false), "setup-append").ConfigureAwait(false); }
            catch (Exception error) { SessionBoundaryOriginals.Add(errors, error); }
        try { await originals.JoinAll().ConfigureAwait(false); } catch (Exception error) { SessionBoundaryOriginals.Add(errors, error); }
        SessionBoundaryOriginals.Throw(errors, originals.Snapshot());
    }
}
