using System.Collections.Immutable;
using PiSharp.Extensions.Abstractions.Reloading;
using PiSharp.Extensions.Runtime.Reloading;

namespace PiSharp.Cli.Reloading;

/// <summary>Explicit host admission for the attached profile. Operations must prepare the complete
/// replacement runtime and commit its host-visible views with the registry. No defaults acquire
/// resources or pretend to implement lifecycle work. Flags are captured before command admission.</summary>
public sealed class NativeHostReloadAdmission
{
    public NativeHostReloadPayload Current { get; }
    public ImmutableDictionary<string, HostReloadFlagValue> Flags { get; }
    public bool HasBindings { get; }
    public ResourceReloadPlan Plan { get; }
    public NativeHostReloadOperations Operations { get; }

    public NativeHostReloadAdmission(NativeHostReloadPayload current,
        IEnumerable<KeyValuePair<string, HostReloadFlagValue>> flags, bool hasBindings,
        ResourceReloadPlan plan, NativeHostReloadOperations operations)
    {
        ArgumentNullException.ThrowIfNull(current); ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(operations); operations.Validate();
        Current = current; Plan = plan; Operations = operations; HasBindings = hasBindings;
        Flags = new HostReloadGeneration<NativeHostReloadPayload>(0, current, flags, [], hasBindings).Flags;
    }
}
