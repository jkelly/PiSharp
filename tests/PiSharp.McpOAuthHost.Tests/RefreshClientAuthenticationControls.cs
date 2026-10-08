using System.Collections.Concurrent;
using System.Net;
using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.Authentication;
using PiSharp.Extensions.Runtime.Mcp.Authentication;

namespace PiSharp.McpOAuthHost.Tests;

public sealed record McpOAuthRefreshControlOriginal(Task? Original, AggregateException? Aggregate, Exception? Direct);

/// <summary>Offline borrowed-callback controls. No store, HTTP client, browser, credential or process acquisition.</summary>
public static partial class RefreshClientAuthenticationControls
{
    private static readonly AsyncLocal<List<McpOAuthRefreshControlOriginal>?> feed = new();
    public static IEnumerable<(string Name, Func<Task> Run)> Cases(Action<McpOAuthRefreshControlOriginal>? retain = null) =>
    [
        ("oauth-refresh.custom-hook-replaces-default-before-exchange-and-retires-proposal", () => RunCase(MutationAndDefaults, retain)),
        ("oauth-refresh.held-shared-hook-original-cancellation-joins-and-fences-token-effects", () => RunCase(HeldOriginal, retain)),
        ("oauth-refresh.custom-hook-shared-faults-and-faulted-oce-complete-original-graphs", () => RunCase(Faults, retain)),
        ("oauth-refresh.after-await-normal-register-cross-owner-ancestor-refusal", () => RunCase(Ancestry, retain))
    ];
    private static async Task RunCase(Func<Task> invoke, Action<McpOAuthRefreshControlOriginal>? retain)
    {
        var previous = feed.Value; var records = new List<McpOAuthRefreshControlOriginal>(); feed.Value = records;
        Task? original = null; var failures = new List<Exception>(); var reportingFailed = false;
        try { original = invoke(); await original; records.Add(new(original, null, null)); }
        catch (Exception direct) { var aggregate = original?.Exception; records.Add(new(original, aggregate, direct)); if (aggregate is not null) failures.Add(aggregate); failures.Add(direct); }
        finally
        {
            feed.Value = previous;
            foreach (var record in records) try { retain?.Invoke(record); } catch (Exception error) { failures.Add(error); reportingFailed = true; }
            if (reportingFailed) foreach (var record in records) { if (record.Aggregate is not null) failures.Add(record.Aggregate); if (record.Direct is not null) failures.Add(record.Direct); }
        }
        if (failures.Count != 0) throw new AggregateException("Refresh custom-auth criteria/original/report inventory.", failures);
    }
    private static readonly Uri Server = new("https://offline.invalid/resource/mcp");
    private const string Metadata = "{\"issuer\":\"https://issuer.invalid/tenant\",\"authorization_endpoint\":\"https://issuer.invalid/authorize\",\"token_endpoint\":\"https://issuer.invalid/token\",\"response_types_supported\":[\"code\"],\"token_endpoint_auth_methods_supported\":[\"client_secret_basic\"]}";
    private sealed class Host : IMcpAdmittedOAuthStateStore
    {
        internal readonly ConcurrentQueue<Task> Originals = new();
        internal readonly ConcurrentQueue<string> Order = new();
        internal readonly List<McpOAuthExchangeRequest> Requests = [];
        internal McpOAuthState Current = new(Server.AbsoluteUri, Tokens: new("old", "Bearer", RefreshToken: "synthetic-refresh"), Discovery:
            JsonData.Parse("{\"authorizationServerUrl\":\"https://issuer.invalid/tenant\",\"authorizationServerMetadata\":" + Metadata +
                ",\"resourceMetadata\":{\"resource\":\"https://offline.invalid/resource\",\"authorization_servers\":[\"https://issuer.invalid/tenant\"]}}"));
        internal int TokenSaves, Loads;
        public ValueTask<McpOAuthState?> LoadAsync()
        { Interlocked.Increment(ref Loads); var original = Task.FromResult<McpOAuthState?>(Current); Originals.Enqueue(original); return new(original); }
        public ValueTask SaveAsync(McpOAuthState value)
        { if (value.Tokens?.AccessToken == "new") { TokenSaves++; Order.Enqueue("save-tokens"); } else Order.Enqueue("save-discovery"); Current = value; var original = Task.CompletedTask; Originals.Enqueue(original); return new(original); }
        internal ValueTask<McpOAuthExchangeResponse> Exchange(McpOAuthExchangeRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Requests.Add(request); Order.Enqueue("exchange");
            var original = Task.FromResult(new McpOAuthExchangeResponse(200, "{\"access_token\":\"new\",\"token_type\":\"Bearer\"}")); Originals.Enqueue(original); return new(original);
        }
        internal async Task JoinAll(Inventory inventory) { foreach (var original in Originals) await inventory.Cleanup(() => original); }
    }
    private static McpHttpUnauthorizedContext Context(HttpResponseMessage response) => new(response, Server, "old");
    private static McpAdmittedOAuthRefreshAdapter Adapter(Host host, McpOAuthCancellationAdmission admission,
        McpAdmittedOAuthRefreshClientAuthentication? hook = null, McpOAuthAdmittedClient? client = null) =>
        new(Server, host, host.Exchange, () => 1000, admission, client ?? new("synthetic-client", AuthenticationMethod: "client_secret_basic"), 1_048_576, hook);
    private static async Task MutationAndDefaults()
    {
        var host = new Host(); var records = new Inventory(); IMcpOAuthRefreshClientAuthentication? saved = null;
        var client = new McpOAuthAdmittedClient("synthetic-client", AuthenticationMethod: "client_secret_basic");
        var adapter = Adapter(host, new(), (proposal, token) =>
        {
            saved = proposal; host.Order.Enqueue("authenticate");
            Require(ReferenceEquals(proposal.Client, client) && proposal.HeaderGet("accept") == "application/json" &&
                proposal.HeaderGet("Authorization") is null && proposal.FormGet("client_id") is null && proposal.FormGet("grant_type") == "refresh_token" &&
                proposal.FormGet("refresh_token") == "synthetic-refresh" && proposal.FormGet("resource") == "https://offline.invalid/resource", "Original base refresh proposal/default override differs.");
            proposal.HeaderSet("Authorization", "  Custom synthetic\t"); proposal.HeaderAppend("X-Custom", "one"); proposal.HeaderAppend("x-custom", "two");
            proposal.FormAppend("client_id", "first"); proposal.FormAppend("client_id", "duplicate"); proposal.FormSet("client_id", "custom-client");
            proposal.FormAppend("custom", "first"); proposal.FormAppend("custom", "second");
            Require(proposal.FormGetAll("client_id").SequenceEqual(new[] { "custom-client" }), "Duplicate form Set differs."); return ValueTask.CompletedTask;
        }, client).CreateAuthentication();
        using var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
        try
        {
            await records.JoinVoid(() => adapter.OnUnauthorized!(Context(response), default).AsTask());
            var request = host.Requests.Single();
            var body = request.Body ?? throw new IOException("Refresh request body absent.");
            Require(request.Headers["authorization"] == "Custom synthetic" && request.Headers["x-custom"] == "one, two" &&
                body.EndsWith("&client_id=custom-client&custom=first&custom=second", StringComparison.Ordinal) && !body.Contains("client_secret", StringComparison.Ordinal) &&
                host.Order.SequenceEqual(new[] { "save-discovery", "authenticate", "exchange", "save-tokens" }) && host.Current.Tokens?.RefreshToken == "synthetic-refresh", "Custom override/order/retained refresh differs.");
            try { saved!.FormSet("late", "mutation"); throw new IOException("Retired proposal stayed live."); } catch (ObjectDisposedException) { }
            Require(!body.Contains("late", StringComparison.Ordinal), "Saved reference changed immutable request.");
        }
        catch (Exception error) { records.Failures.Add(error); }
        finally { await host.JoinAll(records); }
        records.ThrowIfFailed();
        var defaults = new Host(); var defaultRecords = new Inventory();
        // Use exact unchanged original constructor rather than customization overload.
        var originalAdapter = new McpAdmittedOAuthRefreshAdapter(Server, defaults, defaults.Exchange, () => 1000, new(), new("synthetic-client", "synthetic-secret"));
        using var defaultResponse = new HttpResponseMessage(HttpStatusCode.Unauthorized);
        try { await defaultRecords.JoinVoid(() => originalAdapter.CreateAuthentication().OnUnauthorized!(Context(defaultResponse), default).AsTask()); Require(defaults.Requests.Single().Headers["Authorization"] == "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("synthetic-client:synthetic-secret")), "Absent-hook original constructor changed default auth."); }
        catch (Exception error) { defaultRecords.Failures.Add(error); }
        finally { await defaults.JoinAll(defaultRecords); }
        defaultRecords.ThrowIfFailed();
    }
    private static async Task HeldOriginal()
    {
        var host = new Host(); var records = new Inventory(); var entered = Gate(); var canceled = Gate(); var release = Gate();
        using var source = new CancellationTokenSource(); var admission = new McpOAuthCancellationAdmission();
        IMcpOAuthRefreshClientAuthentication? proposal = null; Task? callback = null;
        async Task Hold(IMcpOAuthRefreshClientAuthentication value, CancellationToken token)
        { proposal = value; using var registration = token.Register(() => canceled.TrySetResult()); entered.TrySetResult(); await release.Task; }
        var adapter = Adapter(host, admission, (value, token) => { callback = Hold(value, token); return new(callback); }).CreateAuthentication();
        using var response = new HttpResponseMessage(HttpStatusCode.Unauthorized); Task? first = null; Task? second = null; Task? cancelOriginal = null;
        try
        {
            first = adapter.OnUnauthorized!(Context(response), source.Token).AsTask(); await records.JoinSignal(entered.Task, first);
            second = adapter.OnUnauthorized!(Context(response), source.Token).AsTask();
            cancelOriginal = admission.CancelAsync(source); await records.JoinVoid(() => cancelOriginal);
            await records.JoinSignal(canceled.Task, first);
            Require(!first.IsCompleted && !second.IsCompleted && callback is { IsCompleted: false } && host.Requests.Count == 0 && host.TokenSaves == 0,
                "Shared refresh caller cancellation failed to retain held original or caused token effects.");
            release.TrySetResult(); await records.Expected(() => first!, error => error is McpOAuthFlowCanceledException);
            await records.Expected(() => second!, error => error is McpOAuthFlowCanceledException);
            Require(first.IsCanceled && second.IsCanceled && host.Requests.Count == 0 && host.TokenSaves == 0, "Late exchange/token store or cancellation status changed.");
            try { proposal!.HeaderGet("accept"); throw new IOException("Canceled proposal stayed live."); } catch (ObjectDisposedException) { }
        }
        catch (Exception error) { records.Failures.Add(error); }
        finally
        {
            release.TrySetResult(); if (cancelOriginal is not null) await records.Cleanup(() => cancelOriginal);
            if (callback is not null) await records.Cleanup(() => callback); if (first is not null) await records.Cleanup(() => first); if (second is not null) await records.Cleanup(() => second);
            await host.JoinAll(records);
        }
        records.ThrowIfFailed();
    }
    private static async Task Faults()
    {
        foreach (var mode in new[] { 0, 1, 2 })
        {
            var left = new IOException("refresh-left"); var right = new InvalidOperationException("refresh-right");
            var failure = mode == 0 ? (Exception)new AggregateException(left, new AggregateException(left, right)) : new OperationCanceledException("unrequested-refresh-hook");
            var original = mode == 1 ? null : Task.FromException(failure); var host = new Host(); var records = new Inventory(); IMcpOAuthRefreshClientAuthentication? saved = null;
            var adapter = Adapter(host, new(), (proposal, token) => { saved = proposal; if (mode == 1) throw failure; return new(original!); }).CreateAuthentication();
            using var response = new HttpResponseMessage(HttpStatusCode.Unauthorized); Task? work = null;
            try
            {
                work = adapter.OnUnauthorized!(Context(response), default).AsTask();
                await records.Expected(() => work, error => ExactHookFault(error, original, failure));
                Require(work.IsFaulted && !work.IsCanceled && host.Requests.Count == 0 && host.TokenSaves == 0, "Custom hook fault canceled or caused later effects.");
                try { saved!.FormGet("refresh_token"); throw new IOException("Faulted proposal stayed live."); } catch (ObjectDisposedException) { }
                if (original is not null) await records.Expected(() => original, error => ReferenceEquals(error, failure));
            }
            catch (Exception error) { records.Failures.Add(error); }
            finally { if (original is not null) await records.Cleanup(() => original); if (work is not null) await records.Cleanup(() => work); await host.JoinAll(records); }
            records.ThrowIfFailed();
        }
        await CanceledHookOriginal();
    }
    private static async Task CanceledHookOriginal()
    {
        using var source = new CancellationTokenSource(); var admission = new McpOAuthCancellationAdmission(); var host = new Host(); var records = new Inventory();
        Task? callback = null; IMcpOAuthRefreshClientAuthentication? saved = null;
        var authentication = Adapter(host, admission, (proposal, token) =>
        { saved = proposal; admission.Cancel(source); callback = Task.FromCanceled(token); return new(callback); }).CreateAuthentication();
        using var response = new HttpResponseMessage(HttpStatusCode.Unauthorized); Task? work = null;
        try
        {
            work = authentication.OnUnauthorized!(Context(response), source.Token).AsTask();
            await records.Expected(() => work, error => error is McpOAuthFlowCanceledException outer && outer.CancellationToken == source.Token &&
                outer.Direct is McpOAuthFlowCanceledException inner && ReferenceEquals(inner.Original, callback) && inner.CancellationToken == source.Token);
            Require(work.IsCanceled && callback is { IsCanceled: true } && host.TokenSaves == 0 && host.Requests.Count == 0, "Genuine canceled hook original changed status or published effects.");
            await records.Expected(() => callback!, error => error is OperationCanceledException canceled && canceled.CancellationToken == source.Token);
            try { saved!.FormGet("refresh_token"); throw new IOException("Canceled-original proposal stayed live."); } catch (ObjectDisposedException) { }
        }
        catch (Exception error) { records.Failures.Add(error); }
        finally { if (callback is not null) await records.Cleanup(() => callback); if (work is not null) await records.Cleanup(() => work); await host.JoinAll(records); }
        records.ThrowIfFailed();
    }
    private static async Task Ancestry()
    {
        var ambient = new AsyncLocal<string?> { Value = "refresh-frame-free-register" };
        using var sourceA = new CancellationTokenSource(); using var sourceB = new CancellationTokenSource();
        var admissionA = new McpOAuthCancellationAdmission(); var admissionB = new McpOAuthCancellationAdmission();
        var hostA = new Host(); var hostB = new Host(); var records = new Inventory(); var returned = new ConcurrentQueue<Task>(); var callbacks = new ConcurrentQueue<Task>();
        McpAdmittedHttpAuthentication? authenticationA = null; McpAdmittedHttpAuthentication? authenticationB = null; var refused = 0;
        using var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
        using var second = sourceB.Token.Register(() =>
        {
            Require(ambient.Value == "refresh-frame-free-register", "Second normal Register lost captured EC."); var loads = hostA.Loads;
            records.Refused(() => authenticationA!.Token(default).AsTask(), returned.Enqueue); refused++;
            records.Refused(() => authenticationA!.OnUnauthorized!(Context(response), default).AsTask(), returned.Enqueue); refused++;
            Require(hostA.Loads == loads, "After-await ancestor reentry reached state store before refusal.");
        });
        using var first = sourceA.Token.Register(() =>
        {
            var loads = hostA.Loads; records.Refused(() => authenticationA!.Token(default).AsTask(), returned.Enqueue); refused++;
            Require(hostA.Loads == loads, "Own cancellation reentry reached state store.");
            var original = authenticationB!.OnUnauthorized!(Context(response), default).AsTask(); returned.Enqueue(original); records.JoinBlocking(original);
        });
        async Task CustomA(IMcpOAuthRefreshClientAuthentication proposal, CancellationToken token)
        {
            await Task.Yield(); var original = admissionA.CancelAsync(sourceA); callbacks.Enqueue(original); await original;
            proposal.HeaderSet("Authorization", "Custom A");
        }
        async Task CustomB(IMcpOAuthRefreshClientAuthentication proposal, CancellationToken token)
        {
            await Task.Yield(); var original = admissionB.CancelAsync(sourceB); callbacks.Enqueue(original); await original;
            proposal.HeaderSet("Authorization", "Custom B");
        }
        authenticationA = Adapter(hostA, admissionA, (proposal, token) => { var original = CustomA(proposal, token); callbacks.Enqueue(original); return new(original); }).CreateAuthentication();
        authenticationB = Adapter(hostB, admissionB, (proposal, token) => { var original = CustomB(proposal, token); callbacks.Enqueue(original); return new(original); }).CreateAuthentication();
        try
        {
            await records.JoinVoid(() => authenticationA.OnUnauthorized!(Context(response), default).AsTask());
            Require(refused == 3 && hostA.TokenSaves == 1 && hostB.TokenSaves == 1 && hostA.Requests.Single().Headers["Authorization"] == "Custom A" &&
                hostB.Requests.Single().Headers["Authorization"] == "Custom B", "Cross-owner after-await physical ancestry or unrelated refresh admission changed.");
        }
        catch (Exception error) { records.Failures.Add(error); }
        finally
        {
            foreach (var original in returned) await records.Cleanup(() => original); foreach (var original in callbacks) await records.Cleanup(() => original);
            await hostA.JoinAll(records); await hostB.JoinAll(records); ambient.Value = null;
        }
        records.ThrowIfFailed();
    }
    private static bool ExactHookFault(Exception error, Task? original, Exception expected)
    {
        var known = Graph(expected).ToHashSet(ReferenceEqualityComparer.Instance); var shared = 0; var custom = 0;
        foreach (var item in Graph(error))
        {
            if (known.Contains(item)) continue;
            if (item is McpOAuthFlowOriginalException carrier)
            {
                if (carrier.Phase == "shared-refresh")
                {
                    if (carrier.Original is not { IsFaulted: true } || carrier.Evidence is not AggregateException { InnerExceptions.Count: 1 } aggregate ||
                        !ReferenceEquals(aggregate.InnerExceptions[0], carrier.Direct) || carrier.Direct is not McpOAuthFlowOriginalException { Phase: "refresh-client-authentication" }) return false;
                    shared++;
                }
                else if (carrier.Phase == "refresh-client-authentication")
                {
                    if (!ReferenceEquals(carrier.Original, original) || !ReferenceEquals(carrier.Direct, expected) ||
                        (original is null ? !ReferenceEquals(carrier.Evidence, expected) : carrier.Evidence is not AggregateException { InnerExceptions.Count: 1 } raw || !ReferenceEquals(raw.InnerExceptions[0], expected))) return false;
                    custom++;
                }
                else return false;
            }
            else if (item is AggregateException all)
            {
                if (all.InnerExceptions.Count != 1 || !(ReferenceEquals(all.InnerExceptions[0], expected) || all.InnerExceptions[0] is McpOAuthFlowOriginalException)) return false;
            }
            else return false;
        }
        return shared == 1 && custom == 1;
    }
    private static IEnumerable<Exception> Graph(Exception root)
    {
        var pending = new Queue<Exception>(); var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance); var edges = 0; pending.Enqueue(root);
        while (pending.Count != 0)
        {
            var item = pending.Dequeue(); if (!seen.Add(item)) continue; if (seen.Count > 1024) throw new IOException("Fixture node bound."); yield return item;
            IEnumerable<Exception> children = item is AggregateException aggregate ? aggregate.InnerExceptions : item.InnerException is { } inner ? [inner] : [];
            foreach (var child in children) { if (++edges > 4096) throw new IOException("Fixture edge bound."); pending.Enqueue(child); }
        }
    }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Require(bool valid, string message) { if (!valid) throw new IOException(message); }
}
