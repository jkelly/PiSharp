// Decision 0011 (run W2, WU4): where PiSharp Web Agent keeps its data and whether it runs on faux models. Everything lives in one folder,
// %LOCALAPPDATA%\PiSharpWebAgent by default; the PISHARP_WEBAGENT_DATA environment variable moves it, and --data moves it again (the command
// line wins). --faux answers every request with FauxDurableModels and lists only the faux catalog, so no provider code runs at all.
// Both switches are read here, before ASP.NET Core sees the arguments: its command-line provider would take "--faux --data x" as faux="--data".
// Run W3 (S0) adds --faux-metered: still --faux (offline, no provider code), but the faux provider is Metered at a list price and its
// answers report usage, so the Spend Guard, its ledger and its pages can be exercised without a provider (Faux/FauxPricing.cs).
// Run W5 (integration fix 2) adds --idle-exit: how long the app may go without a request before it stops by itself (WebAgentIdleExit). A
// faux app (a review or test server) stops after an hour unless --idle-exit says otherwise; real use never stops unless --idle-exit asks.
using System.Globalization;

namespace PiSharp.WebAgent.Hosting;

/// <summary>The Web Agent's data folder and mode, read once at start (<see cref="FromCommandLine"/>) and registered as a singleton.</summary>
public sealed class WebAgentOptions
{
    /// <summary>The environment variable that moves the data folder.</summary>
    public const string DataEnvironmentVariable = "PISHARP_WEBAGENT_DATA";

    /// <summary>The folder name under %LOCALAPPDATA% when nothing moves it.</summary>
    public const string DefaultFolderName = "PiSharpWebAgent";

    /// <summary>The data folder, an absolute path.</summary>
    public required string DataDirectory { get; init; }

    /// <summary>Whether the app runs on faux models only (<c>--faux</c>): no provider is called, and only the faux catalog is listed.</summary>
    public bool Faux { get; init; }

    /// <summary>Whether the faux provider is priced (<c>--faux-metered</c>, which implies <see cref="Faux"/>): Metered at
    /// <see cref="Faux.FauxPricing.Price"/>, with answers that report usage (tokens from text length). Plain <c>--faux</c> is free and reports
    /// zero usage.</summary>
    public bool FauxMetered { get; init; }

    /// <summary>How long a faux app (<see cref="Faux"/>) goes without a request before it stops by itself, unless <c>--idle-exit</c>
    /// sets another time.</summary>
    public static readonly TimeSpan FauxIdleExit = TimeSpan.FromHours(1);

    /// <summary>How long the app goes without a request (none in progress, none started or finished) before it stops by itself, or null
    /// for never (<see cref="WebAgentIdleExit"/>): <c>--idle-exit &lt;minutes&gt;</c> or <c>--idle-exit hh:mm:ss</c>, where 0 means never;
    /// without it <see cref="FauxIdleExit"/> with <see cref="Faux"/>, else never.</summary>
    public TimeSpan? IdleExit { get; init; }

    /// <summary><c>device\</c>: the durable device host's data (one <c>agent.sqlite</c> per chat session).</summary>
    public string DeviceDirectory => Path.Join(DataDirectory, "device");

    /// <summary><c>keys\</c>: the ASP.NET Data Protection key ring (protected with DPAPI on Windows).</summary>
    public string KeysDirectory => Path.Join(DataDirectory, "keys");

    /// <summary><c>webagent.db</c>: the app's SQLite database (<see cref="Data.WebAgentDb"/>).</summary>
    public string DatabasePath => Path.Join(DataDirectory, "webagent.db");

    /// <summary><c>credentials.json</c>: secret values, each encrypted with Data Protection (written by the Credentials area).</summary>
    public string CredentialsPath => Path.Join(DataDirectory, "credentials.json");

    /// <summary>Creates the data folder, <see cref="DeviceDirectory"/> and <see cref="KeysDirectory"/> if they are missing.</summary>
    public void EnsureDirectories()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(DeviceDirectory);
        Directory.CreateDirectory(KeysDirectory);
    }

    /// <summary>The data folder: <paramref name="commandLine"/> (<c>--data</c>), else <paramref name="environment"/>
    /// (<see cref="DataEnvironmentVariable"/>), else <see cref="DefaultFolderName"/> under <paramref name="localApplicationData"/>; blank values
    /// count as missing. A relative path is taken from the current directory.</summary>
    public static string ResolveDataDirectory(string? commandLine, string? environment, string localApplicationData)
    {
        var chosen = !string.IsNullOrWhiteSpace(commandLine) ? commandLine.Trim()
            : !string.IsNullOrWhiteSpace(environment) ? environment.Trim()
            : Path.Join(localApplicationData, DefaultFolderName);
        return Path.GetFullPath(chosen);
    }

    /// <summary>Reads <c>--faux</c>, <c>--faux-metered</c> (which implies <c>--faux</c>), <c>--data &lt;folder&gt;</c> (or
    /// <c>--data=&lt;folder&gt;</c>) and <c>--idle-exit &lt;minutes or hh:mm:ss&gt;</c> (or <c>--idle-exit=...</c>) from
    /// <paramref name="args"/>, and returns the options with the arguments left for ASP.NET Core (<c>--urls</c>, logging and other
    /// configuration).</summary>
    /// <param name="args">The process arguments.</param>
    /// <param name="environment">Reads an environment variable; default <see cref="Environment.GetEnvironmentVariable(string)"/>.</param>
    /// <param name="localApplicationData">The %LOCALAPPDATA% folder; default the current user's.</param>
    /// <exception cref="ArgumentException"><c>--data</c> has no folder, <c>--faux=</c> or <c>--faux-metered=</c> is not true or false, or
    /// <c>--idle-exit</c> is not a whole number of minutes or hh:mm:ss.</exception>
    public static (WebAgentOptions Options, string[] HostArgs) FromCommandLine(IReadOnlyList<string> args, Func<string, string?>? environment = null,
        string? localApplicationData = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? data = null;
        var faux = false;
        var metered = false;
        TimeSpan? idle = null;
        var rest = new List<string>();
        for (var index = 0; index < args.Count; index++)
        {
            var arg = args[index];
            if (arg == "--faux") faux = true;
            else if (arg.StartsWith("--faux=", StringComparison.Ordinal))
                faux = bool.TryParse(arg["--faux=".Length..], out var value) ? value : throw new ArgumentException("--faux= takes true or false.");
            else if (arg == "--faux-metered") metered = true;
            else if (arg.StartsWith("--faux-metered=", StringComparison.Ordinal))
                metered = bool.TryParse(arg["--faux-metered=".Length..], out var value) ? value
                    : throw new ArgumentException("--faux-metered= takes true or false.");
            else if (arg == "--data")
                data = index + 1 < args.Count && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
                    ? args[++index] : throw new ArgumentException("--data needs a folder: --data <folder>.");
            else if (arg.StartsWith("--data=", StringComparison.Ordinal))
                data = arg["--data=".Length..] is { Length: > 0 } folder ? folder : throw new ArgumentException("--data needs a folder: --data=<folder>.");
            else if (arg == "--idle-exit")
                idle = index + 1 < args.Count && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
                    ? ParseIdleExit(args[++index]) : throw new ArgumentException(IdleExitUsage);
            else if (arg.StartsWith("--idle-exit=", StringComparison.Ordinal)) idle = ParseIdleExit(arg["--idle-exit=".Length..]);
            else rest.Add(arg);
        }
        environment ??= Environment.GetEnvironmentVariable;
        var directory = ResolveDataDirectory(data, environment(DataEnvironmentVariable),
            localApplicationData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        var idleExit = idle is { } chosen ? (chosen > TimeSpan.Zero ? chosen : null) : faux || metered ? FauxIdleExit : (TimeSpan?)null;
        return (new WebAgentOptions { DataDirectory = directory, Faux = faux || metered, FauxMetered = metered, IdleExit = idleExit }, [.. rest]);
    }

    private const string IdleExitUsage = "--idle-exit takes whole minutes (--idle-exit 30) or hh:mm:ss (--idle-exit 00:00:30); 0 never stops.";

    /// <summary>A whole number of minutes, or hh:mm:ss; zero (never) included.</summary>
    private static TimeSpan ParseIdleExit(string text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) ? TimeSpan.FromMinutes(minutes)
        : TimeSpan.TryParseExact(text, [@"hh\:mm\:ss", @"h\:mm\:ss"], CultureInfo.InvariantCulture, out var time) ? time
        : throw new ArgumentException(IdleExitUsage);
}
