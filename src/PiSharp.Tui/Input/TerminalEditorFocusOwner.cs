namespace PiSharp.Tui.Input;

public enum TerminalEditorFocusFailure { Detached, AlreadyAttached, AdmissionLimit, StaleIdentity }

public sealed class TerminalEditorFocusException(TerminalEditorFocusFailure failure) : InvalidOperationException
{
    public TerminalEditorFocusFailure Failure { get; } = failure;
}

/// <summary>An immutable applied component owner; terminal window focus is independent.</summary>
public readonly record struct TerminalEditorFocusSnapshot(Guid EditorLifetimeId, long Revision, bool EditorOwnsFocus);

/// <summary>One editor attachment, with at most eight admitted focus changes. Detachment preserves ownership.</summary>
public sealed class TerminalEditorFocusOwner(bool initiallyFocused = true)
{
    private readonly object gate = new();
    private readonly HashSet<Request> pending = [];
    private Attachment? active;
    private bool focused = initiallyFocused;
    private long revision;

    public Attachment AttachEditor(Guid editorLifetimeId, Action<Request> admit)
    {
        ArgumentNullException.ThrowIfNull(admit);
        if (editorLifetimeId == Guid.Empty) throw new ArgumentException("An editor lifetime is required.", nameof(editorLifetimeId));
        lock (gate)
        {
            if (active is not null) throw new TerminalEditorFocusException(TerminalEditorFocusFailure.AlreadyAttached);
            if (revision == long.MaxValue) throw new TerminalEditorFocusException(TerminalEditorFocusFailure.StaleIdentity);
            active = new(this, editorLifetimeId, admit); revision++;
            return active;
        }
    }

    public Task SetEditorFocusAsync(bool editorOwnsFocus, CancellationToken token = default)
    {
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            if (active is null) throw new TerminalEditorFocusException(TerminalEditorFocusFailure.Detached);
            if (pending.Count == 8) throw new TerminalEditorFocusException(TerminalEditorFocusFailure.AdmissionLimit);
            var request = new Request(this, active, editorOwnsFocus, token);
            pending.Add(request);
            request.RegisterCancellation();
            try { active.Admit(request); }
            catch (Exception error) { Finish(request, error); }
            return request.Completion;
        }
    }

    public bool IsCurrent(TerminalEditorFocusSnapshot snapshot)
    {
        lock (gate) return active is not null && snapshot == Capture(active);
    }

    private TerminalEditorFocusSnapshot Capture(Attachment attachment)
    {
        if (active != attachment) throw new TerminalEditorFocusException(TerminalEditorFocusFailure.Detached);
        return new(attachment.EditorLifetimeId, revision, focused);
    }

    private void Finish(Request request, Exception? error)
    {
        lock (gate)
        {
            if (!pending.Remove(request)) return;
            request.Registration.Unregister();
            if (error is null) request.Done.TrySetResult();
            else request.Done.TrySetException(error);
        }
    }

    /// <summary>The input consumer owns this attachment until every admitted callback and read has joined.</summary>
    public sealed class Attachment : IDisposable
    {
        private readonly TerminalEditorFocusOwner owner;
        internal readonly Action<Request> Admit;
        public Guid EditorLifetimeId { get; }
        internal Attachment(TerminalEditorFocusOwner owner, Guid lifetime, Action<Request> admit)
        { this.owner = owner; EditorLifetimeId = lifetime; Admit = admit; }
        public TerminalEditorFocusSnapshot Snapshot { get { lock (owner.gate) return owner.Capture(this); } }
        public void Dispose()
        {
            lock (owner.gate)
            {
                if (owner.active != this) return;
                owner.active = null;
                foreach (var request in owner.pending.ToArray())
                    owner.Finish(request, new TerminalEditorFocusException(TerminalEditorFocusFailure.Detached));
            }
        }
    }

    /// <summary>Apply on the serialized editor consumer, then complete only after its admitted paint has joined.</summary>
    public sealed class Request
    {
        private readonly TerminalEditorFocusOwner owner;
        private readonly Attachment attachment;
        private readonly bool focused;
        private readonly CancellationToken token;
        internal readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationTokenRegistration Registration;
        private bool applied;
        public Task Completion => Done.Task;
        internal Request(TerminalEditorFocusOwner owner, Attachment attachment, bool focused, CancellationToken token)
        { this.owner = owner; this.attachment = attachment; this.focused = focused; this.token = token; }
        internal void RegisterCancellation() => Registration = token.Register(() =>
        {
            lock (owner.gate)
                if (!applied && owner.pending.Remove(this)) { Registration.Unregister(); Done.TrySetCanceled(token); }
        });
        public bool TryApply()
        {
            lock (owner.gate)
            {
                if (!owner.pending.Contains(this)) return false;
                if (owner.active != attachment)
                { owner.Finish(this, new TerminalEditorFocusException(TerminalEditorFocusFailure.StaleIdentity)); return false; }
                if (token.IsCancellationRequested)
                { owner.pending.Remove(this); Registration.Unregister(); Done.TrySetCanceled(token); return false; }
                if (applied) throw new TerminalEditorFocusException(TerminalEditorFocusFailure.StaleIdentity);
                if (owner.revision == long.MaxValue)
                { owner.Finish(this, new TerminalEditorFocusException(TerminalEditorFocusFailure.StaleIdentity)); return false; }
                owner.focused = focused; owner.revision++; applied = true; return true;
            }
        }
        public void Complete(Exception? error = null)
        {
            lock (owner.gate)
            {
                if (!applied && error is null) throw new TerminalEditorFocusException(TerminalEditorFocusFailure.StaleIdentity);
                owner.Finish(this, error);
            }
        }
    }
}
