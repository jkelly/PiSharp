// Pi 7d073328e44337d3292a7db9b0f0308f679c5e97 (MIT): packages/env/src/connection.ts.
using System.Text.Json.Nodes;

namespace PiSharp.Env;

public sealed record ConnectionOptions
{
    /// <summary>Executable and arguments before the appended serve --token arguments.</summary>
    public IReadOnlyList<string>? Command { get; init; }
    /// <summary>Recomputed on each start; the token belongs to the connection, never to one request.</summary>
    public Func<CancellationToken, Task<IReadOnlyList<string>>>? CommandFactory { get; init; }
    public Action<string>? OnLog { get; init; }
    /// <summary>Optional artifact-specific validation after protocol validation, before exposing a ready connection.</summary>
    public Action<RemoteInfo>? ValidateHello { get; init; }
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan SilenceLimit { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan AbortGrace { get; init; } = TimeSpan.FromSeconds(1);
    /// <summary>Maximum time disposal waits for original startup work and process cleanup. A timeout fails disposal;
    /// it never reports successful cleanup while a custom factory is still running.</summary>
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(5);
}

public sealed record RemoteRequestOptions
{
    public ReadOnlyMemory<byte> Payload { get; init; }
    public long? Session { get; init; }
    public Action<JsonObject, ReadOnlyMemory<byte>>? OnEvent { get; init; }
    public Action<uint, long>? OnStart { get; init; }
}

public sealed record RemoteReply(JsonObject Json, byte[] Payload, long Session);

public sealed record RemoteInfo
{
    public required int Protocol { get; init; }
    public required string Version { get; init; }
    public required string Os { get; init; }
    public required string Arch { get; init; }
    public required string Home { get; init; }
    public required string Tmpdir { get; init; }
    public required string Separator { get; init; }
    public required string Cwd { get; init; }
    public required int Pid { get; init; }
    public IReadOnlyDictionary<string, string> DriveCwds { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<string> Features { get; init; } = [];

    internal static RemoteInfo Parse(JsonObject json)
    {
        string Text(string name) => json[name]?.GetValue<string>() ?? throw new InvalidDataException("Missing pi-env hello field: " + name);
        return new()
        {
            Protocol = json["protocol"]?.GetValue<int>() ?? throw new InvalidDataException("Missing pi-env protocol."),
            Version = Text("version"), Os = Text("os"), Arch = Text("arch"), Home = Text("home"),
            Tmpdir = Text("tmpdir"), Separator = Text("separator"), Cwd = Text("cwd"),
            Pid = json["pid"]?.GetValue<int>() ?? throw new InvalidDataException("Missing pi-env pid."),
            DriveCwds = json["driveCwds"] is JsonObject drives
                ? drives.ToDictionary(pair => pair.Key, pair => pair.Value?.GetValue<string>() ?? "", StringComparer.Ordinal)
                : new Dictionary<string, string>(),
            Features = json["features"] is JsonArray features ? features.Select(value => value!.GetValue<string>()).ToArray() : [],
        };
    }
}

public static class RemotePlatformNames
{
    public static (string Platform, string Arch) NodePlatform(RemoteInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return (info.Os switch { "windows" => "win32", "macos" => "darwin", _ => info.Os },
            info.Arch switch { "x86_64" => "x64", "aarch64" => "arm64", "x86" => "ia32", _ => info.Arch });
    }
}

public sealed class RemoteError : Exception
{
    public string Code { get; }
    public string? Path { get; }
    public JsonObject Fields { get; }
    public bool IsConnectionLost => Fields["lost"] is JsonValue value && value.TryGetValue<bool>(out var lost) && lost;

    public RemoteError(JsonObject fields) : base(Text(fields, "message") ?? "Remote operation failed")
    {
        Fields = (JsonObject)fields.DeepClone();
        Code = Text(fields, "code") ?? "unknown";
        Path = Text(fields, "path");
    }

    internal static RemoteError Lost(string message) => new(new() { ["code"] = "unknown", ["message"] = message, ["lost"] = true });
    internal static RemoteError Aborted() => new(new() { ["code"] = "aborted", ["message"] = "aborted" });
    internal static RemoteError Closed() => new(new() { ["code"] = "unknown", ["message"] = "Connection closed" });
    private static string? Text(JsonObject fields, string name) =>
        fields[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
