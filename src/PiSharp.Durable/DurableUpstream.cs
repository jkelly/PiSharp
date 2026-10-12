namespace PiSharp.Durable;

/// <summary>The upstream source this port follows (decision 0005): Pi's <c>packages/durable</c> and the Chord subset it uses, at the
/// pinned Pi v1.1.0 commit. <c>compatibility/durable-port-map.json</c> maps each upstream file to its C# counterpart.</summary>
public static class DurableUpstream
{
    /// <summary>The pinned Pi commit.</summary>
    public const string Commit = "abe508e1b89912adde45528136c3221eb69acdd7";
    /// <summary>The upstream package and version.</summary>
    public const string Package = "@earendil-works/pi-durable@1.1.0";
}
