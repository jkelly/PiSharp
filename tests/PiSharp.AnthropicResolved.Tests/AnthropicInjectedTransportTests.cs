using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Authentication;
using PiSharp.Contracts;

// Source-only injected controls. Registration belongs to the coordinator's existing Program.
internal static class AnthropicInjectedTransportTests
{
    private static readonly ModelDescriptor Model = new("authored-anthropic", "anthropic-messages", "anthropic");
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Injected Anthropic transport assertion failed."); }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task<Exception> Failure(Task original)
    { try { await original; } catch (Exception error) { return error; } throw new InvalidOperationException("Expected original failure."); }
    private static async Task Entered(Task gate, Task original)
    {
        if (await Task.WhenAny(gate, original) == original && !gate.IsCompleted)
        { await original; throw new InvalidOperationException("Original settled before control entry."); }
        await gate;
    }
    private static IEnumerable<Exception> Inventory(Exception error)
    {
        yield return error;
        if (error is AggregateException aggregate)
            foreach (var inner in aggregate.InnerExceptions) foreach (var value in Inventory(inner)) yield return value;
        else if (error.InnerException is { } cause) foreach (var value in Inventory(cause)) yield return value;
    }
    private static Task<AuthenticationResolution> Resolve(string name, string secret) =>
        InjectedAuthenticationResolver.ResolveAnthropicApiKeyAsync(new ProviderEnvironmentSnapshot([KeyValuePair.Create<string, string?>(name, secret)]),
            (_, _) => ValueTask.FromResult<StoredApiKeyCredential?>(null)).AsTask();
    private sealed class Resource(Func<ValueTask> close) : IAsyncDisposable
    {
        internal int Calls;
        public ValueTask DisposeAsync() { Calls++; return close(); }
    }
    private sealed class Transport : IChatTransport
    {
        internal int Calls;
        internal ChatRequest? Request;
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); Calls++; Request = request; await Task.CompletedTask; yield break; }
    }
    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("anthropic-injected-auth.stored-key-exact-environment-and-borrowed-transport", Key);
        await check("anthropic-injected-auth.header-owned-bearer-does-not-become-api-key-or-OAuth-payload", Bearer);
        await check("anthropic-injected-auth.api-key-OAuth-classification-is-case-sensitive-original-includes", OAuthClassification);
        await check("anthropic-injected-auth.invalid-input-and-multicast-reject-before-factory", Admission);
        await check("anthropic-injected-auth.canceled-held-acquisition-joins-returned-owner-before-cancellation", CanceledAcquisition);
        await check("anthropic-injected-auth.rejected-model-joins-complete-original-cleanup-inventory", RejectedOwner);
        await check("anthropic-injected-auth.factory-sync-and-task-inventories-retain-provenance", FactoryOriginals);
        await check("anthropic-injected-auth.held-cleanup-repeats-original-and-rejects-self-join", CleanupOriginals);
        await check("anthropic-injected-auth.actual-canceled-cleanup-retains-task-state", CanceledCleanup);
    }
    private static async Task Key()
    {
        var environment = new ProviderEnvironmentSnapshot([KeyValuePair.Create<string, string?>("AUTHORED_SCOPE", "AUTHORED_METADATA")]);
        var resolution = await InjectedAuthenticationResolver.ResolveAnthropicApiKeyAsync(new ProviderEnvironmentSnapshot(),
            (_, _) => ValueTask.FromResult<StoredApiKeyCredential?>(new("AUTHORED_KEY", environment)));
        var transport = new Transport(); var owner = new Resource(() => ValueTask.CompletedTask); var calls = 0;
        await using var lease = await AnthropicInjectedTransportAdapter.AcquireAsync(Model, resolution, (model, binding, _) =>
        {
            calls++; Check(ReferenceEquals(model, Model) && ReferenceEquals(binding.Authentication, resolution.Authentication) &&
                binding.ApiKey == "AUTHORED_KEY" && binding.Headers.IsEmpty && !binding.UseOAuthProjection &&
                ReferenceEquals(binding.CredentialEnvironment, environment));
            var diagnostic = binding + JsonSerializer.Serialize(binding);
            Check(!diagnostic.Contains("AUTHORED_KEY", StringComparison.Ordinal) && !diagnostic.Contains("AUTHORED_METADATA", StringComparison.Ordinal));
            return ValueTask.FromResult(new AnthropicTransportAdmission(model, transport, owner));
        });
        Check(calls == 1 && ReferenceEquals(lease.Model, Model) && ReferenceEquals(lease.Transport, transport) && transport.Calls == 0);
        var request = new ChatRequest(Model, []);
        await foreach (var _ in lease.Transport.StreamAsync(request)) throw new InvalidOperationException("Synthetic transport emitted a frame.");
        Check(transport.Calls == 1 && ReferenceEquals(transport.Request, request) && owner.Calls == 0);
        await lease.DisposeAsync(); Check(owner.Calls == 1);
    }
    private static async Task Bearer()
    {
        var resolution = await Resolve("ANTHROPIC_AUTH_TOKEN", "AUTHORED_sk-ant-oat_BEARER");
        var transport = new Transport(); var owner = new Resource(() => ValueTask.CompletedTask);
        await using var lease = await AnthropicInjectedTransportAdapter.AcquireAsync(Model, resolution, (model, binding, _) =>
        {
            Check(binding.Kind == AuthenticationKind.BearerToken && binding.Origin == AuthenticationOrigin.AnthropicAuthToken &&
                binding.ApiKey is null && binding.Headers.Count == 1 && binding.Headers["Authorization"] == "Bearer AUTHORED_sk-ant-oat_BEARER" &&
                !binding.Headers.ContainsKey("x-api-key") && !binding.UseOAuthProjection && ReferenceEquals(binding.Authentication, resolution.Authentication));
            return ValueTask.FromResult(new AnthropicTransportAdmission(model, transport, owner));
        });
        Check(transport.Calls == 0);
    }
    private static async Task OAuthClassification()
    {
        foreach (var (secret, expected) in new[] { ("prefix-sk-ant-oat-authored", true), ("prefix-SK-ANT-OAT-authored", false), ("AUTHORED_NORMAL", false) })
        {
            var resolution = await Resolve("ANTHROPIC_OAUTH_TOKEN", secret);
            await using var lease = await AnthropicInjectedTransportAdapter.AcquireAsync(Model, resolution, (model, binding, _) =>
            {
                Check(binding.Kind == AuthenticationKind.ApiKey && binding.Origin == AuthenticationOrigin.AnthropicOAuthEnvironmentToken &&
                    binding.ApiKey == secret && binding.Headers.IsEmpty && binding.UseOAuthProjection == expected);
                return ValueTask.FromResult(new AnthropicTransportAdmission(model, new Transport(), new Resource(() => ValueTask.CompletedTask)));
            });
        }
    }
    private static async Task Admission()
    {
        var calls = 0; var resolution = await Resolve("ANTHROPIC_API_KEY", "AUTHORED_KEY");
        Func<ModelDescriptor, AnthropicInjectedAuthenticationBinding, CancellationToken, ValueTask<AnthropicTransportAdmission>> factory =
            (model, _, _) => { calls++; return ValueTask.FromResult(new AnthropicTransportAdmission(model, new Transport(), new Resource(() => ValueTask.CompletedTask))); };
        Check(await Failure(AnthropicInjectedTransportAdapter.AcquireAsync(Model, new(AuthenticationDiagnostic.Missing), factory).AsTask()) is ArgumentException);
        Check(await Failure(AnthropicInjectedTransportAdapter.AcquireAsync(Model with { Provider = "foreign" }, resolution, factory).AsTask()) is ArgumentException);
        Check(await Failure(AnthropicInjectedTransportAdapter.AcquireAsync(Model, resolution, factory + factory).AsTask()) is ArgumentException);
        var oversized = await Resolve("ANTHROPIC_API_KEY", new string('a', 4097));
        Check(await Failure(AnthropicInjectedTransportAdapter.AcquireAsync(Model, oversized, factory).AsTask()) is ArgumentException);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        var canceled = AnthropicInjectedTransportAdapter.AcquireAsync(Model, resolution, factory, stop.Token).AsTask();
        Check(await Failure(canceled) is OperationCanceledException && canceled.IsCanceled && calls == 0);
    }
    private static async Task CanceledAcquisition()
    {
        var acquired = new TaskCompletionSource<AnthropicTransportAdmission>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupEntered = Gate(); var cleanupRelease = Gate(); var transport = new Transport();
        var owner = new Resource(() => { cleanupEntered.TrySetResult(); return new(cleanupRelease.Task); });
        using var stop = new CancellationTokenSource();
        var opening = AnthropicInjectedTransportAdapter.AcquireAsync(Model, await Resolve("ANTHROPIC_API_KEY", "AUTHORED_KEY"),
            (_, _, token) => { Check(token == stop.Token); return new(acquired.Task); }, stop.Token).AsTask();
        Exception? primary = null, observed = null;
        try
        {
            stop.Cancel(); Check(!opening.IsCompleted && owner.Calls == 0);
            acquired.TrySetResult(new(Model, transport, owner)); await Entered(cleanupEntered.Task, opening);
            Check(!opening.IsCompleted && owner.Calls == 1 && transport.Calls == 0);
        }
        catch (Exception error) { primary = error; }
        finally { acquired.TrySetResult(new(Model, transport, owner)); cleanupRelease.TrySetResult(); observed = await Failure(opening); }
        if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
        Check(observed is OperationCanceledException cancellation && cancellation.CancellationToken == stop.Token && opening.IsCanceled && owner.Calls == 1);
    }
    private static async Task RejectedOwner()
    {
        var cleanupEntered = Gate(); var original = Gate(); var a = new IOException("authored owner A"); var b = new OperationCanceledException("faulted owner B");
        var owner = new Resource(() => { cleanupEntered.TrySetResult(); return new(original.Task); });
        var opening = AnthropicInjectedTransportAdapter.AcquireAsync(Model, await Resolve("ANTHROPIC_API_KEY", "AUTHORED_KEY"),
            (_, _, _) => ValueTask.FromResult(new AnthropicTransportAdmission(Model with { Id = "foreign" }, new Transport(), owner))).AsTask();
        Exception? primary = null, observed = null;
        try { await Entered(cleanupEntered.Task, opening); Check(!opening.IsCompleted && owner.Calls == 1); }
        catch (Exception rejectedControl) { primary = rejectedControl; }
        finally { original.TrySetException([a, b]); observed = await Failure(opening); }
        if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
        var error = observed!;
        Check(opening.IsFaulted && error is AggregateException { InnerExceptions.Count: 2 } aggregate && aggregate.InnerExceptions[0] is ArgumentException &&
            aggregate.InnerExceptions[1] is AnthropicAuthenticationOriginalFailure { Original: { IsFaulted: true }, InnerException: AggregateException { InnerExceptions.Count: 2 } } &&
            Inventory(error).Count(value => ReferenceEquals(value, a)) == 1 && Inventory(error).Count(value => ReferenceEquals(value, b)) == 1);
    }
    private static async Task FactoryOriginals()
    {
        var resolution = await Resolve("ANTHROPIC_API_KEY", "AUTHORED_KEY");
        var oce = new OperationCanceledException("authored faulted OCE"); var empty = new AggregateException("authored empty inventory");
        foreach (var synchronous in new Exception[] { oce, new AggregateException(new AggregateException(oce, oce)), empty })
        {
            var operation = AnthropicInjectedTransportAdapter.AcquireAsync(Model, resolution, (_, _, _) => throw synchronous).AsTask();
            var error = await Failure(operation);
            Check(operation.IsFaulted && error is AnthropicAuthenticationOriginalFailure { Original: null } && ReferenceEquals(error.InnerException, synchronous));
        }
        foreach (var faults in new Exception[][] { [oce], [oce, oce], [empty] })
        {
            var original = new TaskCompletionSource<AnthropicTransportAdmission>(TaskCreationOptions.RunContinuationsAsynchronously); original.SetException(faults);
            var operation = AnthropicInjectedTransportAdapter.AcquireAsync(Model, resolution, (_, _, _) => new(original.Task)).AsTask();
            var error = await Failure(operation);
            Check(operation.IsFaulted && error is AnthropicAuthenticationOriginalFailure failure && ReferenceEquals(failure.Original, original.Task) &&
                error.InnerException is AggregateException aggregate && aggregate.InnerExceptions.SequenceEqual(faults, ReferenceEqualityComparer.Instance));
        }
        using var source = new CancellationTokenSource(); source.Cancel(); var canceledOriginal = Task.FromCanceled<AnthropicTransportAdmission>(source.Token);
        var canceled = AnthropicInjectedTransportAdapter.AcquireAsync(Model, resolution, (_, _, _) => new(canceledOriginal)).AsTask();
        Check(await Failure(canceled) is AnthropicAuthenticationOriginalCancellation receipt && ReferenceEquals(receipt.Original, canceledOriginal) && canceled.IsCanceled);
    }
    private static async Task CleanupOriginals()
    {
        var entered = Gate(); var original = Gate(); var a = new IOException("authored cleanup A"); var b = new OperationCanceledException("authored faulted cleanup OCE");
        AnthropicInjectedTransportLease? lease = null; var refused = false;
        var owner = new Resource(() =>
        {
            try { _ = lease!.DisposeAsync(); } catch (InvalidOperationException) { refused = true; }
            entered.TrySetResult(); return new(original.Task);
        });
        lease = await AnthropicInjectedTransportAdapter.AcquireAsync(Model, await Resolve("ANTHROPIC_API_KEY", "AUTHORED_KEY"),
            (model, _, _) => ValueTask.FromResult(new AnthropicTransportAdmission(model, new Transport(), owner)));
        var closing = lease.DisposeAsync().AsTask();
        Exception? primary = null, observed = null;
        try
        {
            await Entered(entered.Task, closing); Check(refused && owner.Calls == 1 && !closing.IsCompleted && ReferenceEquals(closing, lease.DisposeAsync().AsTask()));
            var rejected = false; try { _ = lease.Transport; } catch (ObjectDisposedException) { rejected = true; } Check(rejected);
        }
        catch (Exception cleanupControl) { primary = cleanupControl; }
        finally { original.TrySetException([a, b]); observed = await Failure(closing); }
        if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
        var error = observed!;
        Check(closing.IsFaulted && error is AnthropicAuthenticationOriginalFailure failure && ReferenceEquals(failure.Original, original.Task) &&
            Inventory(error).Count(value => ReferenceEquals(value, a)) == 1 && Inventory(error).Count(value => ReferenceEquals(value, b)) == 1 &&
            ReferenceEquals(await Failure(lease.DisposeAsync().AsTask()), error) && owner.Calls == 1);
    }
    private static async Task CanceledCleanup()
    {
        using var stop = new CancellationTokenSource(); stop.Cancel(); var original = Task.FromCanceled(stop.Token);
        var owner = new Resource(() => new(original));
        var lease = await AnthropicInjectedTransportAdapter.AcquireAsync(Model, await Resolve("ANTHROPIC_API_KEY", "AUTHORED_KEY"),
            (model, _, _) => ValueTask.FromResult(new AnthropicTransportAdmission(model, new Transport(), owner)));
        var closing = lease.DisposeAsync().AsTask();
        Check(await Failure(closing) is AnthropicAuthenticationOriginalCancellation cancellation && ReferenceEquals(cancellation.Original, original) &&
            closing.IsCanceled && ReferenceEquals(closing, lease.DisposeAsync().AsTask()) && owner.Calls == 1);
    }
}
