using System.Collections.Concurrent;
using System.Collections.Immutable;
using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.Authentication;
using PiSharp.Extensions.Runtime.Mcp.Authentication;

namespace PiSharp.McpOAuthOrchestration.Tests;

public static class AdmittedOAuthOrchestrationControls
{
    public sealed record Original(string Phase, Task? Task, AggregateException? Aggregate, Exception? Direct);
    private static readonly ConcurrentQueue<Original> records = new();
    public static Original[] CapturedOriginals => records.ToArray();
    public static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        ("oauth-orchestration.invalid-client-invalidate-all-and-register-once", InvalidClient),
        ("oauth-orchestration.invalid-grant-one-rerun-no-loop", InvalidGrant),
        ("oauth-orchestration.refresh-fallback-full-originals-and-insecure-refusal", Fallback),
        ("oauth-orchestration.code-client-metadata-and-persistence-order", CodeAndMetadata),
        ("oauth-orchestration.held-verifier-close-and-normal-register-self-refusal", HeldClose),
        ("oauth-orchestration.faulted-oce-versus-canceled-original", FaultIdentity)
    ];
    public sealed class ControlFailure(Original[] originals, Exception[] failures) : IOException("OAuth control originals and criteria.", new AggregateException(failures))
    { public Original[] Originals { get; } = originals; public Exception[] Failures { get; } = failures; }
    private static readonly McpOAuthOrchestrationOptions Options = new(new("https://offline.invalid/mcp"), JsonData.Parse("{}"), "fallback-scope");
    private static void Require(bool value, string message) { if (!value) throw new IOException(message); }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class Inventory
    {
        internal readonly List<Exception> Failures = [];
        private readonly Dictionary<Task, Original> joined = new(ReferenceEqualityComparer.Instance);
        internal async Task<Original> Join(string phase, Task task)
        {
            if (joined.TryGetValue(task, out var cached)) return cached;
            Exception? direct = null; try { await task.ConfigureAwait(false); } catch (Exception error) { direct = error; }
            var record = new Original(phase, task, task.IsFaulted ? task.Exception : null, direct);
            joined.Add(task, record); records.Enqueue(record); return record;
        }
        internal void Retain(McpOAuthOrchestrationOriginal original) => records.Enqueue(new(original.Phase, original.Original, original.Aggregate, original.Direct));
        internal void Fail(Original record) { if (record.Aggregate is not null) Failures.Add(record.Aggregate); if (record.Direct is not null) Failures.Add(record.Direct); }
        internal async Task Close(McpAdmittedOAuthOrchestrator flow, Task? operation)
        {
            try
            {
                var original = await Join("close", flow.DisposeAsync().AsTask());
                if (original.Direct is null) return;
                if (operation is { IsFaulted: true } && original.Direct is McpOAuthFlowOriginalException { Phase: "close-active" } expected && ReferenceEquals(expected.Original, operation)) return;
                if (operation is { IsCanceled: true } && original.Direct is McpOAuthFlowCanceledException canceled && ReferenceEquals(canceled.Original, operation)) return;
                Fail(original);
            }
            catch (Exception error) { Failures.Add(error); }
        }
        internal void Throw() { if (Failures.Count != 0) throw new ControlFailure(CapturedOriginals, Failures.ToArray()); }
    }
    private sealed class Host
    {
        internal readonly List<string> Calls = [];
        internal McpOAuthAdmittedClient? Client = new("existing-client");
        internal McpOAuthTokens? Tokens = new("old", "Bearer", RefreshToken: "refresh-grant");
        internal int Refreshes, Invalidations, Registrations;
        internal McpOAuthDiscoveredServer Discovery = new("https://offline.invalid/", JsonData.Parse("{}"), null);
        internal Func<CancellationToken, ValueTask<McpOAuthTokens>> RefreshBody = _ => new(new McpOAuthTokens("fresh", "Bearer"));
        internal Func<string, CancellationToken, ValueTask>? VerifierBody;
        internal McpOAuthOrchestrationDependencies Dependencies() => new(
            (_, _) => { Calls.Add("discover"); return new(Discovery); },
            (_, _) => { Calls.Add("save-discovery"); return ValueTask.CompletedTask; },
            _ => { Calls.Add("read-client"); return new(Client); },
            (client, _) => { Calls.Add("save-client"); Client = client; return ValueTask.CompletedTask; },
            (_, _, _, _) => { Calls.Add("register-client"); Registrations++; return new(new McpOAuthAdmittedClient("registered")); },
            _ => { Calls.Add("read-tokens"); return new(Tokens); },
            (_, _, token) => { Calls.Add("refresh"); Refreshes++; return RefreshBody(token); },
            _ => { Calls.Add("read-verifier"); return new("verifier"); },
            (_, _, verifier, _) => { Require(verifier == "verifier", "Actual stored verifier not supplied."); Calls.Add("exchange-code"); return new(new McpOAuthTokens("code-token", "Bearer")); },
            (context, _) => { Calls.Add("begin"); Require(context.Scope == "fallback-scope", "Exact scope fallback absent."); return new(new McpOAuthAuthorizationProposal(new("https://offline.invalid/authorize"), "verifier")); },
            (tokens, _) => { Calls.Add("save-tokens"); Tokens = tokens; return ValueTask.CompletedTask; },
            (verifier, token) => { Calls.Add("save-verifier"); return VerifierBody?.Invoke(verifier, token) ?? ValueTask.CompletedTask; },
            (_, _) => { Calls.Add("redirect"); return ValueTask.CompletedTask; },
            (which, _) => { Calls.Add("invalidate:" + which); Invalidations++; if (which == McpOAuthInvalidation.All) Client = null; Tokens = null; return ValueTask.CompletedTask; });
    }
    private static async Task Exercise(Host host, McpOAuthOrchestrationOptions options,
        Action<McpOAuthOrchestrationResult?, Original, Host> check)
    {
        var inventory = new Inventory(); var flow = new McpAdmittedOAuthOrchestrator(host.Dependencies(), new()); Task<McpOAuthOrchestrationResult>? operation = null;
        try
        {
            operation = flow.AuthorizeAsync(options); var record = await inventory.Join("authorize", operation);
            McpOAuthOrchestrationResult? result = null;
            if (operation.IsCompletedSuccessfully) { result = operation.Result; foreach (var item in result.Originals) inventory.Retain(item); }
            else if (record.Direct is McpOAuthOrchestrationException failed) foreach (var item in failed.Originals) inventory.Retain(item);
            else if (record.Direct is McpOAuthOrchestrationCanceledException canceled) foreach (var item in canceled.Originals) inventory.Retain(item);
            check(result, record, host);
        }
        catch (Exception error) { inventory.Failures.Add(error); if (operation is not null) inventory.Fail(await inventory.Join("failed-authorize", operation)); }
        finally { await inventory.Close(flow, operation); }
        inventory.Throw();
    }
    private static Task InvalidClient()
    {
        var host = new Host(); host.RefreshBody = _ => new(Task.FromException<McpOAuthTokens>(new McpOAuthTokenOperationFailure(McpOAuthTokenFailureCategory.WireOAuthError, "invalid_client", new IOException("actual synthetic wire OAuth error"))));
        return Exercise(host, Options, (result, _, state) =>
        {
            Require(result?.Outcome == McpOAuthAuthorizationOutcome.Redirect && state.Refreshes == 1 && state.Invalidations == 1 && state.Registrations == 1, "Invalid-client retry count differs.");
            Require(string.Join(',', state.Calls) == "discover,save-discovery,read-client,read-tokens,refresh,invalidate:All,discover,save-discovery,read-client,register-client,save-client,read-tokens,begin,save-verifier,redirect", "Actual original call order differs.");
            Require(result!.Originals.Single(item => item.Phase == "refresh").Original is { IsFaulted: true }, "Handled first refresh original absent.");
        });
    }
    private static Task InvalidGrant()
    {
        var host = new Host(); host.RefreshBody = _ => new(Task.FromException<McpOAuthTokens>(new McpOAuthTokenOperationFailure(McpOAuthTokenFailureCategory.WireOAuthError, "invalid_grant", new IOException("actual synthetic wire OAuth error"))));
        var dependencies = host.Dependencies(); // Keep a refresh grant after invalidation to prove the second run cannot retry again.
        dependencies = dependencies with { Invalidate = (which, _) => { host.Calls.Add("invalidate:" + which); host.Invalidations++; Require(which == McpOAuthInvalidation.Tokens, "Wrong invalidation authority."); return ValueTask.CompletedTask; } };
        return ExerciseDependencies(host, dependencies, Options, (_, record, state) =>
            Require(record.Task is { IsFaulted: true } && record.Direct is McpOAuthOrchestrationException && state.Refreshes == 2 && state.Invalidations == 1, "Second invalid-grant did not settle once as Faulted."));
    }
    private static async Task ExerciseDependencies(Host host, McpOAuthOrchestrationDependencies dependencies, McpOAuthOrchestrationOptions options,
        Action<McpOAuthOrchestrationResult?, Original, Host> check)
    {
        var inventory = new Inventory(); var flow = new McpAdmittedOAuthOrchestrator(dependencies, new()); Task<McpOAuthOrchestrationResult>? operation = null;
        try
        {
            operation = flow.AuthorizeAsync(options); var record = await inventory.Join("authorize", operation);
            var result = operation.IsCompletedSuccessfully ? operation.Result : null;
            if (result is not null) foreach (var item in result.Originals) inventory.Retain(item);
            if (record.Direct is McpOAuthOrchestrationException failed) foreach (var item in failed.Originals) inventory.Retain(item);
            if (record.Direct is McpOAuthOrchestrationCanceledException canceled) foreach (var item in canceled.Originals) inventory.Retain(item);
            check(result, record, host);
        }
        catch (Exception error) { inventory.Failures.Add(error); if (operation is not null) inventory.Fail(await inventory.Join("failed-authorize", operation)); }
        finally { await inventory.Close(flow, operation); }
        inventory.Throw();
    }
    private static async Task Fallback()
    {
        var left = new IOException("left"); var shared = new IOException("shared"); var nested = new AggregateException(shared, left, shared);
        var original = Task.FromException<McpOAuthTokens>(nested); var host = new Host { RefreshBody = _ => new(original) };
        await Exercise(host, Options, (result, _, state) =>
        {
            Require(result?.Outcome == McpOAuthAuthorizationOutcome.Redirect && state.Invalidations == 0, "Ordinary refresh failure did not redirect.");
            var captured = result!.Originals.Single(item => item.Phase == "refresh");
            Require(ReferenceEquals(captured.Original, original) && captured.Aggregate is { InnerExceptions.Count: 1 } aggregate && ReferenceEquals(aggregate.InnerExceptions[0], nested), "Full refresh aggregate/shared leaves lost.");
        });
        foreach (var ordinary in new[] { new McpOAuthProtocolException("token_invalid", "local token parse"), new McpOAuthProtocolException("client_authentication", "local default auth selection") })
            await Exercise(new Host { RefreshBody = _ => new(Task.FromException<McpOAuthTokens>(ordinary)) }, Options,
                (result, _, state) => Require(result?.Outcome == McpOAuthAuthorizationOutcome.Redirect && state.Invalidations == 0 && state.Calls.Contains("begin"), "Local error was incorrectly classified as wire OAuthError."));
        foreach (var code in new[] { "", "token_invalid" })
        {
            var wire = new McpOAuthTokenOperationFailure(McpOAuthTokenFailureCategory.WireOAuthError, code, new IOException("actual synthetic string wire error"));
            await Exercise(new Host { RefreshBody = _ => new(Task.FromException<McpOAuthTokens>(wire)) }, Options,
                (_, record, state) => Require(record.Task is { IsFaulted: true } && !state.Calls.Contains("begin") && state.Invalidations == 0,
                    "Empty or same-code wire OAuth error incorrectly became local fallback."));
        }
        var serverError = new McpOAuthTokenOperationFailure(McpOAuthTokenFailureCategory.WireOAuthError, "server_error", new IOException("actual synthetic wire error"));
        await Exercise(new Host { RefreshBody = _ => new(Task.FromException<McpOAuthTokens>(serverError)) }, Options,
            (result, _, state) => Require(result?.Outcome == McpOAuthAuthorizationOutcome.Redirect && state.Invalidations == 0, "Wire server_error did not permit original fallback."));
        var insecure = new McpOAuthTokenOperationFailure(McpOAuthTokenFailureCategory.InsecureEndpoint, null, new IOException("actual synthetic endpoint refusal"));
        await Exercise(new Host { RefreshBody = _ => new(Task.FromException<McpOAuthTokens>(insecure)) }, Options,
            (_, record, state) => Require(record.Task is { IsFaulted: true } && !state.Calls.Contains("begin") && state.Invalidations == 0, "Insecure endpoint was redirected or invalidated."));
    }
    private static async Task CodeAndMetadata()
    {
        await Exercise(new Host(), Options with { AuthorizationCode = "code" }, (result, _, state) =>
            Require(result?.Outcome == McpOAuthAuthorizationOutcome.Authorized && string.Join(',', state.Calls) == "discover,save-discovery,read-client,read-verifier,exchange-code,save-tokens", "Code original order differs."));
        var verifierFault = new IOException("admitted verifier read failed"); var verifierOriginal = Task.FromException<string>(verifierFault);
        var verifierHost = new Host(); var verifierDependencies = verifierHost.Dependencies() with { ReadVerifier = _ => new(verifierOriginal) };
        await ExerciseDependencies(verifierHost, verifierDependencies, Options with { AuthorizationCode = "code" }, (_, record, state) =>
        {
            Require(record.Task is { IsFaulted: true } && record.Direct is McpOAuthOrchestrationException && !state.Calls.Contains("exchange-code") && !state.Calls.Contains("save-tokens"), "Failed verifier read allowed token effects.");
            var raw = ((McpOAuthOrchestrationException)record.Direct!).Originals.Single(item => item.Phase == "read-verifier");
            Require(ReferenceEquals(raw.Original, verifierOriginal) && ReferenceEquals(raw.Direct, verifierFault), "Verifier original fault identity absent.");
        });
        var host = new Host { Client = null, Tokens = null, Discovery = new("https://offline.invalid/", JsonData.Parse("{\"client_id_metadata_document_supported\":true}"), null) };
        // Pi v1.1.0: a Client ID Metadata Document identifies the client without registration and is not stored
        // (v0.99.1 saved the metadata URL as client information).
        await Exercise(host, Options with { ClientMetadataDocument = metadata => new("https://offline.invalid/client.json", "http://127.0.0.1/callback") },
            (result, _, state) => Require(result?.Outcome == McpOAuthAuthorizationOutcome.Redirect && state.Client is null && state.Registrations == 0 && !state.Calls.Contains("save-client") && state.Calls.Contains("begin"), "Metadata document registered or was persisted."));
        await Exercise(new Host { Client = null }, Options with { AuthorizationCode = "code" },
            (_, record, state) => Require(record.Task is { IsFaulted: true } && state.Registrations == 0 && !state.Calls.Contains("exchange-code"), "Missing-client code exchange had effects."));
    }
    private static async Task HeldClose()
    {
        var inventory = new Inventory(); var host = new Host { Tokens = null }; var entered = Gate(); var release = Gate(); var canceled = Gate(); var refusals = 0;
        var admission = new McpOAuthOrchestrationCancellationAdmission(); McpAdmittedOAuthOrchestrator? flow = null;
        using var callbackSource = new CancellationTokenSource(); var marker = new AsyncLocal<int> { Value = 1 };
        using var registration = callbackSource.Token.Register(() =>
        {
            try { flow!.DisposeAsync().AsTask().GetAwaiter().GetResult(); throw new IOException("Self-close was not refused."); } catch (InvalidOperationException) { refusals++; }
            try { flow!.AuthorizeAsync(Options).GetAwaiter().GetResult(); throw new IOException("Self-authorize was not refused."); } catch (InvalidOperationException) { refusals++; }
        }); marker.Value = 0;
        Task? hook = null; Task<McpOAuthOrchestrationResult>? operation = null; Task? close = null;
        async Task Verifier(CancellationToken token)
        {
            await Task.Yield(); var actualCancel = admission.CancelAsync(callbackSource); var cancelRecord = await inventory.Join("owned-normal-register-cancel", actualCancel); if (cancelRecord.Direct is not null) inventory.Fail(cancelRecord);
            using var signal = token.Register(() => canceled.TrySetResult()); entered.TrySetResult(); await release.Task;
        }
        host.VerifierBody = (_, token) => { var original = Verifier(token); hook = original; return new(original); };
        var ownedFlow = new McpAdmittedOAuthOrchestrator(host.Dependencies(), admission); flow = ownedFlow;
        try
        {
            operation = ownedFlow.AuthorizeAsync(Options); await Signal(entered.Task, operation, inventory);
            close = ownedFlow.DisposeAsync().AsTask(); await Signal(canceled.Task, close, inventory);
            Require(!operation.IsCompleted && !close.IsCompleted && refusals == 2 && !host.Calls.Contains("redirect"), "Held close/reentry/fencing differs.");
            release.TrySetResult(); var record = await inventory.Join("authorize", operation);
            Require(operation.IsCanceled && record.Direct is McpOAuthOrchestrationCanceledException, "Held authorization did not preserve real cancellation.");
        }
        catch (Exception error) { inventory.Failures.Add(error); }
        finally
        {
            release.TrySetResult(); if (hook is not null) { var item = await inventory.Join("held-verifier", hook); if (item.Direct is not null) inventory.Fail(item); }
            if (operation is not null) { var item = await inventory.Join("authorize-finally", operation); if (item.Direct is McpOAuthOrchestrationCanceledException known) foreach (var raw in known.Originals) inventory.Retain(raw); else if (item.Direct is not null) inventory.Fail(item); }
            await inventory.Close(ownedFlow, operation); if (close is not null) await inventory.Join("close-original", close);
        }
        inventory.Throw();
    }
    private static async Task Signal(Task signal, Task terminal, Inventory inventory)
    {
        using var timerOwner = new CancellationTokenSource(); var timer = Task.Delay(TimeSpan.FromSeconds(10), timerOwner.Token);
        try { var race = Task.WhenAny(signal, terminal, timer); await race; Require(signal.IsCompletedSuccessfully, "Signal absent before terminal/finite diagnostic deadline."); }
        finally { try { timerOwner.Cancel(); } catch (Exception error) { inventory.Failures.Add(error); } await inventory.Join("diagnostic-timer", timer); }
    }
    private static async Task FaultIdentity()
    {
        var oce = new OperationCanceledException("faulted clear-token original"); var original = Task.FromException<McpOAuthTokens>(oce);
        var host = new Host(); var deps = host.Dependencies() with { ExchangeCode = (_, _, _, _) => new(original) };
        await ExerciseDependencies(host, deps, Options with { AuthorizationCode = "code" }, (_, record, _) =>
        {
            Require(record.Task is { IsFaulted: true } && record.Direct is McpOAuthOrchestrationException, "Faulted OCE became cancellation.");
            var raw = ((McpOAuthOrchestrationException)record.Direct!).Originals.Single(item => item.Phase == "exchange-code");
            var rawAggregate = raw.Aggregate ?? throw new IOException("Faulted original aggregate absent.");
            Require(ReferenceEquals(raw.Original, original) && rawAggregate.InnerExceptions.Count == 1 && ReferenceEquals(rawAggregate.InnerExceptions[0], oce), "Faulted OCE original identity lost.");
        });
        using var source = new CancellationTokenSource(); source.Cancel(); var actualCanceled = Task.FromCanceled<McpOAuthTokens>(source.Token);
        await ExerciseDependencies(new Host(), deps with { ExchangeCode = (_, _, _, _) => new(actualCanceled) }, Options with { AuthorizationCode = "code" },
            (_, record, state) => Require(record.Task is { IsCanceled: true } && record.Direct is McpOAuthOrchestrationCanceledException && !state.Calls.Contains("save-tokens"), "Actual canceled original was normalized or persisted."));
    }
}