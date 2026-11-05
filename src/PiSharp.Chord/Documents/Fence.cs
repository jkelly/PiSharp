// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/documents/fence.ts
//
// The seal fence (slice 6a §1.3): the token an attempt, a job run's invocation or the object itself hands its transactions. Once sealed, new
// transactions under it are refused `released` and the open one is discarded. An attempt may end on another thread than the one its
// transactions run on, so the flag is volatile.
namespace PiSharp.Chord.Documents;

/// <summary>One seal token; sealed once, never unsealed (upstream <c>SealToken</c>).</summary>
internal sealed class SealToken
{
    private volatile bool closed;

    /// <summary>Whether work under this token is refused.</summary>
    public bool Sealed => closed;

    /// <summary>Seals the token; <see cref="Documents.Fence"/> also discards the transaction it holds open.</summary>
    public void Seal() => closed = true;
}
