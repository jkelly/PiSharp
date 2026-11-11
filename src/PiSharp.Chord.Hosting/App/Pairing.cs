// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/node/app/pairing.ts.
using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using PiSharp.Chord.Host;
using PiSharp.Chord.Hosting.Http;

namespace PiSharp.Chord.Hosting.App;

public sealed record PairingLimits(int MaxPending = 100, int PerAddress = 10);
public sealed record PairingStart(string Code, string Poll);
public sealed record PairingInfo(string Code, string Label, DateTimeOffset Expires);
public sealed record PairingPoll(string Status, string? Token = null);

/// <summary>Bounded, process-local device pairings. Poll secrets are retained only as hashes.</summary>
public sealed class Pairings
{
    public const string Path = "/_chord/pair";
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    private sealed class Pending(string code, string label, string? peer, string hash, DateTimeOffset expires)
    {
        internal string Code { get; } = code;
        internal string Label { get; } = label;
        internal string? Peer { get; } = peer;
        internal string Hash { get; } = hash;
        internal DateTimeOffset Expires { get; } = expires;
        internal string? Token;
        internal bool Approving;
    }
    private readonly object gate = new();
    private readonly Dictionary<string, Pending> codes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Pending> polls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> peers = new(StringComparer.Ordinal);
    private readonly PairingLimits limits;
    private readonly TimeProvider time;
    private int anonymous;
    private readonly Sessions? sessions;
    private readonly JsonObject? owner;
    public Facet? Facet { get; }

    public Pairings(PairingLimits? limits = null, TimeProvider? time = null)
    {
        this.limits = limits ?? new(); this.time = time ?? TimeProvider.System;
        if (this.limits.MaxPending < 0 || this.limits.PerAddress < 0) throw new ArgumentOutOfRangeException(nameof(limits));
    }
    public Pairings(JsonObject owner, Sessions sessions, PairingLimits? limits = null, TimeProvider? time = null) : this(limits, time)
    {
        this.owner = AppPrincipals.Require(owner); this.sessions = sessions;
        Facet = PiSharp.Chord.Host.Facet.Define((_, self) => self.Contribute(HttpRegistries.Http, new HttpContribution
        {
            Routes = new Dictionary<string, HttpRoute>
            {
                [$"POST {Path}"] = new(async (request, context) =>
                {
                    var label = "device";
                    if (request.Headers.ContentLength is > 0 || request.Headers.ContainsKey("Transfer-Encoding"))
                    {
                        var body = await request.JsonAsync(4096, context.AbortSignal).ConfigureAwait(false);
                        if (AuthHttp.TryString(body, "label", out var supplied) && supplied.Trim().Length > 0) label = supplied.Trim()[..Math.Min(supplied.Trim().Length, 100)];
                    }
                    var begun = Begin(label, request.RemoteAddress?.ToString() ?? "", AppPrincipals.Same(context.Principal, this.owner));
                    return begun is null ? AuthHttp.Error(429) : new HttpResult.Json(new JsonObject { ["code"] = begun.Code, ["poll"] = begun.Poll })
                    { Headers = new Dictionary<string, string[]> { ["Cache-Control"] = ["no-store"] } };
                }, Public: true),
                [$"POST {Path}/poll"] = new(async (request, context) =>
                {
                    var body = await request.JsonAsync(4096, context.AbortSignal).ConfigureAwait(false);
                    var result = AuthHttp.TryString(body, "poll", out var poll) ? Poll(poll) : null;
                    if (result is null) return AuthHttp.Error(404);
                    var response = new JsonObject { ["status"] = result.Status };
                    if (result.Token is not null) response["token"] = result.Token;
                    return new HttpResult.Json(response) { Headers = new Dictionary<string, string[]> { ["Cache-Control"] = ["no-store"] } };
                }, Public: true),
                [$"GET {Path}/:code"] = new((request, context) =>
                {
                    if (!AppPrincipals.Same(context.Principal, this.owner)) return Task.FromResult(AuthHttp.Error(403));
                    var waiting = Waiting(request.Params.GetValueOrDefault("code") ?? "");
                    return Task.FromResult<HttpResult>(waiting is null ? AuthHttp.Error(404) : new HttpResult.Html(ConfirmPage(waiting)));
                }, Page: true),
                [$"POST {Path}/:code"] = new(async (request, context) =>
                {
                    if (!AppPrincipals.Same(context.Principal, this.owner)) return AuthHttp.Error(403);
                    var code = request.Params.GetValueOrDefault("code") ?? "";
                    var waiting = Waiting(code);
                    if (waiting is null || !await ApproveAsync(code, context.Context).ConfigureAwait(false)) return AuthHttp.Error(404);
                    return new HttpResult.Html("<p>Approved: " + WebUtility.HtmlEncode(waiting.Label) + " is signed in as the owner.</p>");
                }),
            },
        }));
    }

    public PairingStart? Begin(string label, string peer, bool owner = false)
    {
        ArgumentNullException.ThrowIfNull(label); ArgumentNullException.ThrowIfNull(peer);
        lock (gate)
        {
            Expire();
            if (!owner && (anonymous >= limits.MaxPending || peers.GetValueOrDefault(peer) >= limits.PerAddress)) return null;
            string code;
            do { code = Letters(4) + "-" + Letters(4); } while (codes.ContainsKey(code));
            var poll = Secrets.NewToken();
            var pending = new Pending(code, label[..Math.Min(label.Length, 100)], owner ? null : peer, Secrets.HashToken(poll), time.GetUtcNow() + Lifetime);
            codes.Add(code, pending); polls.Add(pending.Hash, pending);
            if (!owner) { anonymous++; peers[peer] = peers.GetValueOrDefault(peer) + 1; }
            return new(code, poll);
        }
    }

    public PairingInfo? Waiting(string code)
    {
        if (code.Length > 32) return null;
        lock (gate)
        {
            Expire();
            return codes.TryGetValue(code.ToUpperInvariant(), out var found) && found.Token is null && !found.Approving ? new(found.Code, found.Label, found.Expires) : null;
        }
    }

    public PairingPoll? Poll(string secret)
    {
        if (string.IsNullOrEmpty(secret) || secret.Length > 256) return null;
        lock (gate)
        {
            Expire();
            if (!polls.TryGetValue(Secrets.HashToken(secret), out var found)) return null;
            if (found.Token is null) return new("waiting");
            Remove(found);
            return new("approved", found.Token);
        }
    }

    /// <summary>Reserves a code before issuing its session, preventing two simultaneous approvals.</summary>
    public async Task<bool> ApproveAsync(string code, ChordContext context)
    {
        if (sessions is null || owner is null) throw new InvalidOperationException("Pairing approval requires sessions and an owner.");
        context.AbortSignal?.ThrowIfCancellationRequested();
        Pending found;
        lock (gate)
        {
            Expire();
            if (code.Length > 32 || !codes.TryGetValue(code.ToUpperInvariant(), out found!) || found.Token is not null || found.Approving) return false;
            found.Approving = true;
        }
        IssuedSession issued;
        // Once reserved, finish or roll back the actual session even if the browser disconnects.
        try { issued = await sessions.IssueAsync(owner, found.Label, SessionKind.Device, false, ChordContext.WithoutAbortSignal(context)).ConfigureAwait(false); }
        catch { lock (gate) found.Approving = false; throw; }
        lock (gate)
        {
            Expire();
            if (codes.TryGetValue(found.Code, out var current) && ReferenceEquals(current, found))
            { found.Token = issued.Token; found.Approving = false; return true; }
        }
        // It expired while the persisted session was being committed; no inaccessible credential survives.
        await sessions.RevokeAsync(issued.Id, ChordContext.Background).ConfigureAwait(false);
        return false;
    }

    private void Expire()
    {
        foreach (var expired in polls.Values.Where(value => value.Expires <= time.GetUtcNow()).ToArray()) Remove(expired);
    }
    private void Remove(Pending pending)
    {
        polls.Remove(pending.Hash); codes.Remove(pending.Code);
        if (pending.Peer is not { } peer) return;
        anonymous--; var count = peers[peer] - 1;
        if (count == 0) peers.Remove(peer); else peers[peer] = count;
    }
    private static string Letters(int count) => new(Enumerable.Range(0, count).Select(_ => Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]).ToArray());
    private static string ConfirmPage(PairingInfo pending) => "<!doctype html><title>Pair a device</title><p>Sign in <b>" +
        WebUtility.HtmlEncode(pending.Label) + "</b> as the owner? Its code: <b>" + WebUtility.HtmlEncode(pending.Code) +
        "</b> (compare it with the device).</p><form method=post><button>Approve</button></form>";
}
