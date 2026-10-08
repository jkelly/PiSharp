using PiSharp.Extensions.Mcp.Authentication;
namespace PiSharp.McpOAuthHost.Tests;
public static partial class RefreshClientAuthenticationControls
{
    private sealed class Inventory
    {
        internal readonly List<Exception> Failures = [];
        private readonly HashSet<Task> joined = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<Task, AggregateException?> aggregates = new(ReferenceEqualityComparer.Instance);
        internal AggregateException? CaptureAggregate(Task original)
        { if (!aggregates.TryGetValue(original, out var aggregate)) { aggregate = original.Exception; aggregates.Add(original, aggregate); } return aggregate; }
        internal readonly List<(Task? Task, AggregateException? Aggregate, Exception? Direct)> Originals = [];
        internal async Task JoinVoid(Func<Task> invoke)
        {
            Task? original = null;
            try { original = invoke(); await original; joined.Add(original); Retain(original, null, null); }
            catch (Exception direct) { Record(original, direct); Failures.Add(direct); }
        }
        internal void Refused(Func<Task> invoke, Action<Task> retainReturned)
        {
            Task? original = null;
            try
            {
                original = invoke(); retainReturned(original);
                if (!original.IsCompleted) throw new IOException("Ancestor refusal returned pending work; do not self-join it inside cancellation callback.");
                original.GetAwaiter().GetResult(); joined.Add(original); Retain(original, null, null);
                throw new IOException("Expected ancestor refusal absent.");
            }
            catch (InvalidOperationException direct) when (direct.Message.Contains("OAuth", StringComparison.Ordinal) && original is { IsFaulted: true })
            {
                var aggregate = CaptureAggregate(original); joined.Add(original); Retain(original, aggregate, direct);
                if (aggregate is not { InnerExceptions.Count: 1 } || !ReferenceEquals(aggregate.InnerExceptions[0], direct))
                    throw new AggregateException("Refusal has unexpected original siblings.", (Exception?)aggregate ?? direct, direct);
            }
        }
        internal void JoinBlocking(Task original)
        {
            try { original.GetAwaiter().GetResult(); joined.Add(original); Retain(original, null, null); }
            catch (Exception direct) { Record(original, direct); Failures.Add(direct); throw; }
        }
        private void Retain(Task? original, AggregateException? aggregate, Exception? direct)
        { Originals.Add((original, aggregate, direct)); feed.Value?.Add(new(original, aggregate, direct)); }
        internal async Task<T> Join<T>(Func<Task<T>> invoke)
        {
            Task<T>? original = null;
            try { original = invoke(); var result = await original; joined.Add(original); Retain(original, null, null); return result; }
            catch (Exception direct) { Record(original, direct); throw; }
        }
        internal async Task Expected(Func<Task> invoke, Func<Exception, bool> accept)
        {
            Task? original = null;
            try { original = invoke(); await original; joined.Add(original); Retain(original, null, null); Failures.Add(new InvalidOperationException("Expected failure absent.")); }
            catch (Exception direct)
            {
                var aggregate = original is null ? null : CaptureAggregate(original); if (original is not null) joined.Add(original); Retain(original, aggregate, direct);
                bool accepted = false; try { accepted = accept(direct); } catch (Exception predicate) { Failures.Add(predicate); }
                if (!accepted) { if (aggregate is not null) Failures.Add(aggregate); Failures.Add(direct); }
            }
        }
        private void Record(Task? original, Exception direct)
        { if (original is not null) joined.Add(original); var aggregate = original is null ? null : CaptureAggregate(original); Retain(original, aggregate, direct); if (aggregate is not null) Failures.Add(aggregate); }
        internal async Task Cleanup(Func<Task> invoke)
        {
            Task? original = null;
            try { original = invoke(); if (joined.Contains(original)) return; await original; joined.Add(original); Retain(original, null, null); }
            catch (Exception direct) { Record(original, direct); Failures.Add(direct); }
        }
        internal async Task JoinSignal(Task signal, Task operation)
        {
            using var diagnostic = new CancellationTokenSource();
            var deadline = Task.Delay(TimeSpan.FromSeconds(10), diagnostic.Token);
            try
            {
                var winner = await Task.WhenAny(signal, operation, deadline);
                if (winner == deadline) throw new TimeoutException("Diagnostic held signal deadline; caller finally releases and joins actual originals.");
                if (winner != signal) { await operation; throw new InvalidOperationException("Operation settled before required held signal."); }
                await signal;
            }
            finally { diagnostic.Cancel(); try { await deadline; } catch (OperationCanceledException) when (deadline.IsCanceled) { } }
        }
        internal void ThrowIfFailed()
        { if (Failures.Count != 0) throw new AggregateException("OAuth refresh fixture original/cleanup inventory.", Failures); }
    }
}
