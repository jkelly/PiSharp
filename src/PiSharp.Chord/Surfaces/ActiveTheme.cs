// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/surfaces/active-theme.ts.
using PiSharp.Chord.Host;

namespace PiSharp.Chord.Surfaces;

public sealed class ThemeFollower : IDisposable
{
    private readonly Action<ThemeMode> setMode;
    private readonly Action stop;
    internal ThemeFollower(Action<ThemeMode> setMode, Action stop) { this.setMode = setMode; this.stop = stop; }
    public void SetMode(ThemeMode mode) => setMode(mode);
    public void Stop() => stop();
    public void Dispose() => Stop();
}

public static class ActiveTheme
{
    public static async Task<ThemeFollower> FollowAsync(ChordHost host, ThemeMode mode, Action<Theme> changed, ChordContext context)
    {
        var abort = CancellationTokenSource.CreateLinkedTokenSource(context.AbortSignal ?? CancellationToken.None);
        var ctx = ChordContext.WithAbortSignal(abort.Token, context);
        IReadOnlyList<Theme> themes = []; var pick = new ThemePick(); var stopped = false;
        IReference? choice = null;
        void Update() { if (!stopped) changed(Themes.Active(themes, pick, mode)); }
        void Stop() => host.Dispatcher.Invoke(() =>
        {
            if (stopped) return; stopped = true; abort.Cancel(); choice?.Release(); abort.Dispose();
        });
        try
        {
            host.Dispatcher.Invoke(Update);
            var registry = await host.RegistryAsync(Themes.Registry, ctx).ConfigureAwait(false);
            host.Dispatcher.Invoke(() => { themes = registry.Value; Update(); });
            var watch = await registry.WatchAsync(ctx).ConfigureAwait(false);
            host.Dispatcher.Invoke(() => { themes = watch.Value; Update(); });
            watch.Start((call, _) =>
            {
                host.Dispatcher.Invoke(() => { if (call is NativeWatchValueCall<IReadOnlyList<Theme>> value) themes = value.Value; Update(); });
                return Task.CompletedTask;
            });
            _ = FollowPickAsync();
            return new(next => host.Dispatcher.Invoke(() => { mode = next; Update(); }), Stop);
        }
        catch { Stop(); throw; }

        async Task FollowPickAsync()
        {
            try
            {
                choice = await host.GetAsync(ThemeChoice.Contract, new(), ctx).ConfigureAwait(false);
                if (stopped) { choice.Release(); return; }
                var watch = await choice.Member("pick").WatchAsync(ctx).ConfigureAwait(false);
                host.Dispatcher.Invoke(() =>
                {
                    pick = new(watch.Value?["dark"]?.GetValue<string>(), watch.Value?["light"]?.GetValue<string>());
                    Update();
                });
                watch.Start((call, _) =>
                {
                    host.Dispatcher.Invoke(() =>
                    {
                        if (call is WatchValueCall<System.Text.Json.Nodes.JsonNode?> value)
                            pick = new(value.Value?["dark"]?.GetValue<string>(), value.Value?["light"]?.GetValue<string>());
                        Update();
                    });
                    return Task.CompletedTask;
                });
            }
            catch (Exception) { /* An optional choice may never bind or may end before its first value. */ }
        }
    }
}
