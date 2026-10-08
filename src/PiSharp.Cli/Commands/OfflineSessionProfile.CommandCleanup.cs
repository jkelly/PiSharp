using System.Runtime.ExceptionServices;
using PiSharp.Cli.Extensions;
using PiSharp.CodingAgent;

namespace PiSharp.Cli.Commands;

internal sealed partial class OfflineSessionProfile
{
    internal async Task SettleOwnedCommandAsync(PersistentAgentSession? session, Exception? primary)
    {
        var failures = new List<Exception>();
        void Add(Exception error)
        {
            if (error is AggregateException aggregate)
            { foreach (var inner in aggregate.InnerExceptions) Add(inner); return; }
            if (error is NativeExtensionException { Failure: NativeExtensionFailure.CleanupFailed, InnerException: { } cleanup })
            { Add(cleanup); return; }
            if (!failures.Any(retained => ReferenceEquals(retained, error))) failures.Add(error);
        }
        if (primary is not null) Add(primary);
        try
        {
            if (Sessions is { } owner) await owner.DisposeAsync().ConfigureAwait(false);
            else if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception error) { Add(error); }
        try { await DisposeAsync().ConfigureAwait(false); } catch (Exception error) { Add(error); }
        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException("Command operation and owned cleanup failed.", failures);
    }
}
