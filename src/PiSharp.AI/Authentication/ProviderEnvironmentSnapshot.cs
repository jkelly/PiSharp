using System.Collections.Immutable;

namespace PiSharp.AI.Authentication;

/// <summary>An injected lookup only. This slice provides no ambient environment implementation.</summary>
public interface IInjectedEnvironmentLookup
{
    ValueTask<string?> ReadAsync(string name, CancellationToken cancellationToken);
}

/// <summary>Copied scoped/process inputs with the original truthy override/fallback semantics.</summary>
public sealed class ProviderEnvironmentSnapshot : IInjectedEnvironmentLookup
{
    private readonly ImmutableDictionary<string, string?> scoped;
    private readonly ImmutableDictionary<string, string?> process;

    public ProviderEnvironmentSnapshot(IEnumerable<KeyValuePair<string, string?>>? scoped = null,
        IEnumerable<KeyValuePair<string, string?>>? process = null)
    {
        this.scoped = (scoped ?? []).ToImmutableDictionary(StringComparer.Ordinal);
        this.process = (process ?? []).ToImmutableDictionary(StringComparer.Ordinal);
    }

    public string? GetValue(string name)
    {
        scoped.TryGetValue(name, out var local);
        if (!string.IsNullOrEmpty(local)) return local;
        process.TryGetValue(name, out var fallback);
        return string.IsNullOrEmpty(fallback) ? null : fallback;
    }

    public ValueTask<string?> ReadAsync(string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(GetValue(name));
    }

    public override string ToString() => "ProviderEnvironmentSnapshot [values redacted]";
}
