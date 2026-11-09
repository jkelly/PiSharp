using System.Collections;
using PiSharp.Chord.Registries;

namespace PiSharp.Chord.Hosting.Http;

// Preserve contributor lifetime metadata while keeping the public composed list shape.
internal sealed class HttpAuthenticators : IReadOnlyList<HttpAuthenticator>
{
    private readonly HttpAuthenticator[] values;
    internal IReadOnlyList<object?> CallbackOwners { get; }
    internal HttpAuthenticators(IEnumerable<RegistryEntry<HttpAuthenticator>> entries)
    { var ordered = entries.OrderBy(entry => entry.Plugin, StringComparer.Ordinal).ToArray(); values = ordered.Select(entry => entry.Value).ToArray(); CallbackOwners = ordered.Select(entry => entry.CallbackOwner).ToArray(); }
    public int Count => values.Length;
    public HttpAuthenticator this[int index] => values[index];
    public IEnumerator<HttpAuthenticator> GetEnumerator() => ((IEnumerable<HttpAuthenticator>)values).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => values.GetEnumerator();
}
