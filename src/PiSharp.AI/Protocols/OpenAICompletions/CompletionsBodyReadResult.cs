using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using PiSharp.Contracts;

namespace PiSharp.AI.Protocols.OpenAICompletions;

/// <summary>The owned result of an actual bounded body read. A closed reader has an absent value.</summary>
public sealed class CompletionsBodyReadResult
{
    internal static CompletionsBodyReadResult End { get; } = new(ImmutableArray<byte>.Empty, true, true);
    internal static CompletionsBodyReadResult Canceled { get; } = new(ImmutableArray<byte>.Empty, true);
    public bool PhysicalEof { get; }
    public bool Done { get; }
    public ImmutableArray<byte> Value { get; }

    /// <summary>Actual physical EOF; a true closed result with an absent value.</summary>
    public static CompletionsBodyReadResult EndOfInput => End;
    /// <summary>Logical closure without physical EOF, for example a canceled read.</summary>
    public static CompletionsBodyReadResult Closed => Canceled;
    /// <summary>Owns an actual byte chunk, including an empty nonterminal chunk.</summary>
    public static CompletionsBodyReadResult FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > 65_536) throw new ArgumentOutOfRangeException(nameof(bytes));
        return new(ImmutableArray.Create(bytes.ToArray()), false);
    }

    internal CompletionsBodyReadResult(ImmutableArray<byte> value, bool done, bool physicalEof = false)
    { Value = value; Done = done; PhysicalEof = physicalEof; }

    /// <summary>Source JSON view of actual bytes, including closed-reader own-undefined presence.</summary>
    public JsonData Snapshot
    {
        get
        {
            if (Done) return JsonData.Parse("{\"value\":{\"done\":true},\"ownUndefinedPaths\":[\"/value\"]}");
            var text = new StringBuilder("{\"value\":{\"value\":{");
            for (var index = 0; index < Value.Length; index++)
            {
                if (index != 0) text.Append(',');
                text.Append('"').Append(index.ToString(CultureInfo.InvariantCulture)).Append("\":")
                    .Append(Value[index].ToString(CultureInfo.InvariantCulture));
            }
            text.Append("},\"done\":false},\"ownUndefinedPaths\":[]}");
            // The existing maximum read is 65,536 bytes. Its numeric-key JSON is below
            // 1 MiB; this representation adds no input, decoder or cumulative budget.
            return JsonData.Parse(text.ToString());
        }
    }
}
