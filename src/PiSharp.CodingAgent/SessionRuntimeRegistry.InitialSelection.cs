namespace PiSharp.CodingAgent;

public sealed partial class SessionRuntimeRegistry
{
    /// <summary>Recompute initial names from this actual catalog under the already admitted lifetime
    /// selection. Models, adapters, final policy and invocation ownership are preserved.</summary>
    public SessionRuntimeRegistry WithInitialToolSelectionFromCatalog() =>
        LifetimeToolSelection is { } lifetime ? new(this, lifetime) : this;
}
