// Web Agent W2 (WU2, plan "Architecture" item 2): the CLI's live route runtime. Its effects moved to PiSharp.ModelRuntime as
// LiveRouteRuntime; the parts that are the CLI's own stay here: the auth.json store (Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT):
// packages/coding-agent/src/core/auth-storage.ts and config.ts getAuthPath), the default runtime of the user's agent directory, and the
// built-in llama.cpp provider (packages/coding-agent/src/extensions/llama/provider.ts).
using PiSharp.AI;
using PiSharp.AI.Authentication;
using PiSharp.Cli.Authentication;
using PiSharp.Cli.Llama;
using PiSharp.Cli.Models;

namespace PiSharp.Cli.Commands;

/// <summary>The live route's effects: the process environment reader (read once per name for the session), the provider HTTP
/// handler, and the <c>auth.json</c> path (null: no stored credentials) with the client and clock its OAuth refresh uses.</summary>
internal sealed record LiveSessionRuntime(Func<string, string?> ReadEnvironment, Func<HttpMessageHandler?> CreateHttpHandler,
    string? AuthPath = null, Func<HttpMessageInvoker>? CreateAuthHttp = null, TimeProvider? Time = null, string? ModelsPath = null)
    : LiveRouteRuntime(ReadEnvironment, CreateHttpHandler, CreateAuthHttp, Time, ModelsPath)
{
    // A stored auth.json credential (written by /login) owns the Anthropic provider; the environment is read only without one.
    internal static LiveSessionRuntime Default => DefaultRuntime.Value;
    private static readonly Lazy<LiveSessionRuntime> DefaultRuntime = new(() =>
    {
        var authPath = AuthJsonCredentialStore.CreateDefault().AuthPath;
        // model-runtime.ts: models.json (and models-store.json) live in the agent directory next to auth.json.
        return new(Environment.GetEnvironmentVariable, () => null, authPath, ModelsPath: Path.Combine(Path.GetDirectoryName(authPath)!, "models.json"));
    });

    /// <summary>Whether the built-in llama.cpp extension (and so its provider) is loaded, read when a registry composes its providers;
    /// null: loaded.</summary>
    internal Func<bool>? LlamaProvider { get; init; }

    /// <summary>The <c>auth.json</c> store at <see cref="AuthPath"/> (a missing file reads as empty); null without a path.</summary>
    protected override IProviderCredentialSource? OpenStore() => AuthPath is null ? null : new AuthJsonCredentialStore(AuthPath, Time);

    /// <summary>The built-in llama.cpp provider over the catalog its models store shares (provider.ts createLlamaProvider's state).</summary>
    protected override IReadOnlyList<IRegistryProviderLayer> RegistryLayers(FileModelsStore? modelsStore) =>
        [new LlamaRegistryLayer(LlamaCatalog.For(modelsStore?.Path), LlamaProvider)];
}

/// <summary>The CLI's entry points of the moved selection: a null runtime is <see cref="LiveSessionRuntime.Default"/> (the user's
/// agent directory), as before the move.</summary>
internal static class LiveSessionRuntimeSelection
{
    extension(LiveSessionSelection selection)
    {
        /// <summary>The selection's live connection (<see cref="LiveSessionSelection.ConnectRoute"/>).</summary>
        internal LiveSessionConnection Connect(LiveSessionRuntime? runtime) => selection.ConnectRoute(runtime ?? LiveSessionRuntime.Default);

        /// <summary>Anthropic, resolved at start and before every request (<see cref="LiveSessionSelection.ResolveAnthropicRouteAsync"/>).</summary>
        internal ValueTask<(AuthenticationResolution Authentication, HttpMessageHandler? Handler,
            Func<CancellationToken, ValueTask<AuthenticationResolution>> Reresolve)> ResolveAnthropicAsync(LiveSessionRuntime? runtime,
            CancellationToken cancellationToken) => selection.ResolveAnthropicRouteAsync(runtime ?? LiveSessionRuntime.Default, cancellationToken);
    }
}
