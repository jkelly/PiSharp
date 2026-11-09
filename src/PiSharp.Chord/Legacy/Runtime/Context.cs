using System.Text.Json.Nodes;

namespace PiSharp.Chord.Legacy;

/// <summary>A root/call scope; references acquired through it are released when that scope settles.</summary>
public sealed class LegacyContext : IDisposable
{
    internal LegacyHost Host { get; }
    private readonly CancellationTokenSource _abort;
    private readonly CancellationToken _token;
    private readonly List<LegacyReference> _references = [];
    internal bool Closed;
    internal LegacyContext(LegacyHost host, string rootId, int codeVersion, object? principal, params CancellationToken[] signals)
    { Host = host; RootId = rootId; CodeVersion = codeVersion; Principal = principal; _abort = CancellationTokenSource.CreateLinkedTokenSource(signals); _token = _abort.Token; }
    public string RootId { get; }
    public int CodeVersion { get; }
    public object? Principal { get; }
    public CancellationToken CancellationToken => _token;
    public async Task<LegacyReference> GetAsync(LegacyContract contract, JsonNode? input, LegacyGetOptions? options = null)
    {
        Check(Host);
        var reference = Host.Hold(contract, input, options is { HasPrincipal: true } ? options : (options ?? new()) with { Principal = Principal, HasPrincipal = true });
        Host.Invoke(() => _references.Add(reference));
        try { await Host.AwaitReady(reference, CancellationToken); Check(Host); return reference; }
        catch { reference.Release(); throw; }
    }
    internal void Check(LegacyHost host)
    {
        if (!ReferenceEquals(Host, host)) throw ChordException.Invalid("foreign_ctx", "ctx belongs to another host");
        if (Closed) throw new ChordException("aborted", "ctx expired: its call already settled", "expired");
        if (!Host.HasCodeVersion(CodeVersion)) throw new ChordException("superseded", "ctx code version was retired");
        if (CancellationToken.IsCancellationRequested) throw new ChordException("aborted", "ctx aborted");
    }
    public void Dispose() => Host.Invoke(() =>
    {
        if (Closed) return; Closed = true;
        try { _abort.Cancel(); } catch (Exception error) { Host.Report(error); }
        foreach (var reference in _references) try { reference.Release(); } catch (Exception error) { Host.Report(error); }
        _references.Clear(); _abort.Dispose();
    });
}
