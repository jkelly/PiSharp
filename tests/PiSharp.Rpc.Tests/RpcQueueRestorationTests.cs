using System.Reflection;
using System.Text;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Rpc.Protocol;
using Fixture = RpcSessionCreationTests.Fixture;

// Authored only. Original operations are directly awaited; deadlines observe gates only.
internal static class RpcQueueRestorationTests
{
    internal const string Prefix = "rpc.queue-restore.";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        (Prefix + "fifo-duplicates-draft-and-repeated-action", Fifo),
        (Prefix + "capacity-response-budget-and-nontext-reject-before-removal", Capacity),
        (Prefix + "preflight-concurrent-growth-and-ABA-never-fall-back", Concurrent),
        (Prefix + "competing-plans-have-one-winner", Competing),
        (Prefix + "precommit-cancellation-and-postcommit-output-join", Cancellation),
        (Prefix + "stale-generation-during-replacement-retains-new-queue", Switch)
    ];

    private static async Task Fifo()
    {
        await using var f = await Fixture.Create([]);
        var before = await ReadBytes(f.Source);
        await f.Send(new { id = "a", type = "steer", message = "same" });
        await f.Send(new { id = "b", type = "steer", message = "same" });
        await f.Send(new { id = "c", type = "follow_up", message = "line\n\u6587" });
        await f.Send(new { id = "missing-generation", type = "pisharp_restore_queue", currentText = "" });
        Rejected(f, "missing-generation");
        await f.Send(Request("take", 1, "draft"));
        var response = f.Response("take").Value;
        Check(response.GetProperty("success").GetBoolean(), "Restoration was rejected.");
        var data = response.GetProperty("data");
        Check(data.GetProperty("text").GetString() == "same\n\nsame\n\nline\n\u6587\n\ndraft" &&
            data.GetProperty("count").GetInt32() == 3 && data.GetProperty("generation").GetInt64() == 1, "FIFO canonical text or draft changed.");
        var records = f.Records; var at = Array.FindIndex(records, r => r.Value.TryGetProperty("id", out var id) && id.GetString() == "take");
        Check(at > 0 && records[at - 1].Value.GetProperty("type").GetString() == "queue_update" &&
            records[at - 1].Value.GetProperty("steering").GetArrayLength() == 0, "Removal response preceded its queue event.");
        await f.Send(Request("again", 1, data.GetProperty("text").GetString()!));
        Check(f.Response("again").Value.GetProperty("data").GetProperty("count").GetInt32() == 0, "Repeated action restored removed messages again.");
        Check((await ReadBytes(f.Source)).SequenceEqual(before), "Queue restoration rewrote durable history.");
    }

    private static async Task Capacity()
    {
        await using (var f = await Fixture.Create([]))
        {
            var session = f.Owner.Current.Session;
            session.Steer(Input(new string('a', 40_000))); session.FollowUp(Input(new string('b', 40_000)));
            await f.Send(Request("large", 1, ""));
            Rejected(f, "large"); Check(session.GetPendingInputQueueSnapshot().SteeringMessages.Length == 1 &&
                session.GetPendingInputQueueSnapshot().FollowUpMessages.Length == 1, "Oversized queue was cleared.");
            session.ClearPendingInputQueues(); session.Steer(Input(new string('a', 40_000)));
            await f.Send(Request("combined", 1, new string('b', 30_000))); Rejected(f, "combined");
            Check(session.GetPendingInputQueueSnapshot().SteeringMessages.Length == 1, "Oversized combined draft was removed.");
            session.ClearPendingInputQueues();
            session.Steer(new("user", JsonData.Parse("""{"role":"user","content":[{"type":"text","text":"text"},{"type":"image","data":"AA==","mimeType":"image/png"}],"timestamp":0}""")));
            await f.Send(Request("image", 1, "")); Rejected(f, "image");
            Check(session.GetPendingInputQueueSnapshot().SteeringMessages.Length == 1, "Unsupported image was silently lost.");
            session.ClearPendingInputQueues();
            session.Steer(Input(new string('a', 32_767))); session.FollowUp(Input(new string('b', 32_767)));
            await f.Send(Request("boundary", 1, ""));
            Check(f.Response("boundary").Value.GetProperty("success").GetBoolean() &&
                f.Response("boundary").Value.GetProperty("data").GetProperty("text").GetString()!.Length == 65_536,
                "The exact editor capacity boundary was rejected or truncated.");
        }
        await using (var f = await Fixture.Create([], new(MaximumOutputBytes: 512)))
        {
            f.Owner.Current.Session.Steer(Input(new string('x', 1024)));
            await f.Send(Request("wire", 1, "")); Rejected(f, "wire");
            Check(f.Owner.Current.Session.GetPendingInputQueueSnapshot().SteeringMessages.Length == 1, "Response-budget failure followed a destructive take.");
        }
    }

    private static async Task Concurrent()
    {
        await using var f = await Fixture.Create([]); var session = f.Owner.Current.Session;
        var first = Input(new string('a', 40_000)); session.Steer(first);
        var plan = Plan(f);
        var release = Gate(); var producer = Task.Run(async () => { await release.Task; session.FollowUp(Input(new string('b', 40_000))); });
        try
        {
            release.TrySetResult(); await producer;
            Check(!plan.TryCommit(session, out var removed, default) && removed is null, "Stale preflight removed concurrent growth.");
            var rejected = false;
            try { _ = Plan(f); } catch (RpcCommandException) { rejected = true; }
            Check(rejected && session.GetPendingInputQueueSnapshot().FollowUpMessages.Length == 1, "Fresh preflight bypassed capacity.");
            session.ClearPendingInputQueues(); session.Steer(first); plan = Plan(f);
            session.ClearPendingInputQueues(); session.Steer(first);
            Check(!plan.TryCommit(session, out removed, default) && removed is null, "ABA reused stale restoration authority.");
        }
        finally { release.TrySetResult(); await producer; }
    }

    private static async Task Competing()
    {
        await using var f = await Fixture.Create([]); f.Owner.Current.Session.Steer(Input("once"));
        var first = Plan(f); var second = Plan(f); var release = Gate();
        var a = Task.Run(async () => { await release.Task; return first.TryCommit(f.Owner.Current.Session, out _, default); });
        var b = Task.Run(async () => { await release.Task; return second.TryCommit(f.Owner.Current.Session, out _, default); });
        try { release.TrySetResult(); Check((await Task.WhenAll(a, b)).Count(won => won) == 1, "Competing plans did not have exactly one winner."); }
        finally { release.TrySetResult(); await Task.WhenAll(a, b); }
    }

    private static async Task Cancellation()
    {
        await using (var f = await Fixture.Create([]))
        {
            f.Owner.Current.Session.Steer(Input("kept"));
            var gate = Publication(f); await gate.WaitAsync();
            using var token = new CancellationTokenSource(); Task? original = null;
            try
            {
                original = f.Dispatcher.SubmitAsync(JsonData.Parse(JsonSerializer.Serialize(Request("cancel", 1, ""))), token.Token);
                token.Cancel(); await original; Rejected(f, "cancel");
                Check(f.Owner.Current.Session.GetPendingInputQueueSnapshot().SteeringMessages.Length == 1, "Precommit cancellation removed input.");
            }
            finally { gate.Release(); if (original is not null) await original; }
        }
        using var output = new HeldClear();
        await using (var f = await Fixture.Create([], output: output))
        {
            f.Owner.Current.Session.Steer(Input("owned")); using var token = new CancellationTokenSource();
            var original = f.Dispatcher.SubmitAsync(JsonData.Parse(JsonSerializer.Serialize(Request("committed", 1, ""))), token.Token);
            Task? nextAdmission = null;
            try
            {
                await output.Entered.Task.WaitAsync(Deadline); token.Cancel();
                Check(f.Owner.Current.Session.GetPendingInputQueueSnapshot().SteeringMessages.IsEmpty && !original.IsCompleted,
                    "Committed take detached its original output.");
                nextAdmission = f.Send(new { id = "next", type = "steer", message = "after take" });
                Check(f.Owner.Current.Session.GetPendingInputQueueSnapshot().SteeringMessages.Length == 1 && !nextAdmission.IsCompleted,
                    "Held restoration output blocked admission or let its later publication escape.");
            }
            finally { output.Release.TrySetResult(); await Task.WhenAll(new[] { original, nextAdmission }.OfType<Task>()); }
            Check(f.Response("committed").Value.GetProperty("success").GetBoolean(), "Postcommit cancellation lost the authoritative response.");
        }
    }

    private static async Task Switch()
    {
        await using var f = await Fixture.Create([]); var publication = Publication(f); await publication.WaitAsync();
        var entered = Gate(); var originalCallback = f.Owner.AttachmentChanged!;
        f.Owner.AttachmentChanged = async change =>
        {
            change.Current.Session.Steer(Input("new generation"));
            var callback = originalCallback(change).AsTask(); entered.TrySetResult(); await callback;
        };
        Task? take = null, replacement = null;
        try
        {
            take = f.Send(Request("stale", 1, "")); replacement = f.Send(new { id = "switch", type = "new_session" });
            await entered.Task.WaitAsync(Deadline);
        }
        finally { publication.Release(); await Task.WhenAll(new[] { take, replacement }.OfType<Task>()); }
        Rejected(f, "stale");
        Check(f.Owner.Current.Session.GetPendingInputQueueSnapshot().SteeringMessages.Length == 1, "Stale request consumed a new generation queue.");
        await f.Send(Request("current", 2, ""));
        Check(f.Response("current").Value.GetProperty("data").GetProperty("text").GetString() == "new generation", "Current generation could not restore.");
    }

    private static object Request(string id, long generation, string currentText) => new { id, type = "pisharp_restore_queue", generation, currentText };
    private static async Task<byte[]> ReadBytes(string path)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var copy = new MemoryStream(); await file.CopyToAsync(copy); return copy.ToArray();
    }
    private static TranscriptEntry Input(string text) => new("user", JsonData.Parse(JsonSerializer.Serialize(new { role = "user", content = text, timestamp = 0 })));
    private static RpcQueueRestorationPlan Plan(Fixture f) => RpcQueueRestorationPlan.Capture(f.Owner.Current.Session,
        RpcCommandCodec.Decode(JsonData.Parse(JsonSerializer.Serialize(Request("plan", 1, ""))), new()), 1, new(), default);
    private static SemaphoreSlim Publication(Fixture f) => (SemaphoreSlim)typeof(RpcSessionDispatcher)
        .GetField("_queuePublication", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(f.Dispatcher)!;
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Rejected(Fixture f, string id) => Check(!f.Response(id).Value.GetProperty("success").GetBoolean(), "Expected rejection: " + id);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class HeldClear : MemoryStream
    {
        internal readonly TaskCompletionSource Entered = Gate(), Release = Gate();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            var record = JsonData.Parse(Encoding.UTF8.GetString(buffer.Span));
            if (record.Value.GetProperty("type").GetString() == "queue_update")
            { Entered.TrySetResult(); await Release.Task; }
            await base.WriteAsync(buffer, token);
        }
    }
}
