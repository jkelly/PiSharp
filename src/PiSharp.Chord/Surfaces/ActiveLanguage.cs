// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/surfaces/active-language.ts.
using PiSharp.Chord.Host;

namespace PiSharp.Chord.Surfaces;

public sealed class LanguageFollower : IDisposable
{
    private readonly Action<IReadOnlyList<string>> setDevice;
    private readonly Action stop;
    internal LanguageFollower(Action<IReadOnlyList<string>> setDevice, Action stop) { this.setDevice = setDevice; this.stop = stop; }
    public void SetDevice(IReadOnlyList<string> preferred) => setDevice(preferred);
    public void Stop() => stop();
    public void Dispose() => Stop();
}

public static class ActiveLanguage
{
    public static PageState<Language?> HostLanguage(ChordHost host) => PageStates.Get<Language?>(host, "chord.language", null);
    public static async Task<LanguageFollower> FollowAsync(ChordHost host, IReadOnlyList<string> preferred, Action<Language> changed,
        Action<MessagesReport> report, ChordContext context)
    {
        var abort = CancellationTokenSource.CreateLinkedTokenSource(context.AbortSignal ?? CancellationToken.None);
        var ctx = ChordContext.WithAbortSignal(abort.Token, context);
        var state = HostLanguage(host); var once = Languages.Once(report);
        var translations = SurfaceTranslations.None; string? pick = null; var device = preferred.ToArray(); var stopped = false;
        IReference? choice = null;
        void Update()
        {
            if (stopped) return;
            var locale = Languages.ResolveLocale(pick, device, translations.Locales);
            if (state.Read() is { } previous && previous.Locale == locale && ReferenceEquals(previous.Translations, translations)) return;
            var language = Languages.Create(locale, translations, once); state.Set(language); changed(language);
        }
        void Stop() => host.Dispatcher.Invoke(() =>
        {
            if (stopped) return; stopped = true; abort.Cancel(); choice?.Release(); abort.Dispose();
        });
        try
        {
            host.Dispatcher.Invoke(Update);
            var registry = await host.RegistryAsync(SurfaceTranslations.Registry, ctx).ConfigureAwait(false);
            host.Dispatcher.Invoke(() => { translations = registry.Value; Update(); });
            var watch = await registry.WatchAsync(ctx).ConfigureAwait(false);
            host.Dispatcher.Invoke(() => { translations = watch.Value; Update(); });
            watch.Start((call, _) =>
            {
                host.Dispatcher.Invoke(() => { if (call is NativeWatchValueCall<TranslationIndex> value) translations = value.Value; Update(); });
                return Task.CompletedTask;
            });
            _ = FollowPickAsync();
            return new(next => host.Dispatcher.Invoke(() => { device = next.ToArray(); Update(); }), Stop);
        }
        catch { Stop(); throw; }

        async Task FollowPickAsync()
        {
            try
            {
                choice = await host.GetAsync(LocaleChoice.Contract, new(), ctx).ConfigureAwait(false);
                if (stopped) { choice.Release(); return; }
                var watch = await choice.Member("pick").WatchAsync(ctx).ConfigureAwait(false);
                host.Dispatcher.Invoke(() => { pick = watch.Value?.GetValue<string>(); Update(); });
                watch.Start((call, _) =>
                {
                    host.Dispatcher.Invoke(() =>
                    {
                        if (call is WatchValueCall<System.Text.Json.Nodes.JsonNode?> value) pick = value.Value?.GetValue<string>();
                        Update();
                    });
                    return Task.CompletedTask;
                });
            }
            catch (Exception) { /* An optional choice may never bind or may end before its first value. */ }
        }
    }
}
