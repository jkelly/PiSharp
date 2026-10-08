namespace PiSharp.Extensions;

// Approved negative fixture only. Never a host ABI or a runtime contract replacement.
public interface IExtensionRegistry { }
public interface IPiSharpExtension : IAsyncDisposable
{
    ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken cancellationToken);
}
