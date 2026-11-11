// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/node/app/sessions.ts.
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Documents;
using PiSharp.Chord.Host;
using PiSharp.Chord.Hosting.Http;
using static PiSharp.Chord.Contracts.Schemas;

namespace PiSharp.Chord.Hosting.App;

public enum SessionKind { Browser, Device, Ephemeral }
public sealed record IssuedSession(string Id, string Token, string Cookie);
public sealed record SessionInfo(string Id, string Label, SessionKind Kind, JsonObject Principal, long Created, long LastUsed);

/// <summary>Hashed credentials stored in the host's persisted document, and bounded in-memory ephemeral credentials.</summary>
public sealed partial class Sessions : IAsyncDisposable
{
    public const string CookieName = "chord-session";
    public static readonly TimeSpan IdleExpiry = TimeSpan.FromDays(400);
    private static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(1);
    private readonly byte[] cookieSecret;
    private readonly EphemeralSessions ephemeral;
    private readonly TimeProvider time;
    private IReference? reference;
    private bool disposed;
    public Facet Facet { get; }
    public static Contract Type { get; } = Contract.Local("chord.app.sessions", new()
    {
        Version = 1, Params = [], Members = new()
        {
            ["list"] = Arr(Obj(("id", Str()), ("label", Str()), ("kind", OneOf("browser", "device")), ("principal", Unknown()), ("created", Num()), ("lastUsed", Num()))),
            ["issue"] = Method(Obj(("principal", Unknown()), ("label", Str()), ("kind", OneOf("browser", "device", "ephemeral")), ("secure", Opt(Bool()))), Obj(("id", Str()), ("token", Str()), ("cookie", Str()))),
            ["verify"] = Method(Str(), Unknown()), ["revoke"] = Method(Str()),
        },
    });
    private static StateDefinition Document { get; } = StateDefinition.Define("chord.app.sessions", new()
    {
        Version = 1, Persist = true, Initial = () => new() { ["sessions"] = new JsonObject() },
        Schema = Obj(("sessions", Map(Obj(("hash", Str()), ("label", Str()), ("kind", OneOf("browser", "device")), ("principal", Str()), ("created", Num()), ("lastUsed", Num()))))),
        CheckpointWhen = (_, _, info) => info.DeltasSinceBase >= 50,
    });
    public Sessions(string cookieSecret, int maxEphemeral = 1000, TimeProvider? time = null)
    {
        if (string.IsNullOrEmpty(cookieSecret)) throw new ArgumentException("cookie signing secret is required", nameof(cookieSecret));
        this.cookieSecret = Encoding.UTF8.GetBytes(cookieSecret); this.time = time ?? TimeProvider.System;
        ephemeral = new(maxEphemeral, time: this.time);
        Facet = PiSharp.Chord.Host.Facet.Define((host, contributions) =>
        {
            host.Serve(Type, (_, _, self) =>
            {
                var document = self.State(Document);
                return new(new Dictionary<string, object?>
                {
                    ["list"] = self.Derive([document], values => ListJson((JsonObject)values[0]!)),
                    ["issue"] = (HostMethod)(async (args, ctx) =>
                    {
                        var input = (JsonObject)args!;
                        if (input["principal"] is not JsonObject principal) throw new ArgumentException("principal must be an object");
                        AppPrincipals.Require(principal);
                        var kind = ParseKind(input["kind"]!.GetValue<string>());
                        var id = Secrets.NewToken(12); var secret = Secrets.NewToken(); var now = this.time.GetUtcNow().ToUnixTimeMilliseconds();
                        var hash = Secrets.HashToken(secret);
                        if (kind == SessionKind.Ephemeral) ephemeral.Issue(id, hash, principal);
                        else await document.ChangeAsync(draft => draft["sessions"]![id] = new JsonObject
                        {
                            ["hash"] = hash, ["label"] = input["label"]!.GetValue<string>(), ["kind"] = KindName(kind),
                            ["principal"] = principal.ToJsonString(), ["created"] = now, ["lastUsed"] = now,
                        }, ctx.Context).ConfigureAwait(false);
                        var token = id + "." + secret;
                        return new JsonObject { ["id"] = id, ["token"] = token, ["cookie"] = Cookie(token, kind, input["secure"]?.GetValue<bool>() == true) };
                    }),
                    ["verify"] = (HostMethod)(async (args, ctx) =>
                    {
                        if (args is not JsonValue value || !value.TryGetValue<string>(out var token) || !TryToken(token, out var id, out var secret)) return null;
                        var temporary = ephemeral.Verify(id, secret); if (temporary is not null) return temporary;
                        var sessions = (JsonObject)document.Value!["sessions"]!;
                        if (sessions[id] is not JsonObject session || !Secrets.MatchesHash(secret, session["hash"]!.GetValue<string>())) return null;
                        var now = this.time.GetUtcNow().ToUnixTimeMilliseconds(); var last = session["lastUsed"]!.GetValue<long>();
                        if (now - last > IdleExpiry.TotalMilliseconds)
                        {
                            await document.ChangeAsync(draft => ((JsonObject)draft["sessions"]!).Remove(id), ctx.Context).ConfigureAwait(false); return null;
                        }
                        var principal = JsonNode.Parse(session["principal"]!.GetValue<string>()) as JsonObject;
                        if (principal is null) return null; AppPrincipals.Require(principal);
                        if (now - last >= TouchInterval.TotalMilliseconds)
                            await document.ChangeAsync(draft => { if (draft["sessions"]![id] is JsonObject stored) stored["lastUsed"] = now; }, ctx.Context).ConfigureAwait(false);
                        return principal;
                    }),
                    ["revoke"] = (HostMethod)(async (args, ctx) =>
                    {
                        var id = ((JsonValue)args!).GetValue<string>();
                        if (!ephemeral.Revoke(id)) await document.ChangeAsync(draft => ((JsonObject)draft["sessions"]!).Remove(id), ctx.Context).ConfigureAwait(false);
                        return null;
                    }),
                });
            });
            contributions.Contribute(HttpRegistries.Authenticators, (HttpAuthenticator)(async (request, ctx) =>
            {
                var token = RequestToken(request); return token is null ? null : await VerifyAsync(token, ctx).ConfigureAwait(false);
            }));
            contributions.Contribute(HttpRegistries.Http, new HttpContribution
            {
                Wraps = [new("chord.app.rolling-cookie", async (request, next, ctx) =>
                {
                    var response = await next().ConfigureAwait(false);
                    if (ctx.Principal is null || response.Headers.Keys.Any(key => key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))) return response;
                    var signed = SignedCookies.ReadSigned(request.Headers.Cookie.ToString(), CookieName, this.cookieSecret, this.time);
                    if (signed?.Expires is not { } expires || expires - this.time.GetUtcNow() > IdleExpiry - TimeSpan.FromDays(1) ||
                        await VerifyAsync(signed.Value, ctx.Context).ConfigureAwait(false) is null) return response;
                    var headers = new Dictionary<string, string[]>(response.Headers, StringComparer.OrdinalIgnoreCase)
                    { ["Set-Cookie"] = [Cookie(signed.Value, SessionKind.Browser, IsSecure(request))] };
                    return response with { Headers = headers };
                }, Phase: "headers", All: true)],
            });
        });
    }
    public async Task AttachAsync(ChordHost host, ChordContext context)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (reference is not null) throw new InvalidOperationException("sessions already attached");
        var acquired = await host.GetAsync(Type, new(), context).ConfigureAwait(false);
        if (disposed) { acquired.Dispose(); throw new ObjectDisposedException(nameof(Sessions)); }
        reference = acquired;
    }
    private IReference Reference => !disposed && reference is { } value ? value : throw new InvalidOperationException("sessions must be attached to a loaded host");
    public async Task<IssuedSession> IssueAsync(JsonObject principal, string label, SessionKind kind, bool secure, ChordContext context)
    {
        AppPrincipals.Require(principal);
        var result = (JsonObject)(await Reference.CallAsync("issue", new JsonObject
        { ["principal"] = principal.DeepClone(), ["label"] = label, ["kind"] = KindName(kind), ["secure"] = secure }, context).ConfigureAwait(false))!;
        return new(result["id"]!.GetValue<string>(), result["token"]!.GetValue<string>(), result["cookie"]!.GetValue<string>());
    }
    public async Task<JsonObject?> VerifyAsync(string token, ChordContext context)
    {
        if (!TryToken(token, out _, out _) || disposed || reference is null) return null;
        return await reference.CallAsync("verify", JsonValue.Create(token), context).ConfigureAwait(false) as JsonObject;
    }
    public async Task RevokeAsync(string id, ChordContext context) => _ = await Reference.CallAsync("revoke", JsonValue.Create(id), context).ConfigureAwait(false);
    public Task<IReadOnlyList<SessionInfo>> ListAsync(ChordContext context)
    {
        context.AbortSignal?.ThrowIfCancellationRequested();
        var result = ((JsonArray)Reference.Member("list").Value!).Select(item =>
        {
            var value = (JsonObject)item!; return new SessionInfo(value["id"]!.GetValue<string>(), value["label"]!.GetValue<string>(),
                ParseKind(value["kind"]!.GetValue<string>()), (JsonObject)value["principal"]!.DeepClone(), value["created"]!.GetValue<long>(), value["lastUsed"]!.GetValue<long>());
        }).ToArray();
        return Task.FromResult<IReadOnlyList<SessionInfo>>(result);
    }
    public Task<Func<Task>?> LinksOpenedAsync(HttpRequestContext request, JsonObject principal)
    {
        var token = RequestToken(request); Action? close = token is not null && TryToken(token, out var id, out _) ? ephemeral.LinkOpened(id) : null;
        return Task.FromResult<Func<Task>?>(close is null ? null : () => { close(); return Task.CompletedTask; });
    }
    private string? RequestToken(HttpRequestContext request)
    {
        var authorization = request.Headers.Authorization.ToString();
        var match = Bearer().Match(authorization);
        return match.Success ? match.Groups[1].Value : SignedCookies.Read(request.Headers.Cookie.ToString(), CookieName, cookieSecret, time);
    }
    public static bool IsSecure(HttpRequestContext request) => request.Provenance.Kind != "local";
    public string Cookie(string token, SessionKind kind, bool secure) => SignedCookies.Create(CookieName, token, cookieSecret, secure,
        kind == SessionKind.Ephemeral ? null : IdleExpiry, clock: time);
    internal static bool TryToken(string token, out string id, out string secret)
    {
        id = secret = "";
        if (token is null || token.Length > 128) return false;
        var dot = token.IndexOf('.'); if (dot != 16 || dot != token.LastIndexOf('.') || token.Length != dot + 44) return false;
        id = token[..dot]; secret = token[(dot + 1)..];
        return id.All(IsTokenCharacter) && secret.All(IsTokenCharacter);
    }
    private static bool IsTokenCharacter(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '_';
    private static SessionKind ParseKind(string kind) => kind switch { "browser" => SessionKind.Browser, "device" => SessionKind.Device, "ephemeral" => SessionKind.Ephemeral, _ => throw new ArgumentException("invalid session kind") };
    private static string KindName(SessionKind kind) => kind switch { SessionKind.Browser => "browser", SessionKind.Device => "device", SessionKind.Ephemeral => "ephemeral", _ => throw new ArgumentOutOfRangeException(nameof(kind)) };
    private static JsonArray ListJson(JsonObject document) => new(((JsonObject)document["sessions"]!).Select(pair =>
    {
        var stored = (JsonObject)pair.Value!;
        return (JsonNode?)new JsonObject { ["id"] = pair.Key, ["label"] = stored["label"]!.DeepClone(), ["kind"] = stored["kind"]!.DeepClone(),
            ["principal"] = JsonNode.Parse(stored["principal"]!.GetValue<string>()), ["created"] = stored["created"]!.DeepClone(), ["lastUsed"] = stored["lastUsed"]!.DeepClone() };
    }).ToArray());
    [GeneratedRegex("^Bearer\\s+(\\S+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex Bearer();
    public ValueTask DisposeAsync()
    {
        if (disposed) return ValueTask.CompletedTask; disposed = true;
        reference?.Dispose(); reference = null; ephemeral.Dispose(); System.Security.Cryptography.CryptographicOperations.ZeroMemory(cookieSecret);
        return ValueTask.CompletedTask;
    }
}
