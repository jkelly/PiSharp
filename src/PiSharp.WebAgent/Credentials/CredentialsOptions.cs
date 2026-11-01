// Decision 0011 (run W2, WU5): what the credentials area reads from outside itself, gathered in one place so tests can replace it: the
// environment the host was started with (environment accounts, the OAuth callback host), how HTTP clients are made for sign-in and
// connection tests, the OpenAI auth server and the ChatGPT callback port, and the offline switch. With --faux the app is offline: accounts
// can still be stored and listed, but no sign-in starts and no connection test runs, so nothing reaches a provider or an auth server.
using PiSharp.AI.Catalogs;

namespace PiSharp.WebAgent.Credentials;

/// <summary>Options of the credentials area (registered as a singleton by <see cref="CredentialsModule.AddCredentialsArea"/> unless one is
/// registered first).</summary>
public sealed class CredentialsOptions
{
    /// <summary>The answer to a sign-in or connection test in offline (<c>--faux</c>) mode.</summary>
    public const string OfflineMessage =
        "The app is running offline (--faux): sign-in and connection tests are turned off, so nothing reaches a provider.";

    /// <summary>Reads an environment variable of the host (environment accounts, <c>PI_OAUTH_CALLBACK_HOST</c>); default the process
    /// environment. Tests pass their own, so they never see this machine's keys.</summary>
    public Func<string, string?> Environment { get; init; } = System.Environment.GetEnvironmentVariable;

    /// <summary>Whether a file exists (the Google application-default credentials probe of ambient accounts); default <see cref="File.Exists"/>.</summary>
    public Func<string, bool> FileExists { get; init; } = File.Exists;

    /// <summary>The user's home folder (where Google's application-default credentials live), or null for the current user's.</summary>
    public string? Home { get; init; }

    /// <summary>Makes the HTTP client of one sign-in or connection test; it is disposed afterwards. Tests return a client over a fake
    /// handler.</summary>
    public Func<HttpMessageInvoker> CreateHttp { get; init; } = () => new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>The time source of expiries, device-code polling and account timestamps; default <see cref="TimeProvider.System"/>.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>True when no sign-in or connection test may run (<c>--faux</c>).</summary>
    public bool Offline { get; init; }

    /// <summary>The OpenAI auth server of the ChatGPT (openai-codex) sign-in; tests point it at a fake.</summary>
    public string OpenAIAuthBaseUrl { get; init; } = "https://auth.openai.com";

    /// <summary>The loopback port of the ChatGPT sign-in callback (the registered redirect is <c>http://localhost:1455/auth/callback</c>;
    /// tests use a free port and call it directly).</summary>
    public int CodexCallbackPort { get; init; } = 1455;

    /// <summary>How long a sign-in may wait for the user before it is given up (the device codes expire sooner on their own).</summary>
    public TimeSpan SignInTimeout { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>How long a start call waits for the authorize URL or the device code before it answers with the flow as it stands.</summary>
    public TimeSpan SignInStartWait { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long one connection test may take.</summary>
    public TimeSpan TestTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>The providers checked for environment and ambient accounts; default every built-in PiSharp provider.</summary>
    public IReadOnlyList<string> EnvironmentProviders { get; init; } = [.. BuiltinProviders.All.Select(provider => provider.Id)];
}
