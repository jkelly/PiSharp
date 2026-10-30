// Decision 0011 (run W3, unit AP): what Settings › About and its diagnostics report say about this build and this host, all read from the
// running process, never typed in: the app's and PiSharp's versions (the assemblies' informational versions, whose "+<commit>" names the
// source; a PiSharp version names the Pi release it tracks in its first three numbers, README), the build configuration and the date the
// app's assembly was written, the .NET and ASP.NET Core runtimes, the OS, the addresses Kestrel listens on (always loopback:
// WebAgentHost refuses anything else at start), the uptime of the process and the data folder.
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using PiSharp.WebAgent.Hosting;

namespace PiSharp.WebAgent.About;

/// <summary>The versions of this build.</summary>
/// <param name="App">The Web Agent's version, such as <c>1.2.0.1</c>.</param>
/// <param name="PiSharp">PiSharp's version (the PiSharp.AI assembly).</param>
/// <param name="TracksPi">The Pi release PiSharp tracks: the first three numbers of its version.</param>
/// <param name="Commit">The source commit (the first 8 hex digits), or null when the build did not record one.</param>
/// <param name="Configuration">The build configuration, such as <c>Release</c>.</param>
/// <param name="Built">When the app's assembly was written (UTC), or null when unknown.</param>
/// <param name="Runtime">The .NET runtime, such as <c>.NET 10.0.3</c>.</param>
/// <param name="AspNetCore">ASP.NET Core's version.</param>
public sealed record VersionFacts(string App, string PiSharp, string TracksPi, string? Commit, string Configuration, DateTimeOffset? Built, string Runtime,
    string AspNetCore);

/// <summary>Facts about the host the app runs on.</summary>
/// <param name="Os">The OS and architecture, such as <c>Microsoft Windows 10.0.26300 · X64</c>.</param>
/// <param name="Addresses">The addresses the server listens on (empty when unknown, such as before it started).</param>
/// <param name="LoopbackOnly">Whether every address is loopback (127.0.0.1, localhost or [::1]).</param>
/// <param name="Started">When the process started (UTC).</param>
/// <param name="Uptime">How long it has run.</param>
/// <param name="DataDirectory">The data folder.</param>
/// <param name="Faux">Whether the app runs offline on faux models (<c>--faux</c>).</param>
public sealed record HostFacts(string Os, IReadOnlyList<string> Addresses, bool LoopbackOnly, DateTimeOffset Started, TimeSpan Uptime, string DataDirectory,
    bool Faux);

/// <summary>Reads the versions and host facts. A singleton; safe from many threads.</summary>
public sealed class AboutFacts(WebAgentOptions options, IServer? server = null, TimeProvider? time = null)
{
    private static readonly Lazy<VersionFacts> CachedVersions = new(ReadVersions);
    private static readonly DateTimeOffset ProcessStarted = ReadProcessStart();
    private readonly TimeProvider clock = time ?? TimeProvider.System;

    /// <summary>The versions of this build.</summary>
    public VersionFacts Versions => CachedVersions.Value;

    /// <summary>The host facts now.</summary>
    public HostFacts Host()
    {
        var addresses = server?.Features.Get<IServerAddressesFeature>()?.Addresses.ToList() ?? [];
        var now = clock.GetUtcNow();
        var uptime = now - ProcessStarted;
        return new HostFacts(RuntimeInformation.OSDescription.Trim() + " · " + RuntimeInformation.OSArchitecture, addresses,
            addresses.Count > 0 && addresses.All(IsLoopback), ProcessStarted, uptime < TimeSpan.Zero ? TimeSpan.Zero : uptime, options.DataDirectory, options.Faux);
    }

    /// <summary>Whether <paramref name="address"/> (such as <c>http://127.0.0.1:5180</c>) is on this computer only.</summary>
    public static bool IsLoopback(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)) return false;
        return uri.Host == "localhost" || (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));
    }

    /// <summary>"2d 3h", "2h 14m", "5m 12s", "40s".</summary>
    public static string Duration(TimeSpan span)
    {
        if (span.TotalDays >= 1) return $"{(int)span.TotalDays}d {span.Hours}h";
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h {span.Minutes}m";
        if (span.TotalMinutes >= 1) return $"{(int)span.TotalMinutes}m {span.Seconds}s";
        return $"{Math.Max(0, (int)span.TotalSeconds)}s";
    }

    /// <summary>The version and commit of <paramref name="assembly"/>'s informational version (<c>1.2.0.1+abcdef…</c>).</summary>
    public static (string Version, string? Commit) VersionOf(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString() ?? "unknown";
        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        if (plus < 0) return (informational, null);
        var commit = informational[(plus + 1)..];
        return (informational[..plus], commit.Length >= 8 && commit.All(Uri.IsHexDigit) ? commit[..8] : null);
    }

    /// <summary>The Pi release a PiSharp version tracks: its first three numbers (<c>1.2.0.1</c> → <c>1.2.0</c>).</summary>
    public static string TracksPi(string version)
    {
        ArgumentNullException.ThrowIfNull(version);
        var parts = version.Split('-', 2)[0].Split('.');
        return parts.Length >= 3 ? string.Join('.', parts.Take(3)) : version;
    }

    private static VersionFacts ReadVersions()
    {
        var app = typeof(AboutFacts).Assembly;
        var (appVersion, commit) = VersionOf(app);
        var (piSharp, _) = VersionOf(typeof(PiSharp.AI.ThinkingLevels).Assembly);
        var (aspNet, _) = VersionOf(typeof(WebApplication).Assembly);
        var configuration = app.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "unknown";
        DateTimeOffset? built = null;
        try
        {
            if (app.Location is { Length: > 0 } location && File.Exists(location)) built = new DateTimeOffset(File.GetLastWriteTimeUtc(location), TimeSpan.Zero);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { built = null; }
        return new VersionFacts(appVersion, piSharp, TracksPi(piSharp), commit, configuration, built, RuntimeInformation.FrameworkDescription, aspNet);
    }

    private static DateTimeOffset ReadProcessStart()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            return DateTimeOffset.UtcNow;
        }
    }
}
