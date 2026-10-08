using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.Authentication;
using PiSharp.Extensions.Mcp.OAuth;
using PiSharp.Extensions.Runtime.Mcp.OAuth;

namespace PiSharp.McpAuthorizationCode.Tests;

public sealed record McpAuthorizationCodeControlOriginal(Task? Original, AggregateException? Aggregate, Exception? Direct);

public static class AuthorizationCodeControls
{
    private static readonly AsyncLocal<List<McpAuthorizationCodeControlOriginal>?> feed = new();
    public static IEnumerable<(string Name, Func<Task> Run)> Cases(Action<McpAuthorizationCodeControlOriginal>? retain = null) =>
    [
        ("oauth-code.rfc7636-pkce-save-before-redirect-and-exact-query", () => RunCase(Pkce, retain)),
        ("oauth-code.original-code-form-basic-client-and-strict-token-save", () => RunCase(CodeExchange, retain)),
        ("oauth-code.metadata-refusals-before-entropy-store-browser-effects", () => RunCase(MetadataRefusal, retain)),
        ("oauth-code.held-verifier-close-joins-original-and-fences-late-redirect", () => RunCase(HeldClose, retain)),
        ("oauth-code.multifault-shared-leaf-and-faulted-oce-originals-retained", () => RunCase(FaultInventory, retain)),
        ("oauth-code.after-await-normal-register-reentry-and-malformed-token-no-save", () => RunCase(CancellationAndMalformed, retain)),
        ("oauth-code.custom-client-auth-replaces-default-and-retires-mutable-proposal", () => RunCase(ClientAuthenticationMutation, retain)),
        ("oauth-code.held-custom-auth-close-fault-cancel-and-after-await-reentry-originals", () => RunCase(ClientAuthenticationOwnership, retain))
    ];
    private static async Task RunCase(Func<Task> invoke, Action<McpAuthorizationCodeControlOriginal>? retain)
    {
        var previous = feed.Value; var records = new List<McpAuthorizationCodeControlOriginal>(); feed.Value = records;
        Task? original = null; var failures = new List<Exception>(); var reportingFailed = false;
        try { original = invoke(); await original; records.Add(new(original, null, null)); }
        catch (Exception direct)
        { var aggregate = original?.Exception; records.Add(new(original, aggregate, direct)); if (aggregate is not null) failures.Add(aggregate); failures.Add(direct); }
        finally
        {
            feed.Value = previous;
            foreach (var record in records)
                try { retain?.Invoke(record); } catch (Exception error) { failures.Add(error); reportingFailed = true; }
            if (reportingFailed) foreach (var record in records)
            { if (record.Aggregate is not null) failures.Add(record.Aggregate); if (record.Direct is not null) failures.Add(record.Direct); }
        }
        if (failures.Count != 0) throw new AggregateException("OAuth code criteria/original/report inventory.", failures);
    }

    private sealed class Host
    {
        internal readonly List<string> Order = [];
        internal string? Verifier;
        internal Uri? Redirected;
        internal McpAuthorizationCodeRequest? Request;
        internal McpOAuthTokens? Tokens;
        internal Func<CancellationToken, ValueTask<byte[]>>? Entropy;
        internal Func<string, CancellationToken, ValueTask>? Save;
        internal Func<McpAuthorizationCodeRequest, CancellationToken, ValueTask<McpAuthorizationCodeResponse>>? Send;
        internal Func<IMcpAuthorizationCodeClientAuthentication, CancellationToken, ValueTask>? Authenticate;
        internal McpAuthorizationCodeDependencies Dependencies() => new(
            ct => { Order.Add("entropy"); return Entropy is null ? ValueTask.FromResult(Convert.FromBase64String("dBjftJeZ4CVP+mB92K27uhbUJU1p1r/wW1gFWFOEjXk=")) : Entropy(ct); },
            (verifier, ct) => { Order.Add("save-verifier"); Verifier = verifier; return Save is null ? ValueTask.CompletedTask : Save(verifier, ct); },
            ct => { Order.Add("read-verifier"); return ValueTask.FromResult(Verifier ?? "explicit-stored-verifier"); },
            (uri, ct) => { Order.Add("redirect"); Redirected = uri; return ValueTask.CompletedTask; },
            (request, ct) => { Order.Add("exchange"); Request = request; return Send is null ? ValueTask.FromResult(new McpAuthorizationCodeResponse(200,
                "{\"access_token\":\"synthetic-access\",\"token_type\":\"Bearer\",\"expires_in\":60,\"refresh_token\":\"synthetic-refresh\"}")) : Send(request, ct); },
            (tokens, ct) => { Order.Add("save-tokens"); Tokens = tokens; return ValueTask.CompletedTask; },
            Authenticate is null ? null : (proposal, ct) => { Order.Add("authenticate"); return Authenticate!(proposal, ct); });
    }
    private static McpAuthorizationCodeProfile Profile(JsonData? metadata = null) => new(new("https://authorization.example/issuer"),
        new("http://127.0.0.1:1234/callback"), new("synthetic-client", "synthetic-secret"), metadata, "read offline_access", "exact-state", "https://resource.example/mcp");
    private static async Task Pkce()
    {
        var host = new Host();
        await WithFlow(host, async (flow, inventory) =>
        {
            var result = await inventory.Join(() => flow.BeginAsync(Profile()));
            Require(host.Verifier == "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk", "RFC verifier differs.");
            var query = Fields(result.Query.TrimStart('?'));
            Require(result.AbsolutePath == "/authorize" && query["code_challenge"] == "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM" &&
                query["code_challenge_method"] == "S256" && query["response_type"] == "code" && query["prompt"] == "consent" &&
                query["state"] == "exact-state" && query["resource"] == "https://resource.example/mcp" &&
                query["redirect_uri"] == "http://127.0.0.1:1234/callback" && query["client_id"] == "synthetic-client", "Original authorization query differs.");
            Require(ReferenceEquals(result, host.Redirected) && host.Order.SequenceEqual(new[] { "entropy", "save-verifier", "redirect" }), "Verifier was not saved before exact redirect original.");
            Require(result.Query.EndsWith("&scope=read+offline_access&prompt=consent&resource=https%3A%2F%2Fresource.example%2Fmcp", StringComparison.Ordinal),
                "Original consent/resource query insertion order differs.");
        });
        await WithFlow(new Host(), async (flow, inventory) =>
        {
            var result = await inventory.Join(() => flow.BeginAsync(Profile(JsonData.Parse("{\"response_types_supported\":[\"code\"],\"authorization_endpoint\":\"https://authorization.example/authorize?keep=hello%20world&flag&keep=second&client_id=old&client_id=duplicate\"}"))));
            Require(result.Query.StartsWith("?keep=hello+world&flag=&keep=second&client_id=synthetic-client&response_type=code&", StringComparison.Ordinal) &&
                !result.Query.Contains("duplicate", StringComparison.Ordinal), "Original URLSearchParams normalization/duplicate replacement differs.");
        });
        await WithFlow(new Host(), async (flow, inventory) =>
        {
            var result = await inventory.Join(() => flow.BeginAsync(Profile(JsonData.Parse("{\"response_types_supported\":[\"code\"],\"authorization_endpoint\":\"https://authorization.example/authorize?x=%FF&partial=%E2%82&broken=%C3%28&literal=%ZZ&plus=one+two&encoded=%2B&mix=%41%FF%42\"}"))));
            Require(result.Query.StartsWith("?x=%EF%BF%BD&partial=%EF%BF%BD&broken=%EF%BF%BD%28&literal=%25ZZ&plus=one+two&encoded=%2B&mix=A%EF%BF%BDB&response_type=code&", StringComparison.Ordinal),
                "Original forgiving percent-byte UTF-8 decode differs for invalid, incomplete, malformed or plus components.");
        });
        await WithFlow(new Host(), async (flow, inventory) =>
        {
            var result = await inventory.Join(() => flow.BeginAsync(Profile(JsonData.Parse("{\"response_types_supported\":[\"code\"],\"authorization_endpoint\":\"https://authorization.example/authorize??x=y\"}"))));
            Require(result.Query.StartsWith("?%3Fx=y&response_type=code&", StringComparison.Ordinal), "A literal leading query question mark was removed.");
        });
        await WithFlow(new Host(), async (flow, inventory) =>
        {
            var result = await inventory.Join(() => flow.BeginAsync(Profile(JsonData.Parse("{\"response_types_supported\":[\"code\"],\"authorization_endpoint\":\"https://b\\u00fccher.example/authorize\"}"))));
            Require(result.AbsoluteUri.StartsWith("https://xn--bcher-kva.example/authorize?", StringComparison.Ordinal), "Original IDNA ASCII endpoint authority differs.");
        });
    }
    private static async Task CodeExchange()
    {
        var host = new Host();
        await WithFlow(host, async (flow, inventory) =>
        {
            var result = await inventory.Join(() => flow.CompleteAsync(Profile(), "exact code+"));
            var request = host.Request ?? throw new InvalidOperationException("No exchange."); var fields = Fields(request.Body);
            Require(request.Endpoint.AbsolutePath == "/token" && fields["grant_type"] == "authorization_code" && fields["code"] == "exact code+" &&
                fields["code_verifier"] == "explicit-stored-verifier" && fields["redirect_uri"] == "http://127.0.0.1:1234/callback" && !fields.ContainsKey("client_secret"), "Code form mismatch.");
            Require(request.Headers["Authorization"] == "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("synthetic-client:synthetic-secret")) &&
                ReferenceEquals(result, host.Tokens) && host.Order.SequenceEqual(new[] { "read-verifier", "exchange", "save-tokens" }), "Actual token publication/order mismatch.");
        });
        var utf16 = new Host { Send = (request, ct) => ValueTask.FromResult(new McpAuthorizationCodeResponse(200,
            "{\"access_token\":\"\\uD800\",\"token_type\":\"Bearer\",\"refresh_token\":\"\\uDC00\",\"scope\":\"x\\uD800y\"}")) };
        await WithFlow(utf16, async (flow, inventory) =>
        {
            var result = await inventory.Join(() => flow.CompleteAsync(Profile(), "synthetic"));
            Require(result.AccessToken == "\uD800" && result.RefreshToken == "\uDC00" && result.Scope == "x\uD800y" && ReferenceEquals(result, utf16.Tokens),
                "Original nonempty UTF16 token strings were normalized, refused or changed before saved publication.");
        });
        var errorUtf16 = new Host { Send = (request, ct) => ValueTask.FromResult(new McpAuthorizationCodeResponse(200,
            "{\"error\":\"invalid_grant\",\"error_description\":\"\\uD800\",\"error_uri\":\"\\uDC00\"}")) };
        await WithFlow(errorUtf16, async (flow, inventory) =>
        {
            await inventory.Expected(() => flow.CompleteAsync(Profile(), "synthetic"), error => error is McpOAuthProtocolException protocol &&
                protocol.Code == "invalid_grant" && protocol.Message == "\uD800" && protocol.ErrorUri == "\uDC00");
            Require(errorUtf16.Tokens is null, "UTF16 OAuth error saved tokens.");
        }, expectedCloseFault: true);
    }
    private static async Task ClientAuthenticationMutation()
    {
        var host = new Host(); var profile = Profile(JsonData.Parse("{\"token_endpoint_auth_methods_supported\":[\"client_secret_basic\"]}"));
        IMcpAuthorizationCodeClientAuthentication? saved = null;
        host.Authenticate = (proposal, ct) =>
        {
            saved = proposal;
            Require(ReferenceEquals(proposal.Client, profile.Client) && proposal.Endpoint.AbsolutePath == "/token" &&
                proposal.Metadata?.Value.GetRawText() == profile.Metadata?.Value.GetRawText() &&
                proposal.HeaderGet("ACCEPT") == "application/json" && proposal.HeaderGet("Authorization") is null &&
                proposal.FormGet("client_id") is null && proposal.FormGet("code") == "synthetic" && proposal.FormGet("resource") == profile.Resource,
                "Custom authentication saw default credentials or wrong original prefetch proposal.");
            proposal.HeaderSet("Authorization", "  Custom synthetic\t"); proposal.HeaderAppend("X-Custom", "first"); proposal.HeaderAppend("x-custom", "second");
            proposal.FormAppend("client_id", "old"); proposal.FormAppend("client_id", "duplicate"); proposal.FormSet("client_id", "custom-client");
            Require(proposal.FormGetAll("client_id").SequenceEqual(new[] { "custom-client" }), "Form Set failed first-position duplicate replacement.");
            proposal.FormDelete("redirect_uri"); proposal.FormAppend("custom", "first"); proposal.FormAppend("custom", "second");
            proposal.FormSet("utf16", "\uD800"); Require(proposal.FormGet("utf16") == "\uFFFD", "Original form USVString normalization differs.");
            try { proposal.HeaderSet("X-Bad", "line\r\nbreak"); throw new InvalidOperationException("Invalid header accepted."); } catch (ArgumentException) { }
            return ValueTask.CompletedTask;
        };
        await WithFlow(host, async (flow, inventory) =>
        {
            await inventory.Join(() => flow.CompleteAsync(profile, "synthetic"));
            var request = host.Request ?? throw new InvalidOperationException("Custom exchange absent.");
            Require(request.Headers["Authorization"] == "Custom synthetic" && request.Headers["x-custom"] == "first, second" &&
                !request.Headers.ContainsKey("X-Bad") && request.Body.Contains("&client_id=custom-client&custom=first&custom=second", StringComparison.Ordinal) &&
                !request.Body.Contains("client_secret", StringComparison.Ordinal) && !request.Body.Contains("redirect_uri", StringComparison.Ordinal) &&
                host.Order.SequenceEqual(new[] { "read-verifier", "authenticate", "exchange", "save-tokens" }), "Custom original mutation/order/default override differs.");
            try { saved!.FormSet("late", "mutation"); throw new InvalidOperationException("Retired proposal mutated."); } catch (ObjectDisposedException) { }
            Require(!request.Body.Contains("late", StringComparison.Ordinal), "Immutable exchange request changed after publication.");
        });
        // Original default authentication remains selected only when the custom callback is absent.
        foreach (var method in new[] { "client_secret_post", "none" })
        {
            var defaults = new Host();
            await WithFlow(defaults, async (flow, inventory) =>
            {
                var selected = Profile(JsonData.Parse("{\"token_endpoint_auth_methods_supported\":[\"" + method + "\"]}"));
                await inventory.Join(() => flow.CompleteAsync(selected, "synthetic"));
                var request = defaults.Request ?? throw new InvalidOperationException("Default exchange absent."); var fields = Fields(request.Body);
                Require(!request.Headers.ContainsKey("Authorization") && fields["client_id"] == "synthetic-client" &&
                    (fields.ContainsKey("client_secret") == (method == "client_secret_post")) && !defaults.Order.Contains("authenticate"), "Absent-hook default auth changed.");
            });
        }
    }
    private static async Task ClientAuthenticationOwnership()
    {
        var host = new Host(); var entered = Gate(); var canceled = Gate(); var release = Gate();
        IMcpAuthorizationCodeClientAuthentication? proposal = null; Task? callback = null;
        async Task Hold(IMcpAuthorizationCodeClientAuthentication value, CancellationToken ct)
        { proposal = value; using var registration = ct.Register(() => canceled.TrySetResult()); entered.TrySetResult(); await release.Task; }
        host.Authenticate = (value, ct) => { callback = Hold(value, ct); return new(callback); };
        var inventory = new Inventory(); var flow = new McpAdmittedAuthorizationCodeFlow(host.Dependencies(), new()); Task? work = null; Task? close = null;
        try
        {
            work = flow.CompleteAsync(Profile(), "synthetic"); await inventory.JoinSignal(entered.Task, work);
            close = flow.DisposeAsync().AsTask(); await inventory.JoinSignal(canceled.Task, close);
            Require(!work.IsCompleted && !close.IsCompleted && callback is { IsCompleted: false } && host.Request is null, "Close did not join held custom original.");
            release.TrySetResult(); await inventory.Expected(() => work!, error => error is OperationCanceledException);
            await inventory.Expected(() => close!, error => error is McpOAuthFlowCanceledException);
            Require(work.IsCanceled && close.IsCanceled && host.Request is null && host.Tokens is null, "Held custom cancellation published exchange/tokens.");
            try { proposal!.HeaderGet("Accept"); throw new InvalidOperationException("Canceled custom proposal remained active."); } catch (ObjectDisposedException) { }
        }
        catch (Exception error) { inventory.Failures.Add(error); }
        finally
        {
            release.TrySetResult(); if (callback is not null) await inventory.Cleanup(() => callback);
            if (work is not null) await inventory.Cleanup(() => work); if (close is not null) await inventory.Cleanup(() => close);
            await inventory.Cleanup(() => flow.DisposeAsync().AsTask());
        }
        inventory.ThrowIfFailed();
        await CustomAuthenticationFaults(); await CustomAuthenticationReentry();
    }
    private static async Task CustomAuthenticationFaults()
    {
        foreach (var mode in new[] { 0, 1, 2 })
        {
            var synchronous = mode == 1;
            var left = new IOException("custom-left"); var right = new InvalidOperationException("custom-right");
            var failure = mode != 0 ? (Exception)new OperationCanceledException("faulted-unrequested-custom") : new AggregateException(left, new AggregateException(left, right));
            var original = synchronous ? null : Task.FromException(failure); var host = new Host(); IMcpAuthorizationCodeClientAuthentication? proposal = null;
            host.Authenticate = (value, ct) => { proposal = value; if (synchronous) throw failure; return new(original!); };
            await WithFlow(host, async (flow, inventory) =>
            {
                var work = flow.CompleteAsync(Profile(), "synthetic");
                await inventory.Expected(() => work, error => error is McpOAuthFlowOriginalException carrier && carrier.Phase == "add-client-authentication" &&
                    (synchronous ? carrier.Original is null && ReferenceEquals(carrier.Evidence, failure) : ReferenceEquals(carrier.Original, original) &&
                        carrier.Evidence is AggregateException aggregate && aggregate.InnerExceptions.Count == 1 && ReferenceEquals(aggregate.InnerExceptions[0], failure)) && ReferenceEquals(carrier.Direct, failure));
                Require(work.IsFaulted && !work.IsCanceled && host.Request is null && host.Tokens is null, "Custom fault was canceled or caused later effects.");
                try { proposal!.FormGet("code"); throw new InvalidOperationException("Faulted proposal stayed live."); } catch (ObjectDisposedException) { }
                if (original is not null) await inventory.Expected(() => original, error => ReferenceEquals(error, failure));
            }, expectedCloseFault: true);
        }
        using var canceledSource = new CancellationTokenSource(); var canceledAdmission = new McpAuthorizationCodeCancellationAdmission();
        Task? canceledOriginal = null; CancellationToken callbackToken = default;
        var canceledHost = new Host { Authenticate = (value, ct) =>
        { callbackToken = ct; canceledAdmission.Cancel(canceledSource); canceledOriginal = Task.FromCanceled(ct); return new(canceledOriginal); } };
        var records = new Inventory(); var canceledFlow = new McpAdmittedAuthorizationCodeFlow(canceledHost.Dependencies(), canceledAdmission); Task? canceledWork = null;
        try
        {
            canceledWork = canceledFlow.CompleteAsync(Profile(), "synthetic", canceledSource.Token);
            await records.Expected(() => canceledWork!, error => error is McpOAuthFlowCanceledException carrier && ReferenceEquals(carrier.Original, canceledOriginal) && carrier.CancellationToken == callbackToken);
            Require(canceledWork.IsCanceled && canceledHost.Request is null, "Canceled hook original became faulted or published request.");
            Require(canceledOriginal is not null, "Canceled hook original absent.");
            await records.Expected(() => canceledOriginal!, error => error is OperationCanceledException canceled && canceled.CancellationToken == callbackToken);
        }
        catch (Exception error) { records.Failures.Add(error); }
        finally
        {
            if (canceledOriginal is not null) await records.Cleanup(() => canceledOriginal); if (canceledWork is not null) await records.Cleanup(() => canceledWork);
            await records.Expected(() => canceledFlow.DisposeAsync().AsTask(), error => error is McpOAuthFlowCanceledException carrier && ReferenceEquals(carrier.Original, canceledWork));
        }
        records.ThrowIfFailed();
    }
    private static async Task CustomAuthenticationReentry()
    {
        var ambient = new AsyncLocal<string?> { Value = "custom-hook-frame-free" };
        using var source = new CancellationTokenSource(); var admission = new McpAuthorizationCodeCancellationAdmission();
        var host = new Host(); McpAdmittedAuthorizationCodeFlow? flow = null; var refused = 0;
        var unexpected = new List<Task>(); var cancellationOriginals = new List<Task>(); Task? callback = null;
        using var registration = source.Token.Register(() =>
        {
            Require(ambient.Value == "custom-hook-frame-free", "Custom cancellation did not restore saved EC.");
            foreach (var invoke in new Func<Task>[] { () => flow!.CompleteAsync(Profile(), "synthetic"), () => flow!.DisposeAsync().AsTask() })
            {
                Task? returned = null;
                try { returned = invoke(); throw new InvalidOperationException("Custom own join permitted."); }
                catch (InvalidOperationException error) when (error.Message.Contains("OAuth", StringComparison.Ordinal)) { refused++; }
                finally { if (returned is not null) unexpected.Add(returned); }
            }
        });
        async Task Authenticate(IMcpAuthorizationCodeClientAuthentication proposal, CancellationToken ct)
        {
            await Task.Yield(); var original = admission.CancelAsync(source); cancellationOriginals.Add(original); await original;
            proposal.HeaderSet("Authorization", "Custom after-await");
        }
        host.Authenticate = (proposal, ct) => { callback = Authenticate(proposal, ct); return new(callback); };
        flow = new(host.Dependencies(), admission); var inventory = new Inventory();
        try
        {
            await inventory.Join(() => flow.CompleteAsync(Profile(), "synthetic"));
            Require(refused == 2 && host.Request?.Headers["Authorization"] == "Custom after-await", "After-await normal Register custom reentry or mutation failed.");
        }
        catch (Exception error) { inventory.Failures.Add(error); }
        finally
        {
            foreach (var original in cancellationOriginals) await inventory.Cleanup(() => original);
            if (callback is not null) await inventory.Cleanup(() => callback);
            foreach (var original in unexpected) await inventory.Cleanup(() => original);
            await inventory.Cleanup(() => flow.DisposeAsync().AsTask()); ambient.Value = null;
        }
        inventory.ThrowIfFailed();
    }
    private static async Task MetadataRefusal()
    {
        foreach (var json in new[] { "{\"response_types_supported\":[\"token\"]}",
            "{\"response_types_supported\":[\"code\"],\"code_challenge_methods_supported\":[\"plain\"]}" })
        {
            var host = new Host();
            await WithFlow(host, async (flow, inventory) =>
            {
                await inventory.Expected(() => flow.BeginAsync(Profile(JsonData.Parse(json))), error => error is McpOAuthProtocolException);
                Require(host.Order.Count == 0, "Refused metadata caused effects.");
            }, expectedCloseFault: true);
        }
        var insecure = new Host();
        await WithFlow(insecure, async (flow, inventory) =>
        {
            await inventory.Expected(() => flow.CompleteAsync(Profile(JsonData.Parse("{\"token_endpoint\":\"http://127.0.0.2/token\"}")), "synthetic"),
                error => error is McpOAuthProtocolException { Code: "insecure_endpoint" });
            Require(insecure.Order.Count == 0, "Non-original loopback host reached verifier/exchange effects.");
        }, expectedCloseFault: true);
    }
    private static async Task HeldClose()
    {
        var host = new Host(); var entered = Gate(); var canceled = Gate(); var release = Gate();
        host.Save = async (verifier, ct) =>
        { using var registration = ct.Register(() => canceled.TrySetResult()); entered.TrySetResult(); await release.Task; };
        var inventory = new Inventory(); var flow = new McpAdmittedAuthorizationCodeFlow(host.Dependencies(), new());
        Task<Uri>? work = null; Task? close = null;
        try
        {
            work = flow.BeginAsync(Profile());
            await inventory.JoinSignal(entered.Task, work);
            close = flow.DisposeAsync().AsTask();
            await inventory.JoinSignal(canceled.Task, close);
            Require(!close.IsCompleted && !work.IsCompleted && host.Redirected is null, "Close failed to hold original.");
            release.TrySetResult();
            await inventory.Expected(() => work!, error => error is OperationCanceledException);
            await inventory.Expected(() => close!, error => error is McpOAuthFlowCanceledException);
            Require(work.IsCanceled && close.IsCanceled && host.Redirected is null && release.Task.IsCompletedSuccessfully, "Late redirect or cancellation identity changed.");
            Task<Uri>? stale = null;
            try { stale = flow.BeginAsync(Profile()); throw new InvalidOperationException("Stale flow admitted."); }
            catch (ObjectDisposedException) { }
            finally { if (stale is not null) await inventory.Cleanup(() => stale); }
        }
        catch (Exception error) { inventory.Failures.Add(error); }
        finally
        {
            release.TrySetResult();
            if (work is not null) await inventory.Cleanup(() => work);
            if (close is not null) await inventory.Cleanup(() => close);
            await inventory.Cleanup(() => flow.DisposeAsync().AsTask());
        }
        inventory.ThrowIfFailed();
    }
    private static async Task FaultInventory()
    {
        foreach (var oce in new[] { false, true })
        {
            var host = new Host(); var left = new IOException("left"); var other = new InvalidOperationException("other");
            var failure = oce ? (Exception)new OperationCanceledException("faulted-unrequested") : new AggregateException(left, new AggregateException(left, other));
            var original = Task.FromException<McpAuthorizationCodeResponse>(failure);
            host.Send = (request, ct) => new(original);
            await WithFlow(host, async (flow, inventory) =>
            {
                var work = flow.CompleteAsync(Profile(), "synthetic-code");
                await inventory.Expected(() => work, error => error is McpOAuthFlowOriginalException carrier &&
                    ReferenceEquals(carrier.Original, original) && carrier.Evidence is AggregateException aggregate && aggregate.InnerExceptions.Count == 1 && ReferenceEquals(aggregate.InnerExceptions[0], failure) &&
                    ReferenceEquals(carrier.Direct, failure));
                Require(work.IsFaulted && !work.IsCanceled && host.Tokens is null, "Faulted original became canceled/published.");
                await inventory.Expected(() => original, error => ReferenceEquals(error, failure));
            }, expectedCloseFault: true);
        }
        await CancellationSiblings();
    }
    private static async Task CancellationSiblings()
    {
        using var source = new CancellationTokenSource(); var admission = new McpAuthorizationCodeCancellationAdmission();
        var left = new IOException("exact-cancellation-left"); var right = new InvalidOperationException("exact-cancellation-right");
        var nested = new AggregateException(left, right);
        using var first = source.Token.Register(() => throw left);
        using var second = source.Token.Register(() => throw nested);
        Task? cancellation = null; AggregateException? cancellationAggregate = null; var host = new Host(); var inventory = new Inventory();
        host.Entropy = async ct =>
        {
            await Task.Yield(); cancellation = admission.CancelAsync(source);
            try { await cancellation; } catch { cancellationAggregate = inventory.CaptureAggregate(cancellation); throw; }
            return new byte[32];
        };
        var flow = new McpAdmittedAuthorizationCodeFlow(host.Dependencies(), admission);
        try
        {
            await inventory.Expected(() => flow.BeginAsync(Profile()), error =>
            {
                var cancelAggregate = cancellationAggregate;
                return cancelAggregate is { InnerExceptions.Count: 1 } && cancelAggregate.InnerExceptions[0] is AggregateException siblings &&
                    siblings.InnerExceptions.Count == 2 && ReferenceEquals(siblings.InnerExceptions[0], nested) && ReferenceEquals(siblings.InnerExceptions[1], left) &&
                    error is McpOAuthFlowOriginalException { Phase: "entropy", Evidence: AggregateException evidence } carrier &&
                    evidence.InnerExceptions.Count == 1 && ReferenceEquals(evidence.InnerExceptions[0], siblings) && ReferenceEquals(carrier.Direct, siblings);
            });
            Require(host.Redirected is null && host.Verifier is null, "Cancellation sibling fault allowed publication.");
            if (cancellation is not null) await inventory.Expected(() => cancellation, error => error is AggregateException siblings &&
                siblings.InnerExceptions.Count == 2 && ReferenceEquals(siblings.InnerExceptions[0], nested) && ReferenceEquals(siblings.InnerExceptions[1], left));
        }
        catch (Exception error) { inventory.Failures.Add(error); }
        finally
        {
            if (cancellation is not null) await inventory.Cleanup(() => cancellation);
            await inventory.Expected(() => flow.DisposeAsync().AsTask(), inventory.MatchesCloseFault);
        }
        inventory.ThrowIfFailed();
    }
    private static async Task CancellationAndMalformed()
    {
        foreach (var duringSave in new[] { false, true }) await NormalRegister(duringSave);
        foreach (var body in new[] { "{\"access_token\":\"a\",\"token_type\":\"Bearer\",\"refresh_token\":\"\"}",
            "{\"access_token\":\"a\",\"token_type\":\"Bearer\",\"expires_in\":\"bad\"}",
            "{\"error\":\"invalid_grant\",\"error_description\":\"synthetic rejected\"}" })
        {
            var malformed = new Host { Send = (request, ct) => ValueTask.FromResult(new McpAuthorizationCodeResponse(200, body)) };
            await WithFlow(malformed, async (flow, records) =>
            { await records.Expected(() => flow.CompleteAsync(Profile(), "synthetic"), error => error is McpOAuthProtocolException); Require(malformed.Tokens is null, "Malformed/OAuth error published tokens."); }, expectedCloseFault: true);
        }
    }
    private static async Task NormalRegister(bool duringSave)
    {
        // Registration captures an earlier nondefault frame-free EC. Cancellation happens after awaited callback work.
        var ambient = new AsyncLocal<string?> { Value = "frame-free-registration" };
        using var source = new CancellationTokenSource(); var admission = new McpAuthorizationCodeCancellationAdmission();
        var host = new Host(); McpAdmittedAuthorizationCodeFlow? owner = null; var rejected = 0;
        using var secondSource = new CancellationTokenSource(); var unrelatedAdmission = new McpAuthorizationCodeCancellationAdmission();
        var unrelatedHost = new Host(); var unrelated = new McpAdmittedAuthorizationCodeFlow(unrelatedHost.Dependencies(), unrelatedAdmission);
        var unrelatedOriginals = new List<Task>();
        var unexpectedOriginals = new List<Task>(); var cancellationOriginals = new List<Task>();
        void RefuseAncestor(Func<Task> invoke)
        {
            Task? unexpected = null;
            try { unexpected = invoke(); throw new InvalidOperationException("Ancestor entry permitted."); }
            catch (InvalidOperationException error) when (error.Message.Contains("OAuth", StringComparison.Ordinal)) { rejected++; }
            finally { if (unexpected is not null) unexpectedOriginals.Add(unexpected); }
        }
        using var secondRegistration = secondSource.Token.Register(() =>
        {
            Require(ambient.Value == "frame-free-registration", "Second cancellation did not restore frame-free EC.");
            RefuseAncestor(() => owner!.BeginAsync(Profile()));
            RefuseAncestor(() => owner!.DisposeAsync().AsTask());
        });
        unrelatedHost.Entropy = async ct =>
        {
            RefuseAncestor(() => owner!.BeginAsync(Profile()));
            await Task.Yield();
            var secondOriginal = unrelatedAdmission.CancelAsync(secondSource); cancellationOriginals.Add(secondOriginal); await secondOriginal;
            return Convert.FromBase64String("dBjftJeZ4CVP+mB92K27uhbUJU1p1r/wW1gFWFOEjXk=");
        };
        using var registration = source.Token.Register(() =>
        {
            Require(ambient.Value == "frame-free-registration", "Expected captured registration EC.");
            try { owner!.BeginAsync(Profile()).GetAwaiter().GetResult(); throw new InvalidOperationException("Own join permitted."); }
            catch (InvalidOperationException error) when (error.Message.Contains("OAuth", StringComparison.Ordinal)) { rejected++; }
            var actualUnrelated = unrelated.BeginAsync(Profile()); unrelatedOriginals.Add(actualUnrelated); actualUnrelated.GetAwaiter().GetResult();
            try { owner!.DisposeAsync().AsTask().GetAwaiter().GetResult(); throw new InvalidOperationException("Own close permitted."); }
            catch (InvalidOperationException error) when (error.Message.Contains("OAuth", StringComparison.Ordinal)) { rejected++; }
        });
        async Task Cancel()
        {
            await Task.Yield(); var original = admission.CancelAsync(source); cancellationOriginals.Add(original); await original;
        }
        if (duringSave) host.Save = async (value, ct) => await Cancel();
        else host.Entropy = async ct => { await Cancel(); return Convert.FromBase64String("dBjftJeZ4CVP+mB92K27uhbUJU1p1r/wW1gFWFOEjXk="); };
        owner = new(host.Dependencies(), admission); var inventory = new Inventory();
        try { await inventory.Join(() => owner.BeginAsync(Profile())); Require(rejected == 5 && host.Redirected is not null && unrelatedHost.Redirected is not null, "After-await physical ancestor joins or unrelated admission changed."); }
        catch (Exception error) { inventory.Failures.Add(error); }
        finally
        { foreach (var original in cancellationOriginals) await inventory.Cleanup(() => original); foreach (var original in unrelatedOriginals) await inventory.Cleanup(() => original);
            foreach (var original in unexpectedOriginals) await inventory.Cleanup(() => original);
            await inventory.Cleanup(() => owner.DisposeAsync().AsTask()); await inventory.Cleanup(() => unrelated.DisposeAsync().AsTask()); ambient.Value = null; }
        inventory.ThrowIfFailed();
    }
    private static async Task WithFlow(Host host, Func<McpAdmittedAuthorizationCodeFlow, Inventory, Task> body, bool expectedCloseFault = false)
    {
        var inventory = new Inventory(); var flow = new McpAdmittedAuthorizationCodeFlow(host.Dependencies(), new());
        try { await body(flow, inventory); } catch (Exception error) { inventory.Failures.Add(error); }
        finally
        {
            if (expectedCloseFault) await inventory.Expected(() => flow.DisposeAsync().AsTask(), inventory.MatchesCloseFault);
            else await inventory.Cleanup(() => flow.DisposeAsync().AsTask());
        }
        inventory.ThrowIfFailed();
    }
    private sealed class Inventory
    {
        internal readonly List<Exception> Failures = [];
        private readonly HashSet<Task> joined = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<Task, AggregateException?> aggregates = new(ReferenceEqualityComparer.Instance);
        internal AggregateException? CaptureAggregate(Task original)
        { if (!aggregates.TryGetValue(original, out var aggregate)) { aggregate = original.Exception; aggregates.Add(original, aggregate); } return aggregate; }
        internal readonly List<(Task? Task, AggregateException? Aggregate, Exception? Direct)> Originals = [];
        private void Retain(Task? original, AggregateException? aggregate, Exception? direct)
        { Originals.Add((original, aggregate, direct)); feed.Value?.Add(new(original, aggregate, direct)); }
        internal async Task<T> Join<T>(Func<Task<T>> invoke)
        {
            Task<T>? original = null;
            try { original = invoke(); var result = await original; joined.Add(original); Retain(original, null, null); return result; }
            catch (Exception direct) { Record(original, direct); throw; }
        }
        internal async Task Expected(Func<Task> invoke, Func<Exception, bool> accept)
        {
            Task? original = null;
            try { original = invoke(); await original; joined.Add(original); Retain(original, null, null); Failures.Add(new InvalidOperationException("Expected failure absent.")); }
            catch (Exception direct)
            {
                var aggregate = original is null ? null : CaptureAggregate(original); if (original is not null) joined.Add(original); Retain(original, aggregate, direct);
                bool accepted = false; try { accepted = accept(direct); } catch (Exception predicate) { Failures.Add(predicate); }
                if (!accepted) { if (aggregate is not null) Failures.Add(aggregate); Failures.Add(direct); }
            }
        }
        private void Record(Task? original, Exception direct)
        { if (original is not null) joined.Add(original); var aggregate = original is null ? null : CaptureAggregate(original); Retain(original, aggregate, direct); if (aggregate is not null) Failures.Add(aggregate); }
        internal async Task Cleanup(Func<Task> invoke)
        {
            Task? original = null;
            try { original = invoke(); if (joined.Contains(original)) return; await original; joined.Add(original); Retain(original, null, null); }
            catch (Exception direct) { Record(original, direct); Failures.Add(direct); }
        }
        internal async Task JoinSignal(Task signal, Task operation)
        {
            using var diagnostic = new CancellationTokenSource();
            var deadline = Task.Delay(TimeSpan.FromSeconds(10), diagnostic.Token);
            try
            {
                var winner = await Task.WhenAny(signal, operation, deadline);
                if (winner == deadline) throw new TimeoutException("Diagnostic held signal deadline; caller finally releases and joins actual originals.");
                if (winner != signal) { await operation; throw new InvalidOperationException("Operation settled before required held signal."); }
                await signal;
            }
            finally { diagnostic.Cancel(); try { await deadline; } catch (OperationCanceledException) when (deadline.IsCanceled) { } }
        }
        internal bool MatchesCloseFault(Exception error)
        {
            if (error is not McpOAuthFlowOriginalException { Phase: "close-active", Original: { } original, Evidence: AggregateException evidence } carrier) return false;
            var records = Originals.Where(record => ReferenceEquals(record.Task, original) && record.Aggregate is not null).ToArray();
            return records.Length == 1 && ReferenceEquals(carrier.Direct, records[0].Direct) &&
                evidence.InnerExceptions.SequenceEqual(records[0].Aggregate!.InnerExceptions, ReferenceEqualityComparer.Instance);
        }
        internal void ThrowIfFailed()
        { if (Failures.Count != 0) throw new AggregateException("OAuth code fixture original/cleanup inventory.", Failures); }
    }
    private static Dictionary<string, string> Fields(string query) => query.Split('&').ToDictionary(p => Decode(p.Split('=')[0]), p => Decode(p[(p.IndexOf('=') + 1)..]), StringComparer.Ordinal);
    private static string Decode(string text) => Uri.UnescapeDataString(text.Replace('+', ' '));
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
