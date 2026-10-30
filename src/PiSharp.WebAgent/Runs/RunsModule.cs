// Decision 0011 (run W4, R0): registration of the run engine and run history. Program.cs calls AddRunsArea after AddSpendArea, so:
//   - runs.db (RunsDb), its store, the live logs and the engine are singletons (TryAdd: a test may put its own first);
//   - the recorder (RecordingDurableModels) wraps the app's final IDurableModels, which is the Spend Guard over the routes (or the faux
//     models): every durable session's call (a chat's) is recorded by op; the engine itself streams through the unguarded models under its
//     own lease, so a Playground run is never recorded twice;
//   - with real providers (not --faux), RoutingOptions.ProviderHandler is wrapped by the capture hook (RunCaptureHandler) so a run's options
//     reach the provider and, when the user turned raw payloads on, its redacted request and response are kept. A handler a test or host
//     registered stays theirs (the hook never disposes it); otherwise the hook owns one shared handler (redirects off, no cookies);
//   - RunRetention cleans runs.db when the app starts and every day after (history.retentionDays, pinned runs kept; raw payloads after
//     RunHistorySettings.RawRetentionDays).
// MapRunsArea maps /api/runs (RunEndpoints).
using Microsoft.Extensions.DependencyInjection.Extensions;
using PiSharp.Durable;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Hosting;
using PiSharp.WebAgent.Routing;

namespace PiSharp.WebAgent.Runs;

/// <summary>Registration and endpoints of the run engine and run history.</summary>
public static class RunsModule
{
    /// <summary>Registers the area's services. Call it after <c>AddSpendArea</c>, so the recorder wraps the Spend Guard.</summary>
    public static IServiceCollection AddRunsArea(this IServiceCollection services, WebAgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        services.TryAddSingleton(provider => new RunsDb(options, provider.GetService<ILogger<RunsDb>>()));
        services.TryAddSingleton(provider => new RunStore(provider.GetRequiredService<RunsDb>(), provider.GetService<ILogger<RunStore>>()));
        services.TryAddSingleton<RunLive>();
        services.TryAddSingleton(provider => new RunEngine(provider.GetRequiredService<RunStore>(), provider.GetRequiredService<RunLive>(), provider,
            provider.GetService<ILogger<RunEngine>>(), provider.GetService<IHostApplicationLifetime>()));
        services.AddHostedService<RunRetention>();
        DecorateModels(services);
        if (!options.Faux) DecorateRouting(services);
        return services;
    }

    /// <summary>Maps <c>/api/runs</c>.</summary>
    public static WebApplication MapRunsArea(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapRunEndpoints();
        return app;
    }

    /// <summary>Replaces every <see cref="IDurableModels"/> registration with the recorder over the last one; nothing when there is none, or
    /// when the last one is already the recorder.</summary>
    private static void DecorateModels(IServiceCollection services)
    {
        var registered = services.Where(descriptor => descriptor.ServiceType == typeof(IDurableModels) && !descriptor.IsKeyedService).ToList();
        if (registered.Count == 0 || registered[^1].ImplementationFactory?.Target is RecordingModelsFactory) return;
        var factory = new RecordingModelsFactory(registered[^1]);
        foreach (var descriptor in registered) services.Remove(descriptor);
        services.Add(new ServiceDescriptor(typeof(IDurableModels), factory.Create, registered[^1].Lifetime));
    }

    /// <summary>Wraps the provider handler of the last <see cref="RoutingOptions"/> registration with the capture hook.</summary>
    private static void DecorateRouting(IServiceCollection services)
    {
        var registered = services.Where(descriptor => descriptor.ServiceType == typeof(RoutingOptions) && !descriptor.IsKeyedService).ToList();
        if (registered.Count == 0 || registered[^1].ImplementationFactory?.Target is CaptureRoutingFactory) return;
        var factory = new CaptureRoutingFactory(registered[^1]);
        foreach (var descriptor in registered) services.Remove(descriptor);
        services.TryAddSingleton<RunCaptureHook>();
        services.Add(new ServiceDescriptor(typeof(RoutingOptions), factory.Create, ServiceLifetime.Singleton));
    }

    /// <summary>Builds the recorder over one registration of the inner models.</summary>
    private sealed class RecordingModelsFactory(ServiceDescriptor inner)
    {
        public object Create(IServiceProvider provider)
        {
            var models = Resolve<IDurableModels>(inner, provider);
            return new RecordingDurableModels(models, provider.GetRequiredService<RunStore>(), provider, provider.GetService<ILogger<RecordingDurableModels>>());
        }
    }

    /// <summary>Builds routing options whose provider handler is the capture hook over the registered one.</summary>
    private sealed class CaptureRoutingFactory(ServiceDescriptor inner)
    {
        public object Create(IServiceProvider provider)
        {
            var options = Resolve<RoutingOptions>(inner, provider);
            return new RoutingOptions
            {
                ProviderHandler = provider.GetRequiredService<RunCaptureHook>().Wrap(options.ProviderHandler),
                CodexTransport = options.CodexTransport,
                Time = options.Time,
            };
        }
    }

    private static T Resolve<T>(ServiceDescriptor descriptor, IServiceProvider provider) where T : class => descriptor switch
    {
        { ImplementationInstance: T instance } => instance,
        { ImplementationFactory: { } create } => (T)create(provider),
        { ImplementationType: { } type } => (T)ActivatorUtilities.CreateInstance(provider, type),
        _ => throw new InvalidOperationException($"The {typeof(T).Name} registration names no implementation."),
    };
}

/// <summary>The capture hook of the routing path, held by the container so it is disposed with the app (and with it the shared handler it
/// made; a handler a test or host passed is never disposed here).</summary>
public sealed class RunCaptureHook : IDisposable
{
    private readonly Lock gate = new();
    private RunCaptureHandler? handler;

    /// <summary>The hook over <paramref name="inner"/> (null: <see cref="RunCaptureHandler.CreateDefaultInner"/>, which every route then
    /// shares, owned and disposed by this hook). Made once.</summary>
    public RunCaptureHandler Wrap(HttpMessageHandler? inner)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(handler is null && disposed, this);
            return handler ??= inner is null
                ? new RunCaptureHandler(RunCaptureHandler.CreateDefaultInner(), ownsInner: true)
                : new RunCaptureHandler(inner, ownsInner: false);
        }
    }

    private bool disposed;

    /// <inheritdoc/>
    public void Dispose()
    {
        RunCaptureHandler? made;
        lock (gate)
        {
            disposed = true;
            made = handler;
            handler = null;
        }
        made?.Dispose();
    }
}

/// <summary>Cleans runs.db when the app starts and every <see cref="Interval"/> after: runs older than the retention (unpinned, not
/// running), and raw payloads older than <see cref="RunHistorySettings.RawRetentionDays"/>. Its first cleanup opens the store, which marks
/// the runs a stopped process left running as interrupted (never retried).</summary>
public sealed class RunRetention(RunStore store, IServiceProvider services, ILogger<RunRetention>? log = null) : BackgroundService
{
    /// <summary>How often the cleanup runs.</summary>
    public static TimeSpan Interval { get; } = TimeSpan.FromHours(24);

    /// <summary>One cleanup now.</summary>
    public RunCleanup CleanNow()
    {
        var settings = services.GetService<ISettingsStore>()?.Get<RunHistorySettings>() ?? RunHistorySettings.CreateDefault();
        var time = services.GetService<TimeProvider>() ?? TimeProvider.System;
        return store.Cleanup(settings, time.GetUtcNow());
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var cleanup = CleanNow();
                if (cleanup.Runs > 0 || cleanup.RawRuns > 0)
                    log?.LogInformation("Run history cleanup: {Runs} runs removed, raw payloads of {RawRuns} runs removed.", cleanup.Runs, cleanup.RawRuns);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                log?.LogWarning(error, "Run history cleanup failed; it runs again in {Interval}.", Interval);
            }
            try
            {
                await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
