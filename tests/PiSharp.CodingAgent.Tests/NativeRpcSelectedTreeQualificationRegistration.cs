using System.Collections.Concurrent;
using PiSharp.CodingAgent;
using PiSharp.Extensions;
using PiSharp.Rpc.Protocol;

// Coordinator installs the supplied probe on the actual native owner's existing
// scope before snapshot capture. The returned profile owns all scope/worker cleanup.
// Shared Program, profile preparation, qualified launches and execution remain external.
internal static class NativeRpcSelectedTreeQualificationRegistration
{
    internal const string Prefix = "native-rpc.selected-tree.";
    internal sealed record OriginalEvidence(string Phase, Task Original, AggregateException? Aggregate, Exception? Direct);
    private static readonly ConcurrentQueue<OriginalEvidence> originals = new();
    internal static OriginalEvidence[] CapturedOriginals => originals.ToArray();
    internal static PiSharp.Rpc.Protocol.RpcSessionTreePublicationException[] CapturedPublicationCarriers =>
        NativeRpcSelectedTreeHostTests.CapturedPublicationCarriers;
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases(
        Func<string, IPiSharpExtension, Task<NativeRpcSelectedTreeHostTests.ActualOwnerConsumer>> prepareFreshOwnerWithProbe,
        Func<Func<SessionTreeNavigationReceipt, string?, ValueTask>, RpcSessionDispatcher> constructActualDispatcherWithPublisher)
    {
        ArgumentNullException.ThrowIfNull(prepareFreshOwnerWithProbe);
        ArgumentNullException.ThrowIfNull(constructActualDispatcherWithPublisher);
        yield return (Prefix + "response-joins-original", () => RunAsync(0, Prefix + "response-joins-original"));
        yield return (Prefix + "retirement-joins-original", () => RunAsync(1, Prefix + "retirement-joins-original"));
        yield return (Prefix + "postcommit-failure-retains-identity", () => RunAsync(2, Prefix + "postcommit-failure-retains-identity"));
        yield return (Prefix + "original-todo-automatic-runner-delivery", () => RunAsync(3, Prefix + "original-todo-automatic-runner-delivery"));
        yield return (Prefix + "multicast-rejected-before-effects", () => Observe(
            NativeRpcSelectedTreeHostTests.MulticastRejectedBeforeEffectsAsync(constructActualDispatcherWithPublisher), Prefix + "multicast.control"));

        Task RunAsync(int mode, string name)
        {
            Task control = mode switch
            {
                0 => NativeRpcSelectedTreeHostTests.SelectedResponseAsync(probe => prepareFreshOwnerWithProbe(name, probe)),
                1 => NativeRpcSelectedTreeHostTests.SelectedRetirementAsync(probe => prepareFreshOwnerWithProbe(name, probe)),
                2 => NativeRpcSelectedTreeHostTests.SelectedPublicationFailureAsync(probe => prepareFreshOwnerWithProbe(name, probe)),
                _ => NativeRpcSelectedTreeHostTests.OriginalTodoAutomaticRpcAsync(probe => prepareFreshOwnerWithProbe(name, probe))
            };
            return Observe(control, name + ".control");
        }
        async Task Observe(Task task, string phase)
        {
            AggregateException? aggregate = null; Exception? direct = null;
            try { await task.ConfigureAwait(false); }
            catch (Exception error) { direct = error; aggregate = task.IsFaulted ? task.Exception : null; throw; }
            finally { originals.Enqueue(new(phase, task, aggregate, direct)); }
        }
    }
}
