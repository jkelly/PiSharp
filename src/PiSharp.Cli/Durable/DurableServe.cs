// PiSharp-only (decision 0008, Phase 4). `pisharp durable serve` runs this machine as a durable device: PiSharp.Durable.Hosting's sessions
// behind PiSharp.Durable.Server's v1 API, which durable-pi's hub and TUI, and PiSharp Durable Web, can drive. `pisharp durable token` prints
// or rotates the device token. The device's directory holds device.json, the token file (readable by the user only), the catalog and the
// sessions; it defaults to ~/.pi/agent/experimental/durable-device. Models are the CLI's own (auth.json, models.json, the environment), or
// the faux model with --provider faux. The server binds loopback unless --host says otherwise: a device token is a shell.
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using PiSharp.AI;
using PiSharp.Cli.Pi;
using PiSharp.Contracts;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Hosting;
using PiSharp.Durable.Hosting.Testing;
using PiSharp.Durable.Server;
using System.Collections.Immutable;

namespace PiSharp.Cli.Durable;

/// <summary><c>pisharp durable serve</c> and <c>pisharp durable token</c>.</summary>
internal static class DurableServe
{
    internal const string ServeUsage =
        "Usage: pisharp durable serve [--port <n>] [--host <address>] [--name <name>] [--start-dirs <dir>[,<dir>...]] [--data-dir <dir>] " +
        "[--provider faux] [--model <provider>/<id>] [--save]";

    internal const string TokenUsage = "Usage: pisharp durable token [--rotate] [--data-dir <dir>]";

    /// <summary>The parsed options of <c>serve</c>.</summary>
    internal sealed record ServeOptions
    {
        public int? Port { get; init; }
        public string? Host { get; init; }
        public string? Name { get; init; }
        public IReadOnlyList<string>? StartDirs { get; init; }
        public string? DataDir { get; init; }
        public bool Faux { get; init; }
        public string? Model { get; init; }
        public bool Save { get; init; }
    }

    /// <exception cref="ArgumentException">An unknown option or a missing value.</exception>
    internal static ServeOptions ParseServe(IReadOnlyList<string> args)
    {
        var options = new ServeOptions();
        for (var i = 0; i < args.Count; i++)
        {
            string Value() => i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
            options = args[i] switch
            {
                "--port" => options with
                {
                    Port = int.TryParse(Value(), out var port) && port is >= 0 and <= 65535 ? port : throw new ArgumentException("--port must be 0 to 65535."),
                },
                "--host" => options with { Host = Value() },
                "--name" => options with { Name = Value() },
                "--start-dirs" => options with { StartDirs = [.. (options.StartDirs ?? []), .. Value().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)] },
                "--data-dir" => options with { DataDir = Value() },
                "--provider" => Value() == "faux" ? options with { Faux = true } : throw new ArgumentException("--provider only takes faux; real models come from your Pi configuration."),
                "--model" => options with { Model = Value() },
                "--save" => options with { Save = true },
                _ => throw new ArgumentException($"Unknown argument: {args[i]}\n{ServeUsage}"),
            };
        }
        return options;
    }

    /// <summary>Runs the device until the process is asked to stop.</summary>
    internal static async Task<int> ServeAsync(string[] args, PiHost host, CancellationToken cancellationToken)
    {
        ServeOptions options;
        try { options = ParseServe(args); }
        catch (ArgumentException error) { return await FailAsync(host, error.Message).ConfigureAwait(false); }
        var directory = Path.GetFullPath(options.DataDir is { } data ? PiPaths.NormalizePath(data, host.Home) : DeviceConfig.DefaultDirectory(host.Home), host.Cwd);
        DeviceConfig config;
        string token;
        try
        {
            config = DeviceConfig.Load(directory);
            config = config with
            {
                Name = options.Name ?? config.Name,
                Host = options.Host ?? config.Host,
                Port = options.Port ?? config.Port,
                StartDirs = options.StartDirs is { Count: > 0 } dirs ? [.. dirs.Select(dir => Path.GetFullPath(PiPaths.NormalizePath(dir, host.Home), host.Cwd))] : config.StartDirs,
            };
            if (options.Save) config.Save(directory);
            token = DeviceConfig.EnsureToken(directory);
        }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return await FailAsync(host, error.Message).ConfigureAwait(false);
        }
        var startDirs = config.StartDirs.Count > 0 ? config.StartDirs : [host.Home];
        foreach (var dir in startDirs) Directory.CreateDirectory(dir);

        DurableModels? cliModels = null;
        IDurableModels models;
        ModelRef? defaultModel;
        string thinking = "low";
        try
        {
            if (options.Faux)
            {
                models = new FauxDurableModels();
                defaultModel = FauxDurableModels.EchoRef;
            }
            else
            {
                var agentDir = PiPaths.AgentDirectory(host.GetEnvironment, host.Home);
                var settings = PiSettings.Load(startDirs[0], agentDir, projectTrusted: true);
                var runtime = DurableHarnessSetup.ConfigureHarnessHttp(settings, host.LiveRuntime with { LlamaProvider = () => false }, host.GetEnvironment,
                    host.SetEnvironment);
                cliModels = await DurableModels.CreateAsync(runtime, null, cancellationToken).ConfigureAwait(false);
                models = new ServeModels(cliModels);
                (string? Provider, string Model)? chosen = options.Model is { } model
                    ? model.IndexOf('/', StringComparison.Ordinal) is var slash and > 0 ? (model[..slash], model[(slash + 1)..]) : (null, model)
                    : null;
                var initial = DurableHarnessSetup.FindInitialAgentModel(settings, cliModels, chosen);
                defaultModel = initial.Model;
                thinking = initial.ThinkingLevel ?? thinking;
                if (defaultModel is null)
                    await host.Stderr.WriteLineAsync(initial.FallbackMessage ?? "No model with configured auth: sessions need a model chosen in the UI.").ConfigureAwait(false);
            }
        }
        catch (InvalidOperationException error)
        {
            return await FailAsync(host, error.Message).ConfigureAwait(false);
        }

        DurableDeviceHost? device = null;
        WebApplication? app = null;
        try
        {
            device = await DurableDeviceHost.OpenAsync(new DurableDeviceOptions
            {
                DataDirectory = directory,
                StartDirectories = startDirs,
                DeviceName = config.Name,
                Models = models,
                DefaultModel = defaultModel,
                DefaultThinkingLevel = thinking,
                OnReport = (session, failure) => host.Stderr.WriteLine($"[device{(session is null ? "" : " " + session)}] {failure.Message}"),
            }, cancellationToken).ConfigureAwait(false);
            app = DurableDeviceServer.Build(device, new DurableDeviceServerOptions
            {
                Token = token,
                OnError = failure => host.Stderr.WriteLine("[device] stream update failed: " + failure.Message),
            }, $"http://{(config.Host.Contains(':', StringComparison.Ordinal) ? $"[{config.Host}]" : config.Host)}:{config.Port}");
            await app.StartAsync(cancellationToken).ConfigureAwait(false);
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault() ?? $"http://{config.Host}:{config.Port}";
            await host.Stdout.WriteLineAsync($"PiSharp durable device \"{device.Info.Name}\" on {address}").ConfigureAwait(false);
            await host.Stdout.WriteLineAsync($"  data: {directory}").ConfigureAwait(false);
            await host.Stdout.WriteLineAsync($"  start dirs (not a sandbox): {string.Join(", ", startDirs)}").ConfigureAwait(false);
            await host.Stdout.WriteLineAsync($"  token: {DeviceConfig.TokenPath(directory)} (pisharp durable token prints it)").ConfigureAwait(false);
            if (config.Host is not ("127.0.0.1" or "localhost" or "::1"))
                await host.Stderr.WriteLineAsync($"WARNING: listening on {config.Host}. Anyone with the token can run commands as you; plain HTTP sends it in clear text. " +
                    "Prefer an SSH tunnel or HTTPS.").ConfigureAwait(false);
            await host.Stdout.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            try { await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { /* Stopping. */ }
            return 0;
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return await FailAsync(host, error.Message).ConfigureAwait(false);
        }
        finally
        {
            if (app is not null)
            {
                await app.StopAsync(CancellationToken.None).ConfigureAwait(false);
                await app.DisposeAsync().ConfigureAwait(false);
            }
            if (device is not null) await device.DisposeAsync().ConfigureAwait(false);
            if (cliModels is not null) await cliModels.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Prints the device token, or replaces it with <c>--rotate</c>.</summary>
    internal static async Task<int> TokenAsync(string[] args, PiHost host)
    {
        var rotate = false;
        string? data = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--rotate") rotate = true;
            else if (args[i] == "--data-dir" && i + 1 < args.Length) data = args[++i];
            else return await FailAsync(host, $"Unknown argument: {args[i]}\n{TokenUsage}").ConfigureAwait(false);
        }
        var directory = Path.GetFullPath(data is null ? DeviceConfig.DefaultDirectory(host.Home) : PiPaths.NormalizePath(data, host.Home), host.Cwd);
        try
        {
            var token = rotate ? DeviceConfig.RotateToken(directory) : DeviceConfig.EnsureToken(directory);
            await host.Stdout.WriteLineAsync(token).ConfigureAwait(false);
            if (rotate) await host.Stderr.WriteLineAsync("Rotated. Restart the device and give hubs the new token.").ConfigureAwait(false);
            await host.Stdout.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            return 0;
        }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return await FailAsync(host, error.Message).ConfigureAwait(false);
        }
    }

    private static async Task<int> FailAsync(PiHost host, string message)
    {
        await host.Stderr.WriteLineAsync(message).ConfigureAwait(false);
        await host.Stderr.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        return 1;
    }
}

/// <summary>The CLI's models for a device: its streams, and its available chat models as the device's catalog (the CLI's model runtime
/// lists them through its registry, which <see cref="IDurableModels"/>' default catalog does not see).</summary>
internal sealed class ServeModels(DurableModels inner) : IDurableModels
{
    public DurableModel? GetModel(string provider, string modelId) => inner.GetModel(provider, modelId);

    public IAsyncEnumerable<StreamEvent> StreamSimple(DurableModel model, ImmutableArray<JsonValue> messages, DurableStreamOptions? options,
        CancellationToken cancellationToken) => inner.StreamSimple(model, messages, options, cancellationToken);

    public Task<AssistantMessage> CompleteSimpleAsync(DurableModel model, ImmutableArray<JsonValue> messages, DurableStreamOptions? options,
        CancellationToken cancellationToken) => inner.CompleteSimpleAsync(model, messages, options, cancellationToken);

    public IAsyncEnumerable<StreamEvent> StreamDeferred(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) =>
        inner.StreamDeferred(model, handle, cancellationToken);

    public Task<AssistantMessage> FetchDeferredAsync(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) =>
        inner.FetchDeferredAsync(model, handle, cancellationToken);

    public Task CancelDeferredAsync(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) =>
        inner.CancelDeferredAsync(model, handle, cancellationToken);

    public ImmutableArray<JsonObjectValue> GetModelsOfType(string type, string? provider) =>
        type != "chat" ? [] : [.. inner.GetAvailableSnapshot().Where(model => provider is null || model.Provider == provider).Select(Json)];

    public Task<ImmutableArray<JsonObjectValue>> GetAvailableOfTypeAsync(string type, string? provider, CancellationToken cancellationToken) =>
        Task.FromResult(GetModelsOfType(type, provider));

    private static JsonObjectValue Json(DurableModel model) => model.Wire ?? JsonValue.Object(
    [
        new("id", model.Id),
        new("name", model.Name),
        new("provider", model.Provider),
        new("contextWindow", model.ContextWindow),
        new("reasoning", model.Reasoning),
    ]);
}
