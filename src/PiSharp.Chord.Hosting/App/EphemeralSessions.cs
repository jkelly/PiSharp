// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/node/app/ephemeral-sessions.ts.
using System.Text.Json.Nodes;

namespace PiSharp.Chord.Hosting.App;

/// <summary>Bounded, process-local sessions. No link for a minute ends a session, including its first link.</summary>
public sealed class EphemeralSessions : IDisposable
{
    private sealed class Entry(string hash, JsonObject principal)
    {
        internal string Hash { get; } = hash;
        internal JsonObject Principal { get; } = principal;
        internal int Links;
        internal ITimer? Timer;
    }
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly int maximum;
    private readonly TimeSpan grace;
    private readonly TimeProvider time;
    private bool disposed;
    public EphemeralSessions(int maximum = 1000, TimeSpan? grace = null, TimeProvider? time = null)
    {
        if (maximum < 1) throw new ArgumentOutOfRangeException(nameof(maximum));
        this.maximum = maximum; this.grace = grace ?? TimeSpan.FromMinutes(1); this.time = time ?? TimeProvider.System;
        if (this.grace <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(grace));
    }
    public int Count { get { lock (gate) return entries.Count; } }
    public void Issue(string id, string hash, JsonObject principal)
    {
        AppPrincipals.Require(principal);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            RevokeLocked(id);
            var entry = new Entry(hash, (JsonObject)principal.DeepClone());
            entries.Add(id, entry); WaitForLink(id, entry);
            while (entries.Count > maximum) RevokeLocked(entries.First().Key);
        }
    }
    public JsonObject? Verify(string id, string secret)
    {
        lock (gate) return !disposed && entries.TryGetValue(id, out var entry) && Secrets.MatchesHash(secret, entry.Hash)
            ? (JsonObject)entry.Principal.DeepClone() : null;
    }
    public bool Revoke(string id) { lock (gate) return RevokeLocked(id); }
    private bool RevokeLocked(string id)
    {
        if (!entries.Remove(id, out var entry)) return false;
        entry.Timer?.Dispose(); entry.Timer = null; return true;
    }
    public Action? LinkOpened(string id)
    {
        lock (gate)
        {
            if (disposed || !entries.TryGetValue(id, out var entry)) return null;
            entry.Links++; entry.Timer?.Dispose(); entry.Timer = null;
            var closed = false;
            return () =>
            {
                lock (gate)
                {
                    if (closed) return; closed = true; entry.Links--;
                    if (entry.Links == 0 && entries.TryGetValue(id, out var current) && ReferenceEquals(entry, current)) WaitForLink(id, entry);
                }
            };
        }
    }
    private void WaitForLink(string id, Entry entry) => entry.Timer = time.CreateTimer(_ =>
    {
        lock (gate)
            if (entry.Links == 0 && entries.TryGetValue(id, out var current) && ReferenceEquals(entry, current)) RevokeLocked(id);
    }, null, grace, Timeout.InfiniteTimeSpan);
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return; disposed = true;
            foreach (var entry in entries.Values) entry.Timer?.Dispose();
            entries.Clear();
        }
    }
}
