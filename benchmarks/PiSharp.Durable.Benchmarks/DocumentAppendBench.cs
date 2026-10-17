// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/test/document-append-bench.ts
// Appending to a list, one commit per item: one array in one document against one family member per item, as a journal can be stored.
// Reports milliseconds and microseconds per append at each size. An append costs one copy of the array for the next immutable value;
// the family's cost stays flat. Upstream's performance.now() is a Stopwatch.
using System.Diagnostics;
using System.Globalization;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Storage;

namespace PiSharp.Durable.Benchmarks;

/// <summary>Upstream <c>document-append-bench.ts</c> (<c>npm run bench:document-append</c>).</summary>
public static class DocumentAppendBench
{
    /// <summary>Upstream's default sizes.</summary>
    public static IReadOnlyList<int> DefaultSizes { get; } = [1_000, 8_000, 16_000, 32_000];

    private static readonly SessionDocToken Journal = Documents.DefineSessionDoc("bench.journal", 1,
        () => JsonValue.Object(KeyValuePair.Create("entries", (JsonValue)JsonValue.Array())));

    private static readonly SessionDocFamilyToken Member = Documents.DefineSessionDocFamily("bench.member", 1,
        _ => JsonValue.Object(KeyValuePair.Create("issued", (JsonValue)0), KeyValuePair.Create("at", (JsonValue)0)));

    /// <summary>One row: the array and family timings at <paramref name="N"/> appends.</summary>
    public sealed record Row(int N, double ArrayMs, double FamilyMs);

    /// <summary>Runs both kinds at each size, writes upstream's line per size and returns the rows.</summary>
    public static async Task<IReadOnlyList<Row>> RunAsync(IReadOnlyList<int> sizes, TextWriter output)
    {
        var rows = new List<Row>();
        foreach (var n in sizes)
        {
            var array = await RunAsync("array", n);
            var family = await RunAsync("family", n);
            string Per(double ms) => ((ms / n) * 1000).ToString("F0", CultureInfo.InvariantCulture) + " µs";
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"n={n}: array {array:F0} ms ({Per(array)}/append), family {family:F0} ms ({Per(family)}/append)"));
            rows.Add(new Row(n, array, family));
        }
        return rows;
    }

    /// <summary>Upstream <c>run(kind, n)</c>: <paramref name="n"/> commits on a fresh memory session, in milliseconds.</summary>
    public static async Task<double> RunAsync(string kind, int n)
    {
        var context = DurableContext.Background;
        var session = Session.Create(new MemoryStorage());
        var started = Stopwatch.GetTimestamp();
        for (var i = 0; i < n; i++)
        {
            var index = i;
            await session.CommitAsync(async tx =>
            {
                if (kind == "array")
                {
                    (await tx.DocAsync(Journal)).GetArray("entries").Add(JsonValue.Object(
                        KeyValuePair.Create("key", (JsonValue)$"c{index}"),
                        KeyValuePair.Create("issued", (JsonValue)index),
                        KeyValuePair.Create("at", (JsonValue)index)));
                }
                else
                {
                    var member = await tx.DocAsync(Member, $"c{index}", JsonValue.Null);
                    member.Set("issued", index);
                    member.Set("at", index);
                }
            }, context);
        }
        var ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        await session.CloseAsync(context);
        return ms;
    }

    /// <summary>Upstream's argument: comma-separated sizes, or the defaults.</summary>
    public static IReadOnlyList<int> ParseSizes(string? argument) =>
        argument is null ? DefaultSizes : [.. argument.Split(',').Select(size => int.Parse(size, CultureInfo.InvariantCulture))];
}
