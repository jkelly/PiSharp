// Pi fb0fe823539f6d04850f6e34748c01f01f212e1f (MIT): packages/durable/src/harness/parks.ts. Tool parks: what `Execute` returns instead of a
// result to sleep or wait, its shape check, and how the Harness tells a base tool's parks from a wrapper's.
//
// Mappings: upstream's `execute()` returns `ToolExecutionResult | ToolPark`; here a ToolPark derives from ToolExecutionResult (Harness/Types.cs), so
// IsPark tests that type, and "a park has no result field" checks its inherited fields. Upstream carries the parks a wrapped base
// returned in its run's api under a symbol key, which a wrapper's spread passes on; here the run's own api (ToolExecutionApi) holds the set
// and a wrapper's DelegatingToolExecutionApi is followed to it (CarrierOf). An api a wrapper builds from scratch carries none, as an api
// object upstream builds without spreading does, and the composite's park is then invalid. Parks are told apart by reference, as upstream's
// Set of park objects does; upstream's WeakSet of composites is a ConditionalWeakTable.
using System.Runtime.CompilerServices;
using PiSharp.Durable.Chord;

namespace PiSharp.Durable;

/// <summary>Tool parks (upstream <c>parks.ts</c>).</summary>
internal static class Parks
{
    /// <summary>Composite tools a wrapper built; their parks must come from their base (upstream <c>composites</c>).</summary>
    private static readonly ConditionalWeakTable<ToolRegistration, object> Composites = [];

    /// <summary>Whether <c>Execute</c> returned a park rather than a result (upstream <c>isPark</c>).</summary>
    public static bool IsPark(ToolExecutionResult returned) => returned is ToolPark;

    /// <summary>The set of parks the wrapped base returned in the run <paramref name="api"/> belongs to: the run's own api, reached through
    /// any <see cref="DelegatingToolExecutionApi"/> a wrapper put around it; null for an api a wrapper built itself.</summary>
    public static HashSet<ToolPark>? CarrierOf(IToolExecutionApi api)
    {
        var current = api;
        while (current is DelegatingToolExecutionApi wrapped) current = wrapped.InnerApi;
        return (current as ToolExecutionApi)?.BaseParks;
    }

    /// <summary>
    /// Why a park breaks its shape (spec suspendable §4.1), or null (upstream <c>parkError</c>): a <c>sleep</c> or <c>wait</c> tag, no result field, and, when <paramref name="tool"/> is a wrapper's composite (§9), one its base returned in this run,
    /// whose api is <paramref name="api"/>. Rules on what it waits for are the commit's.
    /// </summary>
    public static string? ParkError(ToolRegistration tool, ToolExecutionResult returned, IToolExecutionApi api)
    {
        var park = (ToolPark)returned;
        // Tool code is a trust boundary for the mixed objects upstream's types forbid.
        if (park.Kind is not ("sleep" or "wait")) return $"a park's kind is sleep or wait, not {park.Kind}";
        var field = returned.Output is not null ? "output"
            : returned.StructuredOutput is not null ? "structuredOutput"
            : returned.IsError is not null ? "isError"
            : returned.Details is not null ? "details"
            : returned.Diagnostics is not null ? "diagnostics"
            : returned.Usage is not null ? "usage"
            : returned.Control is not null ? "control"
            : null;
        if (field is not null) return $"a park has no result field, but this one has {field}";
        if (Composites.TryGetValue(tool, out _) && CarrierOf(api)?.Contains(park) != true) return "a wrapper returned a park its base did not return";
        return null;
    }

    /// <summary>
    /// Applies <paramref name="wrap"/> to <paramref name="tool"/> as tool wrappers apply (spec suspendable §9; upstream
    /// <c>wrapComposite</c>): the first wrapper of a base gets a copy whose <c>Execute</c> records every park it returns in its run's api,
    /// so the composite's own parks are told apart.
    /// </summary>
    public static ToolRegistration WrapComposite(ToolRegistration tool, Func<ToolRegistration, ToolRegistration> wrap)
    {
        var inner = Composites.TryGetValue(tool, out _)
            ? tool
            : tool with
            {
                Execute = async (args, api, context) =>
                {
                    var returned = await tool.Execute(args, api, context).ConfigureAwait(false);
                    if (returned is ToolPark park && CarrierOf(api) is { } parks)
                        lock (parks) parks.Add(park);
                    return returned!;
                },
            };
        var composite = wrap(inner);
        if (composite is not null) Composites.AddOrUpdate(composite, Composites);
        return composite!;
    }
}
