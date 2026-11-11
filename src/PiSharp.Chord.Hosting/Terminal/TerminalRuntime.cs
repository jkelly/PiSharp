// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/tui/runtime.ts.
// Native adapters retain explicit trusted-code and terminal ownership boundaries.
using System.Text.Json.Nodes;
using PiSharp.Chord.Host;
using PiSharp.Chord.Surfaces;

namespace PiSharp.Chord.Hosting.Terminal;

/// <summary>Native terminal application code and explicitly admitted managed plugin registrations.</summary>
public sealed class TerminalRuntimeOptions
{
    public IReadOnlyDictionary<string, object?> Modules { get; init; } = new Dictionary<string, object?>();
    public IReadOnlyList<PluginFacet> Core { get; init; } = [];
    public IReadOnlyList<RegisteredScript> Initial { get; init; } = [];
    /// <summary>Resolves an admitted managed registration. Downloaded JavaScript/DLLs must not be evaluated here.</summary>
    public required Func<ScriptEntry, CancellationToken, Task<RegisteredScript>> ResolveTrustedScript { get; init; }
    public required Action<TerminalRuntime> Render { get; init; }
    public Action<ClientError> OnError { get; init; } = _ => { };
    public Action<ConnectionState>? OnConnection { get; init; }
    public Func<Task<bool>>? CanRetry { get; init; }
    public ILoopEnvironment Environment { get; init; } = new LoopEnvironment();
}

/// <summary>A complete native Chord terminal client with reconnects, scope resources, live theme/language and confirmed actions.</summary>
public sealed class TerminalRuntime : IAsyncDisposable
{
    private readonly Client _client;
    private readonly TerminalChannel _channel;
    private readonly ThemeFollower _theme;
    private readonly LanguageFollower _language;
    private Task? _stopping;
    private readonly object _gate = new();
    public ChordHost Host => _client.Host;
    public BridgeTerminal Terminal { get; }
    public TerminalTheme Theme { get; }
    private TerminalRuntime(Client client, TerminalChannel channel, BridgeTerminal terminal, TerminalTheme theme,
        ThemeFollower themeFollower, LanguageFollower languageFollower)
    { _client = client; _channel = channel; Terminal = terminal; Theme = theme; _theme = themeFollower; _language = languageFollower; }

    public static async Task<TerminalRuntime> StartAsync(ITerminalBridge bridge, TerminalRuntimeOptions options, ChordContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(options); context ??= ChordContext.Background;
        var channel = new TerminalChannel(bridge); Client? client = null; ThemeFollower? themeFollower = null; LanguageFollower? languageFollower = null;
        try
        {
            client = await Client.StartAsync(new ClientOptions
            {
                Scripts = Scripts.TuiScripts, Base = bridge.Start.Base, Initial = options.Initial, Modules = options.Modules,
                Core = [new("chord.themes", Themes.Facet()), new("chord.translations", SurfaceTranslations.Facet()), .. options.Core],
                Open = events => channel.OpenSocket(events.Opened, events.Received), FetchScript = options.ResolveTrustedScript,
                ServerBase = async cancellationToken =>
                {
                    try { return (await channel.RequestAsync(new TerminalRequest.Base(), cancellationToken).ConfigureAwait(false) as JsonValue)?.GetValue<string>(); }
                    catch (IOException) { return null; }
                },
                Outdated = channel.Outdated, CanRetry = options.CanRetry, Environment = options.Environment,
                OnError = options.OnError, OnConnection = options.OnConnection,
            }, context).ConfigureAwait(false);
            var mode = bridge.Start.Mode == "light" ? ThemeMode.Light : ThemeMode.Dark;
            var theme = new TerminalTheme(Themes.FallbackThemes[mode], bridge.Start.Colors == "truecolor" ? ColorDepth.TrueColor : ColorDepth.Indexed256);
            themeFollower = await ActiveTheme.FollowAsync(client.Host, mode, theme.Set, context).ConfigureAwait(false);
            languageFollower = await ActiveLanguage.FollowAsync(client.Host, bridge.Start.Locales ?? [], _ => { },
                report => options.OnError(new("messages", Messages: report)), context).ConfigureAwait(false);
            var runtime = new TerminalRuntime(client, channel, new(channel), theme, themeFollower, languageFollower);
            try { options.Render(runtime); }
            catch { await runtime.DisposeAsync().ConfigureAwait(false); throw; }
            return runtime;
        }
        catch
        {
            languageFollower?.Dispose(); themeFollower?.Dispose();
            if (client is not null) await client.DisposeAsync().ConfigureAwait(false);
            channel.Dispose(); throw;
        }
    }
    public async Task<bool> CopyAsync(string text, CancellationToken cancellationToken = default) =>
        (await _channel.RequestAsync(new TerminalRequest.Copy(text), cancellationToken).ConfigureAwait(false) as JsonValue)?.GetValue<bool>() == true;
    public async Task<bool> OpenAsync(string url, CancellationToken cancellationToken = default) =>
        (await _channel.RequestAsync(new TerminalRequest.Open(url), cancellationToken).ConfigureAwait(false) as JsonValue)?.GetValue<bool>() == true;
    public async Task<TerminalAttachment?> AttachAsync(string path, CancellationToken cancellationToken = default) =>
        await _channel.RequestAsync(new TerminalRequest.Attach(path), cancellationToken).ConfigureAwait(false) as TerminalAttachment;
    public void Exit(int code = 0) => _channel.Exit(code);
    public Task StopAsync()
    {
        lock (_gate) return _stopping ??= StopCoreAsync();
    }
    private async Task StopCoreAsync()
    {
        _language.Dispose(); _theme.Dispose();
        try { await _client.StopAsync().ConfigureAwait(false); }
        finally { Terminal.Dispose(); _channel.Dispose(); }
    }
    public ValueTask DisposeAsync() => new(StopAsync());
}
