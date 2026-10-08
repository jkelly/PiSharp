using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using PiSharp.Cli.Mcp;
using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.Authentication;
using PiSharp.Extensions.Mcp.Configuration;
using PiSharp.Extensions.Mcp.Runtime;
using PiSharp.Extensions.Mcp.Transport;
using PiSharp.Extensions.Runtime.Mcp.Authentication;
using PiSharp.Extensions.Runtime.Mcp.Transport;
using PiSharp.Extensions.Runtime.Mcp;

// SOURCE ONLY: explicitly supplied in-memory HTTP handler and synthetic token strings; no network.
internal static class McpAdmittedHttpAuthenticationTests
{
    internal sealed record OriginalEvidence(string Phase, Task Original, AggregateException? Aggregate, Exception? Direct);
    private static readonly ConcurrentQueue<OriginalEvidence> originals = new();
    private static readonly ConcurrentDictionary<Task, OriginalEvidence> joined = new(ReferenceEqualityComparer.Instance);
    internal static OriginalEvidence[] CapturedOriginals => originals.ToArray();
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        ("mcp-http-auth.actual-channel-401-discards-response-and-rereads-token-for-exact-retry", ChannelRetry),
        ("mcp-http-auth.ordinary-403-never-invokes-challenge-or-retries", OrdinaryForbidden),
        ("mcp-http-auth.insufficient-scope-403-retries-once-only", ScopeBound),
        ("mcp-http-auth.stop-joins-held-token-and-callback-reentry-refuses-before-http", HeldToken),
        ("mcp-http-auth.stop-joins-held-challenge-before-discard-and-no-late-retry", HeldChallenge),
        ("mcp-http-auth.exact-multifault-faulted-oce-and-discard-failures-survive", Faults)
    ];
    private static readonly Uri Endpoint = new("https://inert.invalid/mcp");
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Require(bool value) { if (!value) throw new IOException("Admitted HTTP authentication control failed."); }
    private static async Task<Exception?> Join(string phase, Task original)
    {
        if (joined.TryGetValue(original, out var prior)) return prior.Aggregate ?? prior.Direct;
        Exception? direct = null; try { await original.ConfigureAwait(false); } catch (Exception error) { direct = error; }
        var aggregate = original.IsFaulted ? original.Exception : null; var record = new OriginalEvidence(phase, original, aggregate, direct);
        joined.TryAdd(original, record); originals.Enqueue(record); return aggregate ?? direct;
    }
    private static void Throw(List<Exception> errors)
    { if (errors.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(errors[0]).Throw(); if (errors.Count > 1) throw new AggregateException(errors); }
    private static T? Find<T>(Exception? root, Func<T, bool> predicate) where T : Exception
    {
        if (root is null) return null;
        var queue = new Queue<Exception>(); var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance); queue.Enqueue(root);
        var edges = 0;
        while (queue.Count > 0)
        {
            var error = queue.Dequeue(); if (!seen.Add(error)) continue; if (seen.Count > 1024) throw new IOException("Fixture identity graph node bound reached."); if (error is T target && predicate(target)) return target;
            IEnumerable<Exception> children = error is AggregateException all ? all.InnerExceptions : error.InnerException is { } inner ? [inner] : [];
            foreach (var child in children) { if (++edges > 4096) throw new IOException("Fixture identity graph edge bound reached."); queue.Enqueue(child); }
        }
        return null;
    }
    private static async Task WaitSignal(Task signal, Task triggering)
    {
        var errors = new List<Exception>(); var source = new CancellationTokenSource(); var deadline = Task.Delay(TimeSpan.FromSeconds(10), source.Token);
        try
        {
            var winner = await Task.WhenAny(signal, triggering, deadline);
            if (ReferenceEquals(winner, triggering) && !signal.IsCompleted) throw new IOException("Authentication original settled before callback entry.", triggering.IsFaulted ? triggering.Exception : null);
            if (ReferenceEquals(winner, deadline)) throw new TimeoutException("Authentication callback entry bound reached.");
            await signal;
        }
        catch (Exception error) { errors.Add(error); }
        finally
        {
            try { var cancel = source.CancelAsync(); var error = await Join("entry-deadline-cancel", cancel); if (error is not null) errors.Add(error); } catch (Exception error) { errors.Add(error); }
            var deadlineError = await Join("entry-deadline-original", deadline); if (deadline.IsFaulted && deadlineError is not null) errors.Add(deadlineError);
            try { source.Dispose(); } catch (Exception error) { errors.Add(error); }
        }
        Throw(errors);
    }
    private sealed class Content(string value, Exception? disposeFailure = null) : StringContent(value, Encoding.UTF8, "application/json")
    {
        internal bool Disposed;
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); if (disposing && disposeFailure is not null) throw disposeFailure; }
    }
    private sealed class Server(params HttpStatusCode[] statuses) : HttpMessageHandler
    {
        internal int Calls; internal readonly List<string> Bodies = [], Tokens = [];
        internal readonly List<Content> Responses = [];
        internal string? Challenge; internal Exception? DiscardFailure;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Require(request.RequestUri == Endpoint);
            var body = await (request.Content ?? throw new IOException("No request content.")).ReadAsStringAsync(token);
            Bodies.Add(body); Tokens.Add(request.Headers.Authorization?.ToString() ?? "");
            var ordinal = Calls++; var status = statuses[Math.Min(ordinal, statuses.Length - 1)];
            var value = JsonData.Parse(body).Value;
            if (!value.TryGetProperty("id", out var requestId)) return new HttpResponseMessage(HttpStatusCode.Accepted);
            var id = requestId.GetRawText();
            var result = value.GetProperty("method").GetString() == "initialize"
                ? "{\"protocolVersion\":\"2025-11-25\",\"capabilities\":{},\"serverInfo\":{\"name\":\"offline-auth\",\"version\":\"1\"}}" : "{}";
            var content = new Content("{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":" + result + "}", ordinal == 0 ? DiscardFailure : null); Responses.Add(content);
            var response = new HttpResponseMessage(status) { Content = content };
            if (Challenge is not null) response.Headers.TryAddWithoutValidation("WWW-Authenticate", Challenge);
            return response;
        }
    }
    private sealed class Fixture(Server server, McpAdmittedHttpAuthentication auth)
    {
        internal readonly Server Server = server;
        internal readonly HttpClient Client = new(server, false);
        internal readonly HttpRequestMessage Request = new(HttpMethod.Post, Endpoint) { Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}", Encoding.UTF8, "application/json") };
        internal IMcpAdmittedHttpRequestOperation? Operation; internal Task<HttpResponseMessage>? Send; internal Task? Stop;
        internal HttpResponseMessage? Response; internal Exception? SendError; private bool settled;
        internal void Start()
        {
            var factory = McpAuthenticatedHttpRequestFactory.Create(AdmittedHttpClientRequestFactory.Create(Client), Endpoint, auth);
            Operation = factory(Request); Send = Operation.SendAsync(default).AsTask();
        }
        internal async Task Settle()
        {
            if (Send is null || settled) return; settled = true;
            SendError = await Join("authenticated-send-original", Send); if (Send.IsCompletedSuccessfully) Response = Send.Result;
        }
        internal async Task Cleanup(List<Exception> errors, bool expectedSendFailure = false, bool expectedStopFailure = false)
        {
            try { if (Operation is not null) { Stop ??= Operation.StopAsync(); var stopError = await Join("authenticated-stop-original", Stop); if (stopError is not null && (!expectedStopFailure || errors.Count > 0)) errors.Add(stopError); } }
            catch (Exception error) { errors.Add(error); }
            if (Send is not null && !settled) await Settle();
            if (SendError is not null && (!expectedSendFailure || errors.Count > 0)) errors.Add(SendError);
            try { Response?.Dispose(); } catch (Exception error) { errors.Add(error); }
            try { Request.Dispose(); } catch (Exception error) { errors.Add(error); }
            try { Client.Dispose(); } catch (Exception error) { errors.Add(error); }
            try { Server.Dispose(); } catch (Exception error) { errors.Add(error); }
        }
    }
    private static async Task ChannelRetry()
    {
        var server = new Server(HttpStatusCode.Unauthorized, HttpStatusCode.OK); var client = new HttpClient(server, false);
        var tokenCalls = 0; var challengeCalls = 0; var old = Task.FromResult<string?>("synthetic-old"); var fresh = Task.FromResult<string?>("synthetic-new");
        var challenge = Task.CompletedTask; var errors = new List<Exception>(); McpServerRuntime? runtime = null;
        var auth = new McpAdmittedHttpAuthentication(_ => { if (++tokenCalls > 1) Require(server.Responses[0].Disposed); return new(tokenCalls == 1 ? old : fresh); },
            (context, _) => { Require(context.ServerUrl == Endpoint && context.RejectedToken == "synthetic-old" && !server.Responses[0].Disposed); challengeCalls++; return new(challenge); });
        try
        {
            var entry = new McpServerEntry("auth", McpConfigurationReader.Validate("auth", JsonData.Parse("{\"url\":\"https://inert.invalid/mcp\"}").Value).Config!, "admitted-offline", McpConfigurationScope.Extension);
            var options = new McpRuntimeOptions(1, "0.99.1");
            var factory = AdmittedMcpHttpChannelFactory.Create(entry, new(Endpoint, ImmutableDictionary<string, string>.Empty, new(false, false)), client, options, authentication: auth);
            runtime = new(entry, options, factory, (publication, _) => ValueTask.FromResult(new McpCatalogPublicationReceipt(publication.Current.Generation, publication.Current.Revision, true)));
            var connect = runtime.ConnectAsync(); var error = await Join("auth-runtime-connect", connect); if (error is not null) throw error;
            Require(runtime.Snapshot.Catalog.Connected && server.Calls == 3 && tokenCalls == 3 && challengeCalls == 1 && server.Bodies[0] == server.Bodies[1] &&
                server.Tokens.SequenceEqual(new[] { "Bearer synthetic-old", "Bearer synthetic-new", "Bearer synthetic-new" }) && server.Responses[0].Disposed);
        }
        catch (Exception error) { errors.Add(error); }
        finally
        {
            if (runtime is not null) try { var close = runtime.DisposeAsync().AsTask(); var error = await Join("auth-runtime-close", close); if (error is not null) errors.Add(error); } catch (Exception error) { errors.Add(error); }
            foreach (var original in new Task[] { old, fresh, challenge }) { var error = await Join("admitted-callback-original", original); if (error is not null) errors.Add(error); }
            try { client.Dispose(); } catch (Exception error) { errors.Add(error); } try { server.Dispose(); } catch (Exception error) { errors.Add(error); }
        }
        Throw(errors);
    }
    private static Task OrdinaryForbidden() => Rejection(false);
    private static Task ScopeBound() => Rejection(true);
    private static async Task Rejection(bool scope)
    {
        var server = new Server(HttpStatusCode.Forbidden) { Challenge = scope ? "Bearer error=\"insufficient_scope\"" : "Bearer error=\"invalid_token\"" };
        var tokens = 0; var challenges = 0; var callback = Task.CompletedTask;
        var fixture = new Fixture(server, new(_ => { tokens++; return ValueTask.FromResult<string?>("synthetic"); }, (_, _) => { challenges++; return new(callback); }));
        var errors = new List<Exception>();
        try { fixture.Start(); await fixture.Settle(); Require(fixture.SendError is null && fixture.Response?.StatusCode == HttpStatusCode.Forbidden && server.Calls == (scope ? 2 : 1) && tokens == server.Calls && challenges == (scope ? 1 : 0)); }
        catch (Exception error) { errors.Add(error); }
        finally { await fixture.Cleanup(errors); var callbackError = await Join("rejection-callback-original", callback); if (callbackError is not null) errors.Add(callbackError); }
        Throw(errors);
    }
    private static async Task HeldToken() { await Held(false); await LateCancellation(); }
    private static Task HeldChallenge() => Held(true);
    private static async Task Held(bool challenge)
    {
        var server = new Server(HttpStatusCode.Unauthorized); var entered = Gate(); var token = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var unauthorized = Gate(); var cancellationObserved = Gate(); CancellationTokenRegistration registration = default;
        Fixture? fixture = null; var refused = false; Task? unexpectedStop = null; CancellationToken owned = default;
        void Enter(CancellationToken value) { owned = value; registration = value.Register(() => cancellationObserved.TrySetResult()); try { unexpectedStop = fixture!.Operation!.StopAsync(); } catch (InvalidOperationException) { refused = true; } entered.TrySetResult(); }
        var auth = new McpAdmittedHttpAuthentication(value => { if (!challenge) Enter(value); return challenge ? ValueTask.FromResult<string?>("synthetic") : new(token.Task); },
            (_, value) => { Enter(value); return new(unauthorized.Task); });
        fixture = new(server, auth); var errors = new List<Exception>();
        try
        {
            fixture.Start(); await WaitSignal(entered.Task, fixture.Send!); fixture.Stop = fixture.Operation!.StopAsync();
            await WaitSignal(cancellationObserved.Task, fixture.Stop);
            Require(refused && !fixture.Stop.IsCompleted && owned.IsCancellationRequested && (!challenge || !server.Responses[0].Disposed));
        }
        catch (Exception error) { errors.Add(error); }
        finally
        {
            token.TrySetResult("synthetic-late"); unauthorized.TrySetResult();
            if (!challenge) { var tokenError = await Join("held-token-original", token.Task); if (tokenError is not null) errors.Add(tokenError); }
            else { var unauthorizedError = await Join("held-unauthorized-original", unauthorized.Task); if (unauthorizedError is not null) errors.Add(unauthorizedError); }
            if (unexpectedStop is not null) { var error = await Join("unexpected-self-stop-original", unexpectedStop); if (error is not null) errors.Add(error); }
            await fixture.Cleanup(errors, expectedSendFailure: true);
            try { registration.Dispose(); } catch (Exception error) { errors.Add(error); }
        }
        try { Require(fixture.Send is { IsCanceled: true } && server.Calls == (challenge ? 1 : 0) && (!challenge || server.Responses[0].Disposed)); }
        catch (Exception error) { errors.Add(error); if (fixture.SendError is not null) errors.Add(fixture.SendError); }
        Throw(errors);
    }
    private static async Task LateCancellation()
    {
        Fixture? outer = null, inner = null, unrelated = null;
        var registrations = new List<CancellationTokenRegistration>(); var unexpectedStops = new List<Task>();
        var selfRefusals = 0; var ancestorRefusals = 0; var errors = new List<Exception>();
        void Refuse(Fixture target, bool ancestor)
        {
            // Do not wait an unexpectedly accepted task: recording it permits finally to join it
            // after the callback unwinds, while the required immediate refusal still fails the control.
            try { unexpectedStops.Add(target.Operation!.StopAsync()); }
            catch (InvalidOperationException) { if (ancestor) ancestorRefusals++; else selfRefusals++; }
        }
        outer = new(new Server(HttpStatusCode.OK), new(value =>
        {
            // Ordinary Register captures the token Invoke context, which is inactive by later Stop.
            registrations.Add(value.Register(() =>
            {
                Refuse(outer!, false);
                inner!.Stop = inner.Operation!.StopAsync();
                var error = Join("nested-inner-stop-original", inner.Stop!).GetAwaiter().GetResult();
                if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
            }));
            return ValueTask.FromResult<string?>("synthetic");
        }));
        inner = new(new Server(HttpStatusCode.OK), new(value =>
        {
            registrations.Add(value.Register(() =>
            {
                Refuse(inner!, false); Refuse(outer!, true);
                unrelated!.Stop = unrelated.Operation!.StopAsync();
                var error = Join("unrelated-stop-original", unrelated.Stop!).GetAwaiter().GetResult();
                if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
            }));
            return ValueTask.FromResult<string?>("synthetic");
        }));
        unrelated = new(new Server(HttpStatusCode.OK), new(_ => ValueTask.FromResult<string?>("synthetic")));
        try
        {
            foreach (var fixture in new[] { outer!, inner!, unrelated! }) { fixture.Start(); await fixture.Settle(); Require(fixture.Send is { IsCompletedSuccessfully: true }); }
            outer.Stop = outer.Operation!.StopAsync(); var error = await Join("late-normal-register-stop-original", outer.Stop); if (error is not null) throw error;
            Require(selfRefusals == 2 && ancestorRefusals == 1 && unexpectedStops.Count == 0 && inner.Stop is { IsCompletedSuccessfully: true } && unrelated.Stop is { IsCompletedSuccessfully: true });
            // Shared ancestry is deactivated after cancellation; ordinary later joins remain valid.
            Require(ReferenceEquals(outer.Stop, outer.Operation!.StopAsync()) && ReferenceEquals(inner.Stop, inner.Operation!.StopAsync()));
        }
        catch (Exception error) { errors.Add(error); }
        finally
        {
            foreach (var fixture in new[] { outer!, inner!, unrelated! }) if (fixture is not null) await fixture.Cleanup(errors);
            foreach (var original in unexpectedStops) { var error = await Join("unexpected-late-callback-stop", original); if (error is not null) errors.Add(error); }
            foreach (var registration in registrations) try { registration.Dispose(); } catch (Exception error) { errors.Add(error); }
        }
        Throw(errors);
    }
    private static async Task LateCancellationFaults()
    {
        var leaf = new IOException("Cancellation shared leaf"); var nested = new AggregateException(leaf, leaf);
        var registrations = new List<CancellationTokenRegistration>(); var errors = new List<Exception>();
        var fixture = new Fixture(new Server(HttpStatusCode.OK), new(value =>
        {
            registrations.Add(value.Register(() => throw leaf));
            registrations.Add(value.Register(() => throw nested));
            return ValueTask.FromResult<string?>("synthetic");
        }));
        try
        {
            fixture.Start(); await fixture.Settle(); Require(fixture.Send is { IsCompletedSuccessfully: true });
            fixture.Stop = fixture.Operation!.StopAsync(); var error = await Join("faulted-cancellation-stop-original", fixture.Stop);
            // A fixed, fully acknowledged six-node/seven-edge DAG: stop aggregate -> native
            // cancel wrapper -> captured cancel Task aggregate -> synchronous Cancel aggregate
            // -> exactly the two thrown callbacks, with the nested aggregate's duplicate leaf edges.
            // Merely finding expected leaves would incorrectly accept an additional fault sibling.
            var stopAggregate = error as AggregateException;
            var cancellation = stopAggregate is { InnerExceptions.Count: 1 }
                ? stopAggregate.InnerExceptions[0] as McpHttpAuthenticationOriginalException : null;
            var evidence = cancellation?.Evidence as AggregateException;
            var direct = cancellation?.Direct as AggregateException;
            var actualCancelAggregate = cancellation?.Original is { IsFaulted: true } actual ? actual.Exception : null;
            Require(fixture.Stop.IsFaulted && !fixture.Stop.IsCanceled && cancellation is { Phase: "cancel" } &&
                cancellation.Original is { IsFaulted: true, IsCanceled: false } &&
                ReferenceEquals(cancellation.InnerException, evidence) && evidence is { InnerExceptions.Count: 1 } &&
                ReferenceEquals(evidence.InnerExceptions[0], direct) && actualCancelAggregate is { InnerExceptions.Count: 1 } &&
                ReferenceEquals(actualCancelAggregate.InnerExceptions[0], direct) && direct is { InnerExceptions.Count: 2 } &&
                ((ReferenceEquals(direct.InnerExceptions[0], leaf) && ReferenceEquals(direct.InnerExceptions[1], nested)) ||
                 (ReferenceEquals(direct.InnerExceptions[0], nested) && ReferenceEquals(direct.InnerExceptions[1], leaf))) &&
                nested.InnerExceptions.Count == 2 && ReferenceEquals(nested.InnerExceptions[0], leaf) &&
                ReferenceEquals(nested.InnerExceptions[1], leaf) && leaf.InnerException is null);
        }
        catch (Exception error) { errors.Add(error); }
        finally
        {
            await fixture.Cleanup(errors, expectedStopFailure: true);
            foreach (var registration in registrations) try { registration.Dispose(); } catch (Exception error) { errors.Add(error); }
        }
        Throw(errors);
    }
    private static async Task Faults()
    {
        var leaf = new IOException("shared callback leaf"); var nested = new AggregateException(leaf, leaf);
        var outer = new AggregateException(leaf, nested); var faultedOce = new OperationCanceledException("Faulted token is not canceled.");
        foreach (var source in new Exception[] { outer, faultedOce })
        {
            var original = Task.FromException<string?>(source); var fixture = new Fixture(new Server(HttpStatusCode.OK), new(_ => new(original)));
            var errors = new List<Exception>();
            try { fixture.Start(); await fixture.Settle(); Require(fixture.Send is { IsFaulted: true, IsCanceled: false } && fixture.Server.Calls == 0 &&
                Find<McpHttpAuthenticationOriginalException>(fixture.SendError, error => error.Phase == "token" && ReferenceEquals(error.Original, original) && Find<Exception>(error.Evidence, item => ReferenceEquals(item, source)) is not null) is not null); }
            catch (Exception error) { errors.Add(error); }
            finally { var error = await Join("faulted-token-original", original); if (errors.Count > 0 && error is not null) errors.Add(error); await fixture.Cleanup(errors, expectedSendFailure: true); }
            Throw(errors);
        }
        var callbackFault = Task.FromException(outer); var discardFault = new IOException("Rejected response discard failed.");
        var server = new Server(HttpStatusCode.Unauthorized) { DiscardFailure = discardFault };
        var combined = new Fixture(server, new(_ => ValueTask.FromResult<string?>("synthetic"), (_, _) => new(callbackFault))); var failures = new List<Exception>();
        try { combined.Start(); await combined.Settle(); Require(combined.Send is { IsFaulted: true } && server.Calls == 1 && server.Responses[0].Disposed &&
            Find<McpHttpAuthenticationOriginalException>(combined.SendError, error => error.Phase == "unauthorized" && ReferenceEquals(error.Original, callbackFault)) is not null &&
            Find<Exception>(combined.SendError, error => ReferenceEquals(error, discardFault)) is not null); }
        catch (Exception error) { failures.Add(error); }
        finally { var error = await Join("faulted-challenge-original", callbackFault); if (failures.Count > 0 && error is not null) failures.Add(error); await combined.Cleanup(failures, expectedSendFailure: true); }
        Throw(failures);
        await LateCancellationFaults();
    }
}
