// Decision 0011 (run W5, integration fix 2): an app nobody uses stops by itself. A Web Agent started with --faux or --faux-metered for a
// review or a test run and then forgotten kept running (a W4 review server ran on for more than twelve hours), and while it runs it holds its
// data folder's webagent.lock and every assembly of the build output it was started from, so a build of that worktree failed on locked files
// and its bin stayed at the old build. WebAgentOptions.IdleExit says how long the app may go with no request in progress and none started or
// finished: an hour with --faux (WebAgentOptions.FauxIdleExit), never without it, and --idle-exit <minutes> (or hh:mm:ss) sets it either way
// (0: never). Once that passes the app stops as a normal shutdown (IHostApplicationLifetime.StopApplication: the hosted services stop, the
// data folder is let go and the process ends with exit code 0) and says why on its log. A page's open event stream (GET /api/stream) is a
// request in progress, so a tab left open keeps the app running. The requests are counted by a startup filter, which wraps the whole
// pipeline, so it sees every request, static files included, whatever middleware the areas add.
namespace PiSharp.WebAgent.Hosting;

/// <summary>The app's requests in progress and the time the last one started or finished (<see cref="WebAgentIdleExit"/>).</summary>
public sealed class WebAgentActivity
{
    private readonly TimeProvider time;
    private int inProgress;
    private long last;

    /// <summary>Activity measured on <paramref name="time"/> (default <see cref="TimeProvider.System"/>), idle from now.</summary>
    public WebAgentActivity(TimeProvider? time = null)
    {
        this.time = time ?? TimeProvider.System;
        last = this.time.GetTimestamp();
    }

    /// <summary>The requests in progress.</summary>
    public int InProgress => Volatile.Read(ref inProgress);

    /// <summary>How long the app has had no request in progress and none started or finished: zero while one is in progress.</summary>
    public TimeSpan Idle => InProgress > 0 ? TimeSpan.Zero : time.GetElapsedTime(Interlocked.Read(ref last));

    /// <summary>A request started.</summary>
    public void Begin()
    {
        Interlocked.Increment(ref inProgress);
        Touch();
    }

    /// <summary>A request finished. The time is taken before the count drops, so a request that ran longer than the limit never reads as
    /// idle for its whole length.</summary>
    public void End()
    {
        Touch();
        Interlocked.Decrement(ref inProgress);
    }

    /// <summary>Starts the idle time again now (the app has just started).</summary>
    public void Touch() => Interlocked.Exchange(ref last, time.GetTimestamp());
}

/// <summary>Stops the app once it has been idle for <see cref="WebAgentOptions.IdleExit"/> (<see cref="WebAgentActivity.Idle"/>).</summary>
/// <param name="options">The app's options; nothing happens when <see cref="WebAgentOptions.IdleExit"/> is null.</param>
/// <param name="activity">The requests, counted by the startup filter <see cref="WebAgentIdleExitServices.AddWebAgentIdleExit"/> adds.</param>
/// <param name="lifetime">The host, stopped as a normal shutdown.</param>
/// <param name="logger">Says why the app stopped.</param>
public sealed class WebAgentIdleExit(WebAgentOptions options, WebAgentActivity activity, IHostApplicationLifetime lifetime,
    ILogger<WebAgentIdleExit> logger) : BackgroundService
{
    /// <summary>How often the idle time is looked at for <paramref name="limit"/>: a quarter of it, between 250 ms and 30 seconds.</summary>
    public static TimeSpan CheckInterval(TimeSpan limit) =>
        TimeSpan.FromTicks(Math.Clamp(limit.Ticks / 4, TimeSpan.FromMilliseconds(250).Ticks, TimeSpan.FromSeconds(30).Ticks));

    /// <summary>The line the app logs when it stops for <paramref name="limit"/>.</summary>
    public static string StopMessage(TimeSpan limit) =>
        $"PiSharp Web Agent: no request for {Describe(limit)}, so it stops (--idle-exit) and lets its data folder and build output go. "
        + "Start it with --idle-exit 0 to keep it running.";

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.IdleExit is not { } limit) return;
        activity.Touch();
        using var timer = new PeriodicTimer(CheckInterval(limit));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                if (activity.Idle < limit) continue;
                logger.LogWarning("{Message}", StopMessage(limit));
                lifetime.StopApplication();
                return;
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The app is stopping for another reason.
        }
    }

    private static string Describe(TimeSpan limit) =>
        limit.TotalMinutes >= 1 && limit.Seconds == 0 && limit.Milliseconds == 0
            ? (limit.TotalMinutes == 1 ? "1 minute" : $"{limit.TotalMinutes:0} minutes")
            : (limit.TotalSeconds == 1 ? "1 second" : $"{limit.TotalSeconds:0.###} seconds");
}

/// <summary>Registers <see cref="WebAgentIdleExit"/>.</summary>
public static class WebAgentIdleExitServices
{
    /// <summary>When <see cref="WebAgentOptions.IdleExit"/> is set, registers <see cref="WebAgentActivity"/>, a startup filter that counts
    /// every request around the whole pipeline, and <see cref="WebAgentIdleExit"/>; otherwise nothing.</summary>
    public static IServiceCollection AddWebAgentIdleExit(this IServiceCollection services, WebAgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        if (options.IdleExit is null) return services;
        services.AddSingleton(_ => new WebAgentActivity());
        services.AddSingleton<IStartupFilter, ActivityStartupFilter>();
        services.AddHostedService<WebAgentIdleExit>();
        return services;
    }

    private sealed class ActivityStartupFilter(WebAgentActivity activity) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, following) =>
            {
                activity.Begin();
                try
                {
                    await following(context).ConfigureAwait(false);
                }
                finally
                {
                    activity.End();
                }
            });
            next(app);
        };
    }
}
