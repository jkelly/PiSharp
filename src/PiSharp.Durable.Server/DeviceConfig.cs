// PiSharp-only (decision 0008, PLAN.md §6). A device's settings live in device.json (name, host, port, start directories, data directory)
// and its token in a separate file only the user can read, under ~/.pi/agent/experimental/durable-device/ unless a data directory says
// otherwise. `pisharp durable serve` reads both and creates the token on first use; `pisharp durable token --rotate` replaces it.
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PiSharp.Durable.Server;

/// <summary>A device's settings (<c>device.json</c>).</summary>
public sealed record DeviceConfig
{
    /// <summary>The device's name; null is the machine name.</summary>
    public string? Name { get; init; }

    /// <summary>The address the server binds; loopback unless the user chooses otherwise.</summary>
    public string Host { get; init; } = "127.0.0.1";

    /// <summary>The port.</summary>
    public int Port { get; init; } = Client.DurableWire.DefaultPort;

    /// <summary>Where new sessions start; not a sandbox. Empty is the user's home directory.</summary>
    public IReadOnlyList<string> StartDirs { get; init; } = [];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The default device directory: <c>~/.pi/agent/experimental/durable-device</c>.</summary>
    public static string DefaultDirectory(string home) => Path.Join(home, ".pi", "agent", "experimental", "durable-device");

    /// <summary>The settings in <paramref name="directory"/>, or the defaults when it has none.</summary>
    /// <exception cref="InvalidDataException">device.json is not valid.</exception>
    public static DeviceConfig Load(string directory)
    {
        var path = Path.Join(directory, "device.json");
        if (!File.Exists(path)) return new DeviceConfig();
        try
        {
            return JsonSerializer.Deserialize<DeviceConfig>(File.ReadAllText(path), Json) ?? new DeviceConfig();
        }
        catch (JsonException failure)
        {
            throw new InvalidDataException($"{path} is not valid: {failure.Message}", failure);
        }
    }

    /// <summary>Writes the settings to <paramref name="directory"/>/device.json.</summary>
    public void Save(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Join(directory, "device.json");
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, Json));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>The token file of a device directory.</summary>
    public static string TokenPath(string directory) => Path.Join(directory, "token");

    /// <summary>The device's token, made and stored (readable by the user only) when there is none.</summary>
    /// <exception cref="InvalidDataException">The stored token is too short.</exception>
    public static string EnsureToken(string directory)
    {
        var path = TokenPath(directory);
        if (File.Exists(path))
        {
            var stored = File.ReadAllText(path).Trim();
            return DeviceTokens.IsStrong(stored) ? stored
                : throw new InvalidDataException($"The token in {path} is shorter than {DeviceTokens.MinimumLength} characters. Run: pisharp durable token --rotate");
        }
        return RotateToken(directory);
    }

    /// <summary>Replaces the device's token; hubs need the new one.</summary>
    public static string RotateToken(string directory)
    {
        Directory.CreateDirectory(directory);
        var token = DeviceTokens.Generate();
        var path = TokenPath(directory);
        var temporary = path + ".tmp";
        if (OperatingSystem.IsWindows())
            File.WriteAllText(temporary, token + Environment.NewLine);
        else
        {
            using var stream = new FileStream(temporary, new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });
            using var writer = new StreamWriter(stream);
            writer.WriteLine(token);
        }
        File.Move(temporary, path, overwrite: true);
        return token;
    }
}
