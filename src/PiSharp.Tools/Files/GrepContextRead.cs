namespace PiSharp.Tools.Files;

/// <summary>Borrowed, explicitly admitted UTF-8 context-read capability; no implicit local implementation.
/// The host must enforce maximumBytes during acquisition, retain its own disposal ownership, and settle
/// original I/O and cleanup before returning or throwing, including cancellation. Paths have passed the
/// grep final-action and canonical containment checks; this interface does not promise atomic identity
/// between the completed search and subsequent reads, or grant broader filesystem access.</summary>
public interface IGrepContextReader
{
    ValueTask<ReadOnlyMemory<byte>> ReadAsync(string absolutePath, int maximumBytes, CancellationToken cancellationToken);
}
