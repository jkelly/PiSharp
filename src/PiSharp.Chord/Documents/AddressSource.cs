// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/documents/address-source.ts
//
// Watchable document addresses (slice 6a §11; decision "One retirement mechanism"): the frames of a source that follows one address, a
// singleton's slot or one key of a keyed slot. A change arrives with its ops, a write over another base as a root replacement, a
// retirement as [["r", initial()]] with the retired flag, and a document created after a retirement in the same batch as one root
// replacement with the flag. A source is the state layer's (State/Watchable.cs): its value is a JsonNode, here always the document's
// JsonObject (a covariant override). `prepared.base === before` is reference identity: the draft started from the value the address read.
using System.Collections.Immutable;
using System.Text.Json.Nodes;
using PiSharp.Chord.Delta;
using PiSharp.Chord.State;

namespace PiSharp.Chord.Documents;

/// <summary>A source following one document address (upstream <c>AddressSource</c>); <see cref="Value"/> is the document's, or
/// <c>initial()</c> while it is absent.</summary>
internal abstract class AddressSource : Source
{
    private static readonly ImmutableArray<Op> NoOps = [];

    // The ops of the current batch's frame, and whether the document was retired in it.
    private ImmutableArray<Op> ops = NoOps;
    private bool retired;

    protected AddressSource(Batch batch)
        : base(batch)
    {
    }

    /// <inheritdoc/>
    public abstract override JsonObject Value { get; }

    /// <inheritdoc/>
    public override void Deliver(ChordContext context)
    {
        Publish(Value, ops, retired, context);
        ops = NoOps;
        retired = false;
    }

    /// <summary>
    /// A commit wrote the document, which read <paramref name="before"/>: its ops, or a root replacement when the draft started from another
    /// base or the document was retired earlier in the batch.
    /// </summary>
    public void Written(JsonObject before, Prepared<JsonObject> prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        // The ops are kept for a frame, which only a watched source gets: an unwatched one keeps none.
        if (Watched) ops = ReferenceEquals(prepared.Base, before) && !retired ? prepared.Ops : [new ReplaceOp(prepared.Value)];
        Changed();
    }

    /// <summary>The document was retired: it reads <paramref name="initial"/> again, and the frame says so.</summary>
    public void Retired(JsonObject initial)
    {
        // Reset by the frame, which only a watched source gets.
        if (Watched)
        {
            ops = [new ReplaceOp(initial)];
            retired = true;
        }
        Changed();
        RetiredInBatch();
    }

    /// <summary>The value changed outside a commit (a migration, a load): a root replacement.</summary>
    public void Replaced()
    {
        if (Watched) ops = [new ReplaceOp(Value)];
        Changed();
    }
}
