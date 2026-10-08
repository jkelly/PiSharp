using PiSharp.Agent;
using PiSharp.Contracts;

namespace PiSharp.CodingAgent;

public sealed partial class SessionRuntimeRegistry
{
    /// <summary>Checks executable provenance without callbacks: exact declaration object and exact
    /// leaf adapter identity through only this registry's private immutable naming wrappers.
    /// Equal schema text, names or an arbitrary forwarding adapter do not establish ownership.</summary>
    public bool UsesCapturedToolBinding(string name, JsonData exactDeclaration, IPreparedToolAdapter actualLeafAdapter)
    {
        ArgumentNullException.ThrowIfNull(name); ArgumentNullException.ThrowIfNull(exactDeclaration);
        ArgumentNullException.ThrowIfNull(actualLeafAdapter);
        if (!_tools.TryGetValue(name, out var registered) || !ReferenceEquals(registered.Declaration, exactDeclaration)) return false;
        static IPreparedToolAdapter Leaf(IPreparedToolAdapter adapter)
        {
            while (adapter is NamedAdapter named) adapter = named.Original;
            return adapter;
        }
        return ReferenceEquals(Leaf(registered.Adapter), Leaf(actualLeafAdapter));
    }
}
