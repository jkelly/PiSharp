// PiSharp-only (no upstream Pi source): per-kind defaults of the media tools: a model and default tool arguments, which a call's own
// arguments override.
using PiSharp.AI.Media;
using PiSharp.Durable.Chord;

namespace PiSharp.Durable.Tools;

/// <summary>A kind's defaults: the model a call without <c>model</c> uses, and arguments merged under the call's own (a call's argument
/// wins). Options use the tool's argument names, e.g. <c>{"background": "transparent", "output_format": "png"}</c> for images.</summary>
public sealed record MediaDefault(string? Model, JsonObjectValue Options)
{
    public static MediaDefault None { get; } = new(null, JsonObjectValue.Empty);
    public bool IsEmpty => Model is null && Options.Count == 0;
}

/// <summary>Where the media tools read and save their per-kind defaults.</summary>
public interface IMediaDefaults
{
    /// <summary>The kind's defaults now; <see cref="MediaDefault.None"/> when none are set.</summary>
    MediaDefault Get(MediaKind kind);
    /// <summary>Replaces the kind's defaults (null clears them) and saves them in <paramref name="scope"/> (<c>global</c> or
    /// <c>project</c>). Throws when they cannot be saved; the replacement still applies to this session.</summary>
    Task SetAsync(MediaKind kind, MediaDefault? value, string scope, CancellationToken cancellationToken);
}

/// <summary>Defaults kept in memory, seeded from settings and saved through <see cref="Persist"/> when one is given.</summary>
public sealed class MediaDefaults : IMediaDefaults
{
    private readonly object gate = new();
    private readonly Dictionary<MediaKind, MediaDefault> values = [];

    public MediaDefaults(IReadOnlyDictionary<MediaKind, MediaDefault>? initial = null)
    {
        foreach (var (kind, value) in initial ?? new Dictionary<MediaKind, MediaDefault>())
            if (!value.IsEmpty) values[kind] = value;
    }

    /// <summary>Saves a change: the kind, its new defaults (null when cleared) and the scope. Null keeps changes in memory only.</summary>
    public Func<MediaKind, MediaDefault?, string, CancellationToken, Task>? Persist { get; init; }

    public MediaDefault Get(MediaKind kind)
    {
        lock (gate) return values.GetValueOrDefault(kind) ?? MediaDefault.None;
    }

    public async Task SetAsync(MediaKind kind, MediaDefault? value, string scope, CancellationToken cancellationToken)
    {
        var next = value is { IsEmpty: false } ? value : null;
        // Applied before the save, as settings changes are: a settings file that cannot be written leaves the session on the new defaults.
        lock (gate)
        {
            if (next is null) values.Remove(kind);
            else values[kind] = next;
        }
        if (Persist is { } persist) await persist(kind, next, scope, cancellationToken).ConfigureAwait(false);
    }
}
