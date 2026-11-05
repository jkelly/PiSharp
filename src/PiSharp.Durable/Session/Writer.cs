// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/src/session/writer.ts (PublishFailure, PartSpec, Part, WriterHost,
// Writer, hop). The Session's writer (docs/group-commit.md section 3): commits the mutation line admitted, stored in batches off the line
// and published in line order.
//
// Concurrency. Upstream's writer runs on the Session's one JavaScript thread, so a publication never overlaps a line job. Here a job on the
// line may run on another thread while the writer stores and publishes, so:
// - One short lock (gate) guards the queue, whether a call is in flight, the per-key map of unpublished writers and the newest part; it is
//   never held while Storage, a publication or a part's callbacks run.
// - A flush starts inline in Admit, up to its first incomplete await (upstream's `void this.#flush(parts)`), so a Storage that completes
//   synchronously publishes the part before Admit returns. The flush loop then takes what was admitted meanwhile, as #pump does.
// - Upstream drops a part's keys before its publication runs, as nothing else runs until the publication returns. Here a job touching one of
//   them could then read memory the publication has not adopted yet, so the part's publish callback drops them itself (its unregister
//   argument) once memory has adopted the part, before its observers run; the writer drops them again, idempotently, after the publication.
//   The part's gate opens only after its whole publication, as upstream's.
// - The macrotask between two parts (hop) is a Task.Yield: what the first part's outcome resumes is scheduled before the next part is
//   published, but .NET gives no order between thread-pool work items, so nothing waits for it to run. Tests that rely on upstream's order
//   hold that step through Hop.
// - Exceptions are never null in .NET: upstream's undefined and null rejection and refusal values have no counterpart.
using PiSharp.Durable.Chord;

namespace PiSharp.Durable;

/// <summary>A part's own failure at publication: memory could not adopt it, or one of the Session's own observers threw (upstream
/// <c>PublishFailure</c>).</summary>
internal sealed record PublishFailure(bool Adoption, Exception Error)
{
    /// <summary>Memory could not adopt the part.</summary>
    public static PublishFailure OfAdoption(Exception error) => new(true, error);

    /// <summary>One of the Session's own observers threw; the part stands.</summary>
    public static PublishFailure OfListener(Exception error) => new(false, error);
}

/// <summary>
/// One logical commit the line admitted (upstream <c>PartSpec</c>): stored (<see cref="Writes"/>, never empty), or held in memory only (no
/// writes).
/// </summary>
/// <param name="Index">Line index of the job that admitted it: a failure it causes makes only later jobs' outcomes SessionFailed.</param>
/// <param name="Writes">What it stores; null for a held commit.</param>
/// <param name="Keys">Keys it writes: a read of one waits for its publication.</param>
/// <param name="Context">The commit's context.</param>
/// <param name="Publish">Publishes it in memory, in line order, once it is durable (with its seq) or, held, once the parts before it are. It
/// calls its second argument once memory has adopted it, which drops its keys (see the file header).</param>
/// <param name="Discard">It will never be published: abort what it prepared.</param>
internal sealed record PartSpec(long Index, IReadOnlyList<StorageWrite>? Writes, IReadOnlyList<string> Keys, DurableContext Context,
    Func<Seq?, Action, PublishFailure?> Publish, Action Discard);

/// <summary>What the writer needs of its Session (upstream <c>WriterHost</c>).</summary>
internal interface IWriterHost
{
    /// <summary>Commits the batches through the Session's guarded Storage.</summary>
    Task<CommitResult> CommitAsync(IReadOnlyList<IReadOnlyList<StorageWrite>> batches, Action<int, Seq> receive, DurableContext context);
    /// <summary>Applies a stored batch to the Session's read caches, right before it is published (area publication-caches).</summary>
    void Stored(IReadOnlyList<StorageWrite> writes);
    /// <summary>Runs one part's publication inside the Storage's own scope (<see cref="IStorage.Publishing"/>); null when it ran at once,
    /// else completes once it ran.</summary>
    Task? Publishing(Action publish, DurableContext context);
    /// <summary>The Session's failure, if it failed.</summary>
    Exception? Failure { get; }
    /// <summary>Fails the Session; <paramref name="at"/> is the line index of the first job whose outcome the failure is (null: the job on the
    /// line), and <paramref name="own"/> the jobs after it whose outcome is still their own error (the other parts of a call that rejected).</summary>
    void Fail(Exception error, long? at, IReadOnlyCollection<long> own, DurableContext? context);
    /// <summary>Reports an own observer's throw that cannot fail the Session, as it fails with a storage error.</summary>
    void Report(Exception error, DurableContext context);
}

/// <summary>
/// The writer of one Session (upstream <c>Writer</c>). The line admits each commit as a part once its callback and settlement succeeded; the
/// writer sends every part admitted while no call is in flight as one <see cref="IStorage.CommitAsync"/> call, off the line, and publishes
/// the parts of a call one by one, in line order, with a hop between two. Held commits are parts without writes, published when the parts
/// before them are. Each part registers the keys it writes; until it is published, it is the newest unpublished writer of each, and a job
/// that touches one waits for its gate. See the file header for what stands in for upstream's single thread.
/// </summary>
internal sealed class Writer(IWriterHost host)
{
    private readonly Lock gate = new();
    // Admitted parts not yet sent, in line order. Guarded by gate.
    private List<Part> queue = [];
    private bool flight;
    // Per key, the newest unpublished part that writes it. Guarded by gate.
    private readonly Dictionary<string, Part> unpublished = new(StringComparer.Ordinal);
    // The newest admitted part that is not yet published, whose gate opens after every earlier one's. Guarded by gate.
    private Part? newest;

    /// <summary>Internal, for tests: the wait between two parts of one call, instead of a <see cref="Task.Yield"/>; it gets the part just
    /// published (see the file header).</summary>
    internal Func<Part, Task>? Hop { get; set; }

    /// <summary>Admits a part: registers its keys, queues it, and sends it at once unless a call is in flight.</summary>
    public Part Admit(PartSpec spec)
    {
        var part = new Part(spec);
        bool start;
        lock (gate)
        {
            foreach (var key in spec.Keys) unpublished[key] = part;
            newest = part;
            queue.Add(part);
            start = !flight;
            flight = true;
        }
        if (start) _ = FlushAsync();
        return part;
    }

    /// <summary>The gate of the newest unpublished writer of <paramref name="key"/>, or null when it has none.</summary>
    public Task? Gate(string key)
    {
        lock (gate) return unpublished.TryGetValue(key, out var part) ? part.Opened : null;
    }

    /// <summary>Opens once every part admitted so far is published or never will be; null when none is pending.</summary>
    public Task? Drained()
    {
        lock (gate) return newest?.Opened;
    }

    /// <summary>Keys with an unpublished writer, for tests that bound memory.</summary>
    public int Unpublished
    {
        get { lock (gate) return unpublished.Count; }
    }

    /// <summary>
    /// Sends and publishes what is queued, then what was admitted meanwhile, until nothing is. Never faults: a throw the writer did not expect
    /// fails the Session and settles the parts (<see cref="Abandon"/>), so no outcome or gate is stranded.
    /// </summary>
    private async Task FlushAsync()
    {
        while (true)
        {
            List<Part> parts;
            lock (gate)
            {
                if (queue.Count == 0)
                {
                    flight = false;
                    return;
                }
                parts = queue;
                queue = [];
            }
            try
            {
                await SendAsync(parts).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                Abandon(parts, error);
            }
        }
    }

    /// <summary>Stores <paramref name="parts"/> in one call, then publishes them in order; settles every one of them.</summary>
    private async Task SendAsync(List<Part> parts)
    {
        var stored = parts.Where(part => part.Spec.Writes is not null).ToList();
        var seqs = new Seq?[stored.Count];
        CommitResult? result = null;
        Exception? rejected = null;
        if (host.Failure is { } failed)
        {
            foreach (var part in parts) Unpublish(part, new SessionFailedException(failed));
            return;
        }
        if (stored.Count > 0)
        {
            try
            {
                result = await host.CommitAsync([.. stored.Select(part => part.Spec.Writes!)], (index, seq) => seqs[index] = seq,
                    stored[0].Spec.Context.WithoutCancellation()).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                rejected = error;
            }
        }
        // A call that settles once another failure failed the Session reports SessionFailed with that first error, whatever it returned
        // (spec 10.1), and publishes nothing.
        if (host.Failure is { } meanwhile)
        {
            foreach (var part in parts) Unpublish(part, new SessionFailedException(meanwhile));
            return;
        }
        await PublishAsync(parts, seqs, result, rejected).ConfigureAwait(false);
    }

    /// <summary>Why the publication of a call stopped: the error, the line index whose outcome it is, and whether it failed the Session from
    /// outside the call.</summary>
    private sealed record Stop(Exception Error, long At, bool External, DurableContext Context);

    /// <summary>
    /// Publishes the received parts and held ones in order, a hop apart, and settles every part (group commit 3.4). A refusal, a rejection, or
    /// a part whose adoption or own observer throws fails the Session once the parts before it are published; the parts after it are not
    /// published, and fault with SessionFailed. What the failure decides (the outcomes, and the gates of the parts it concerns) is settled
    /// only once the Session failed, so no job waiting at such a gate, and no later outcome, goes on as if it had not.
    /// </summary>
    private async Task PublishAsync(List<Part> parts, Seq?[] seqs, CommitResult? result, Exception? rejected)
    {
        Stop? stop = null;
        // Settled once the Session failed: each part, its own error (null: SessionFailed), and whether it was published.
        var failing = new List<(Part Part, Exception? Own, bool Published)>();
        var storedIndex = 0;
        var published = 0;
        Part? previous = null;
        foreach (var part in parts)
        {
            int? index = part.Spec.Writes is null ? null : storedIndex++;
            if (stop is not null)
            {
                // A part after a rejected call's last receipt has the call's error; any other part after a stop was not sent, nor any part
                // once something else failed the Session.
                failing.Add((part, rejected is not null && index is not null && !stop.External ? rejected : null, false));
                continue;
            }
            var seq = index is { } at ? seqs[at] : null;
            if (index is { } position && seq is null)
            {
                var error = rejected ??
                    (result is { } committed && position == committed.Stored && committed.Refused is { } refused
                        ? refused
                        : new InvalidOperationException("Storage committed without adopting the batch"));
                failing.Add((part, error, false));
                stop = new Stop(error, part.Spec.Index, false, part.Spec.Context);
                continue;
            }
            // A hop between two parts: what the first resolves is scheduled before the second is published.
            if (published++ > 0) await HopAsync(previous!).ConfigureAwait(false);
            previous = part;
            PublishFailure? failure = null;
            // Failed by something else meanwhile, during the hop or before a storage's scope runs the publication: nothing more of the call
            // is published. Checked right before it would be, after every wait.
            Exception? external = null;
            var publication = host.Publishing(() =>
            {
                external = host.Failure;
                if (external is not null) return;
                // A throw anywhere in it, the read caches included, leaves memory behind what is stored: like an adoption's.
                try
                {
                    if (part.Spec.Writes is not null) host.Stored(part.Spec.Writes);
                    failure = part.Spec.Publish(seq, () => Unregister(part));
                }
                catch (Exception error)
                {
                    failure = PublishFailure.OfAdoption(error);
                }
                Unregister(part);
            }, part.Spec.Context);
            if (publication is not null) await publication.ConfigureAwait(false);
            if (external is not null)
            {
                stop = new Stop(external, part.Spec.Index, true, part.Spec.Context);
                failing.Add((part, null, false));
                continue;
            }
            if (failure is { Adoption: true })
            {
                failing.Add((part, failure.Error, true));
                stop = new Stop(failure.Error, part.Spec.Index, false, part.Spec.Context);
                continue;
            }
            // A part published while its call rejected is durable but faults (spec 10.1); an own observer's throw is then reported, as the
            // Session fails with the storage error.
            if (rejected is not null)
            {
                if (failure is not null) host.Report(failure.Error, part.Spec.Context);
                failing.Add((part, rejected, true));
                continue;
            }
            // An own observer's throw: the commit stands; the Session fails after it.
            if (failure is not null) stop = new Stop(failure.Error, part.Spec.Index, false, part.Spec.Context);
            if (host.Failure is { } failedMeanwhile && failure is null) part.Reject(new SessionFailedException(failedMeanwhile));
            else part.Resolve();
            Open(part);
        }
        if (rejected is not null || stop is not null)
        {
            // Every part of the call that rejected keeps the storage error; the first of them is the failure's boundary.
            var own = failing.Where(entry => entry.Own is not null).Select(entry => entry.Part.Spec.Index).ToList();
            if (rejected is not null)
                host.Fail(rejected, own.Count > 0 ? own[0] : null, own, failing.Count > 0 ? failing[0].Part.Spec.Context : null);
            else host.Fail(stop!.Error, stop.At, own, stop.Context);
        }
        foreach (var (part, own, wasPublished) in failing)
        {
            var error = own ?? Failed(stop!.Error);
            if (wasPublished)
            {
                part.Reject(error);
                Open(part);
            }
            else Unpublish(part, error);
        }
        // Admitted while this call ran, after the failure: never sent.
        if (host.Failure is { } failure2)
        {
            List<Part> queued;
            lock (gate)
            {
                queued = queue;
                queue = [];
            }
            foreach (var part in queued) Unpublish(part, new SessionFailedException(failure2));
        }
    }

    private Task HopAsync(Part published) => Hop is { } hop ? hop(published) : YieldAsync();

    private static async Task YieldAsync() => await Task.Yield();

    /// <summary>
    /// A throw the writer itself did not expect while sending or publishing <paramref name="parts"/> (a bug): fails the Session at the first
    /// of them and settles every one not settled yet, so no outcome, gate, or close waits on it forever.
    /// </summary>
    private void Abandon(List<Part> parts, Exception error)
    {
        host.Fail(error, parts[0].Spec.Index, [], parts[0].Spec.Context);
        foreach (var part in parts)
        {
            if (!part.Done) Unpublish(part, Failed(error));
            Unregister(part);
            Open(part);
        }
    }

    /// <summary>SessionFailed with the Session's failure, or with <paramref name="error"/> until the Session records it.</summary>
    private SessionFailedException Failed(Exception error) => new(host.Failure ?? error);

    /// <summary>Settles a part that will never be published: discards it, faults it with <paramref name="error"/>, and opens its gate.</summary>
    private void Unpublish(Part part, Exception error)
    {
        try { part.Spec.Discard(); }
        finally
        {
            part.Reject(error);
            Unregister(part);
            Open(part);
        }
    }

    /// <summary>Drops the keys a part is still the newest writer of: it is published, or never will be.</summary>
    private void Unregister(Part part)
    {
        lock (gate)
            foreach (var key in part.Spec.Keys)
                if (unpublished.TryGetValue(key, out var writer) && ReferenceEquals(writer, part)) unpublished.Remove(key);
    }

    /// <summary>Opens a part's gate: the jobs waiting for it go on. The newest part's opens <see cref="Drained"/> too.</summary>
    private void Open(Part part)
    {
        lock (gate)
            if (ReferenceEquals(newest, part)) newest = null;
        part.OpenGate();
    }

    /// <summary>An admitted part and its outcome (upstream <c>Part</c>).</summary>
    internal sealed class Part
    {
        private readonly TaskCompletionSource outcome = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int done;

        public Part(PartSpec spec)
        {
            Spec = spec;
            // Always observed, so a part nobody awaits faults silently.
            _ = outcome.Task.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        public PartSpec Spec { get; }

        /// <summary>Completes once it is published; faults once it never will be, and also after publication when its storage call rejected or
        /// the Session failed meanwhile.</summary>
        public Task Outcome => outcome.Task;

        /// <summary>Opens once it is published or never will be; never faults.</summary>
        public Task Opened => opened.Task;

        /// <summary>Whether its outcome is settled.</summary>
        public bool Done => Volatile.Read(ref done) != 0;

        public void Resolve()
        {
            Volatile.Write(ref done, 1);
            outcome.TrySetResult();
        }

        public void Reject(Exception error)
        {
            Volatile.Write(ref done, 1);
            outcome.TrySetException(error);
        }

        public void OpenGate() => opened.TrySetResult();
    }
}
