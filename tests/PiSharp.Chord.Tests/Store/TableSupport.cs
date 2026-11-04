using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Store;

// Authored test support of the table cases (area cv2-store-tables); no upstream file: the C# forms of what upstream's table tests write
// inline (toMatchObject on a rejection, index maps, object literals for rows, keys and sets, a commit of a transaction's changes, a rows
// step's result). Upstream's table tests open a ChordStore with test/next/support.ts's memoryStore() and use its tableOwner, tableIn and
// holdNextCommit; area cv2-store-engine brought the store and moved those helpers to ChordSupport.cs, where the stand-in store these
// cases first ran on gave way to ChordStore.
internal static partial class Program
{
    /// <summary>expect(await rejection(...)).toMatchObject({ code, reason }): <paramref name="rejection"/> is ChordError JSON with
    /// <paramref name="code"/> (when given), <paramref name="reason"/> (when given) and a message containing <paramref name="message"/>
    /// (when given); an exception's message is checked alone.</summary>
    internal static void Rejected(object rejection, string? code, string? reason, string what, string? message = null)
    {
        if (rejection is Exception error)
        {
            Check(code is null && reason is null, $"{what}: expected a ChordError, actual {error.GetType().Name}: {error.Message}");
            Check(message is not null && error.Message.Contains(message, StringComparison.Ordinal), $"{what}: expected a message containing <{message}>, actual <{error.Message}>.");
            return;
        }
        var json = (JsonObject)rejection;
        if (code is not null) Equal(code, (string?)json["code"], what + ": the code");
        if (reason is not null) Equal(reason, (string?)json["reason"], what + ": the reason");
        if (message is not null)
        {
            var actual = (string?)json["message"] ?? "";
            Check(actual.Contains(message, StringComparison.Ordinal), $"{what}: expected a message containing <{message}>, actual <{actual}>.");
        }
    }

    /// <summary>The error <paramref name="task"/> fails with, as ChordError JSON when it is one (upstream <c>rejection</c>).</summary>
    internal static Task<object> StoreRejection(Task task) => ChordSupport.Rejection(task);

    /// <summary>Index definitions by name, in order (a TableOptions.Indexes).</summary>
    internal static OrderedDictionary<string, IReadOnlyList<string>> StoreIndexes(params (string Name, string[] Columns)[] indexes)
    {
        var map = new OrderedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var (name, columns) in indexes) map[name] = columns;
        return map;
    }

    /// <summary>A JSON value from its text: a row, a key or a set as upstream writes them as object literals.</summary>
    internal static JsonNode StoreValue(string json) => JsonNode.Parse(json)!;

    /// <summary>Stages <paramref name="fill"/>'s writes to <paramref name="handle"/>'s table and commits them through the open fence.</summary>
    internal static async Task StoreCommitWith(ChordStore store, TableHandle handle, Action<TableChanges> fill, ChordContext? context = null)
    {
        var staged = new List<IStagedWrite>();
        fill(new TableChanges(handle, staged));
        await store.CommitAsync(staged, CommitFence.Open, context ?? ChordContext.Background);
    }

    /// <summary>The outbox's commits after <paramref name="afterSeq"/> (up to <paramref name="limit"/>), each as its JSON
    /// (<c>{ seq, nextId, writes }</c>; upstream reads <c>store.outbox!.read(afterSeq, limit, ctx)</c>).</summary>
    internal static async Task<List<JsonObject>> StoreOutbox(ChordStore store, long afterSeq, long limit, ChordContext context) =>
        [.. (await store.Outbox!.ReadAsync(afterSeq, limit, context)).Select(commit => commit.ToJson())];

    /// <summary>A rows step's result: column values as operands (<see cref="Operand.Undefined"/> clears).</summary>
    internal static Dictionary<string, Operand> StorePatch(params (string Column, Operand Value)[] values) =>
        values.ToDictionary(value => value.Column, value => value.Value, StringComparer.Ordinal);
}
