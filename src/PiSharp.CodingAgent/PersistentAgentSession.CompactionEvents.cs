using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Sessions.Compaction;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Serialization;

namespace PiSharp.CodingAgent;

/// <summary>Actual admitted compaction, independent of native recovery notifications.</summary>
public sealed record SessionCompactionStarted(long OperationGeneration, SessionCompactionReason Reason)
    : SessionOperationEvent(OperationGeneration);
/// <summary>Actual prospective checkpoint and rebuilt context; observers may validate output before append.</summary>
public sealed record SessionCompactionPrepared(long OperationGeneration, SessionCompactionReason Reason,
    JsonData Result, bool WillRetry) : SessionOperationEvent(OperationGeneration);
/// <summary>Original compaction terminal, published after owned generator/checkpoint/cancellation joins.</summary>
public sealed record SessionCompactionEnded(long OperationGeneration, SessionCompactionReason Reason,
    JsonData? Result, bool Aborted, bool WillRetry, string? ErrorMessage)
    : SessionOperationEvent(OperationGeneration);

public sealed partial class PersistentAgentSession
{
    private static JsonData CompactionResult(SessionEntry entry, SessionContextProjection context)
    {
        var body = entry.WireBody.Value; var after = context.Messages.Sum(SessionCompactionTokenEstimator.EstimateTokens);
        if (!double.IsFinite(after) || after < 0) throw new SessionCompactionException(SessionCompactionFailure.UnsupportedNumber);
        var boundary = body.GetProperty("firstKeptEntryId");
        if (boundary.ValueKind != JsonValueKind.String) throw new SessionCompactionException(SessionCompactionFailure.InvalidBoundary);
        // All values come from the actual prospective/acknowledged checkpoint and selected rebuilt projection.
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes))
        {
            writer.WriteStartObject();
            writer.WriteString("summary", body.GetProperty("summary").GetString());
            writer.WriteString("firstKeptEntryId", boundary.GetString());
            writer.WriteNumber("tokensBefore", body.GetProperty("tokensBefore").GetDouble());
            writer.WriteNumber("estimatedTokensAfter", after);
            if (body.TryGetProperty("usage", out var usage)) { writer.WritePropertyName("usage"); usage.WriteTo(writer); }
            if (body.TryGetProperty("details", out var details)) { writer.WritePropertyName("details"); details.WriteTo(writer); }
            writer.WriteEndObject();
        }
        return JsonData.Parse(System.Text.Encoding.UTF8.GetString(bytes.ToArray()));
    }

    private static async ValueTask EmitCompactionAsync(ImmutableArray<OperationSubscription> subscriptions,
        SessionOperationEvent observation)
    {
        var failures = new List<Exception>();
        foreach (var subscription in subscriptions)
        {
            Task? original = null;
            try
            {
                // Convert this original ValueTask exactly once. Await selects only one exception from a
                // faulted Task.WhenAll/TCS; the joined task retains every sibling in Task.Exception.
                original = subscription.Sink.EmitAsync(observation, CancellationToken.None).AsTask();
                await original.ConfigureAwait(false);
            }
            catch (Exception error)
            {
                // Preserve each observer failure as one ordinary boundary. Never flatten a source fault
                // inventory: nested, duplicate and empty aggregates are meaningful original data, and
                // flattening/deduplication can turn faulted OCEs into cancellation or erase an empty fault.
                // Only an actually canceled original task establishes observer cancellation provenance.
                if (original is { IsCanceled: true }) AddDistinctFailure(failures, error);
                else failures.Add(new CompactionObserverTaskFault(original is { IsFaulted: true } ? original.Exception! : error));
            }
        }
        if (failures.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException("Compaction observers failed.", failures);
    }
    private sealed class CompactionObserverTaskFault(Exception original)
        : Exception("Compaction observer failed: " + original.Message, original) { }
}
