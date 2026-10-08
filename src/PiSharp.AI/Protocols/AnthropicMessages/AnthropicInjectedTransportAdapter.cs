using System.Collections.Immutable;
using System.Text.Json.Serialization;
using PiSharp.Contracts;

namespace PiSharp.AI.Authentication;

/// <summary>Explicit secret-bearing input for an admitted Anthropic transport factory.
/// The resolver's apiKey channel remains distinct from header-owned bearer authentication.</summary>
public sealed class AnthropicInjectedAuthenticationBinding
{
    [JsonIgnore] public ResolvedAuthentication Authentication { get; }
    [JsonIgnore] public string? ApiKey { get; }
    [JsonIgnore] public ImmutableDictionary<string, string> Headers { get; }
    [JsonIgnore] public ProviderEnvironmentSnapshot? CredentialEnvironment => Authentication.CredentialEnvironment;
    public AuthenticationKind Kind => Authentication.Kind;
    public AuthenticationOrigin Origin => Authentication.Origin;
    public bool UseOAuthProjection { get; }

    internal AnthropicInjectedAuthenticationBinding(ResolvedAuthentication authentication)
    {
        Authentication = authentication;
        if (authentication.Kind == AuthenticationKind.ApiKey)
        {
            ApiKey = authentication.Secret;
            Headers = ImmutableDictionary<string, string>.Empty;
            // Original anthropic-messages.ts isOAuthToken uses case-sensitive includes.
            UseOAuthProjection = ApiKey.Contains("sk-ant-oat", StringComparison.Ordinal);
        }
        else
        {
            Headers = ImmutableDictionary<string, string>.Empty.Add("Authorization", "Bearer " + authentication.Secret);
        }
    }
    public override string ToString() => $"AnthropicInjectedAuthenticationBinding ({Kind}, {Origin}) [redacted]";
}

/// <summary>The admitted factory transfers one actual owner with its transport on return.
/// Before return, the factory owns all allocations and their failure cleanup. Each acquisition
/// must return fresh ownership; the adapter cannot inspect hidden aliases or credential sources.</summary>
public sealed record AnthropicTransportAdmission(ModelDescriptor Model, IChatTransport Transport, IAsyncDisposable Resources);

/// <summary>Preserves all actual task faults, including faulted OCE and empty aggregates.
/// Its inner evidence may contain private callback data and must not be logged by default.</summary>
public sealed class AnthropicAuthenticationOriginalFailure(string stage, Task? original, Exception evidence)
    : IOException("Admitted Anthropic " + stage + " original failed.", evidence)
{
    [JsonIgnore] public Task? Original { get; } = original;
}

/// <summary>Cancellation established by the actual admitted original's task state.</summary>
public sealed class AnthropicAuthenticationOriginalCancellation(string stage, Task original, OperationCanceledException evidence)
    : OperationCanceledException("Admitted Anthropic " + stage + " original canceled.", evidence, evidence.CancellationToken)
{
    [JsonIgnore] public Task Original { get; } = original;
}

/// <summary>Consumes only an already selected injected value. No lookup, client, request or
/// credential acquisition occurs here. The single owning factory is the effect authority.</summary>
public static class AnthropicInjectedTransportAdapter
{
    public static async ValueTask<AnthropicInjectedTransportLease> AcquireAsync(ModelDescriptor model,
        AuthenticationResolution resolution,
        Func<ModelDescriptor, AnthropicInjectedAuthenticationBinding, CancellationToken, ValueTask<AnthropicTransportAdmission>> acquire,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model); ArgumentNullException.ThrowIfNull(resolution);
        ArgumentNullException.ThrowIfNull(acquire);
        if (acquire.GetInvocationList().Length != 1) throw new ArgumentException("One owning transport acquisition is required.", nameof(acquire));
        if (model.Provider != "anthropic" || model.Api != "anthropic-messages" || string.IsNullOrWhiteSpace(model.Id) ||
            model.Id.Length > 1024 || model.Id.Any(char.IsControl)) throw new ArgumentException("Unsupported Anthropic model identity.", nameof(model));
        if (resolution.Diagnostic != AuthenticationDiagnostic.Resolved || resolution.Authentication is not { } authentication ||
            authentication.Kind is not (AuthenticationKind.ApiKey or AuthenticationKind.BearerToken) ||
            authentication.Secret.Length is < 1 or > 4096)
            throw new ArgumentException("An admitted bounded authentication value is required.", nameof(resolution));
        cancellationToken.ThrowIfCancellationRequested();
        var binding = new AnthropicInjectedAuthenticationBinding(authentication);
        Task<AnthropicTransportAdmission> original;
        try { original = acquire(model, binding, cancellationToken).AsTask(); }
        catch (Exception error) { throw new AnthropicAuthenticationOriginalFailure("acquisition", null, error); }
        AnthropicTransportAdmission admitted;
        try { admitted = await original.ConfigureAwait(false); }
        catch (Exception error) { throw AnthropicAuthenticationOriginal.Classify("acquisition", original, error); }
        try
        {
            ArgumentNullException.ThrowIfNull(admitted);
            ArgumentNullException.ThrowIfNull(admitted.Resources); ArgumentNullException.ThrowIfNull(admitted.Transport);
            if (admitted.Model != model) throw new ArgumentException("Admitted transport changed the exact model identity.");
            cancellationToken.ThrowIfCancellationRequested();
            return new(admitted);
        }
        catch (Exception rejected)
        {
            if (admitted?.Resources is { } resources)
            {
                try { await AnthropicAuthenticationOriginal.DisposeAsync(resources).ConfigureAwait(false); }
                catch (Exception cleanup) { throw new AggregateException("Anthropic admission and original cleanup failed.", rejected, cleanup); }
            }
            throw;
        }
    }
}

/// <summary>One returned model/transport/resource owner. Settle borrowed stream enumerators
/// before disposal, as for NativeHttpModelProvider; this leaf does not manufacture stream
/// cancellation or replace the admitted transport's own enumeration ownership.</summary>
public sealed class AnthropicInjectedTransportLease : IAsyncDisposable
{
    private readonly AnthropicTransportAdmission admitted;
    private readonly object gate = new();
    private readonly AsyncLocal<bool> inside = new();
    private Task? close;
    internal AnthropicInjectedTransportLease(AnthropicTransportAdmission admitted) => this.admitted = admitted;
    public ModelDescriptor Model => admitted.Model;
    public IChatTransport Transport
    {
        get { lock (gate) { if (close is not null) throw new ObjectDisposedException(nameof(AnthropicInjectedTransportLease)); return admitted.Transport; } }
    }
    public ValueTask DisposeAsync()
    {
        if (inside.Value) throw new InvalidOperationException("Admitted transport cleanup cannot join its own lease.");
        lock (gate) return new(close ??= CloseAsync());
    }
    private async Task CloseAsync()
    {
        await Task.Yield(); var prior = inside.Value; inside.Value = true;
        try { await AnthropicAuthenticationOriginal.DisposeAsync(admitted.Resources).ConfigureAwait(false); }
        finally { inside.Value = prior; }
    }
}

internal static class AnthropicAuthenticationOriginal
{
    internal static Exception Classify(string stage, Task original, Exception selected) =>
        original.IsCanceled && selected is OperationCanceledException canceled
            ? new AnthropicAuthenticationOriginalCancellation(stage, original, canceled)
            : new AnthropicAuthenticationOriginalFailure(stage, original, (Exception?)original.Exception ?? selected);

    internal static async Task DisposeAsync(IAsyncDisposable resources)
    {
        Task original;
        try { original = resources.DisposeAsync().AsTask(); }
        catch (Exception error) { throw new AnthropicAuthenticationOriginalFailure("cleanup", null, error); }
        try { await original.ConfigureAwait(false); }
        catch (Exception error) { throw Classify("cleanup", original, error); }
    }
}
