using System.Collections.Immutable;
using System.Text.Json;

namespace PiSharp.Cli.Interactive;

// A presentation copy of the host's ordered queue_update event, never queue admission authority.
internal sealed record TerminalPendingQueueSnapshot(long SessionGeneration,
    ImmutableArray<string> Steering, ImmutableArray<string> FollowUp)
{
    internal static TerminalPendingQueueSnapshot Empty(long generation) => new(generation, [], []);

    internal static TerminalPendingQueueSnapshot Parse(JsonElement body, long generation)
    {
        if (generation < 1) throw new InvalidOperationException("Pending queue generation is invalid.");
        return new(generation, Read("steering", 10), Read("followUp", 11));

        ImmutableArray<string> Read(string property, int prefixCharacters)
        {
            if (!body.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > 256)
                throw new InvalidOperationException("Pending queue exceeds its native presentation profile.");
            var values = ImmutableArray.CreateBuilder<string>(array.GetArrayLength()); var characters = 0;
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) throw new InvalidOperationException("Pending queue text is invalid.");
                var text = item.GetString()!; ChatEditor.Validate(text);
                if (text.Length > 65_536 - prefixCharacters || characters > 1_048_576 - text.Length)
                    throw new InvalidOperationException("Pending queue exceeds its native presentation profile.");
                characters += text.Length; values.Add(text);
            }
            return values.MoveToImmutable();
        }
    }
}

internal interface ITerminalPendingQueuePresentation
{
    ValueTask PresentPendingAsync(TerminalPendingQueueSnapshot snapshot, CancellationToken token);
}
