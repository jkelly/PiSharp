// PiSharp-only (decision 0010, M6). `pisharp durable <group> <command>` drives a device's v1 API from a terminal through DurableDeviceClient,
// as the hub and every other client do: no command reads the device's files. A command connects to the local device (the device directory's
// device.json and token file) unless --device names one, whose token comes from PISHARP_DEVICE_TOKEN or --token-file; a token is never an
// argument. Output is text, or the device's JSON with --json. Exit codes are stable (DurableExit). A write that takes --request sends it as
// its Idempotency-Key (or request ID); without one the command makes one and prints it when the device did not confirm the write, so a
// retry with it cannot act twice. A command of an application feature first checks that the device offers it.
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Cli.Pi;
using PiSharp.Durable.Client;
using PiSharp.Durable.Server;

namespace PiSharp.Cli.Durable;

/// <summary>The stable exit codes of <c>pisharp durable</c>'s remote, attach and operations commands.</summary>
internal static class DurableExit
{
    public const int Ok = 0;
    /// <summary>The device or the command failed in a way no other code describes.</summary>
    public const int Failed = 1;
    /// <summary>The arguments were wrong; nothing was sent.</summary>
    public const int Usage = 2;
    public const int NotFound = 3;
    /// <summary>A stale revision, a request ID reused with another body, or work that is busy or ambiguous.</summary>
    public const int Conflict = 4;
    /// <summary>The credential was refused or lacks the scope; retrying will not help.</summary>
    public const int Unauthorized = 5;
    /// <summary>The device does not offer the feature.</summary>
    public const int Unsupported = 6;
    /// <summary>The device did not answer or is unavailable; retrying may help.</summary>
    public const int Offline = 7;
    /// <summary>The device refused the input as invalid.</summary>
    public const int Invalid = 8;
    /// <summary>A budget, rate or size limit refused the work.</summary>
    public const int Limited = 9;
    public const int Cancelled = 130;

    /// <summary>The exit code of a device error code.</summary>
    public static int Of(string code) => code switch
    {
        DurableErrorCodes.NotFound => NotFound,
        DurableErrorCodes.Conflict or DurableErrorCodes.Ambiguous or DurableErrorCodes.Busy => Conflict,
        DurableErrorCodes.Unauthorized or DurableErrorCodes.Forbidden => Unauthorized,
        DurableErrorCodes.Unsupported => Unsupported,
        DurableErrorCodes.Offline or DurableErrorCodes.Unavailable => Offline,
        DurableErrorCodes.Invalid => Invalid,
        DurableErrorCodes.Limited or DurableErrorCodes.TooLarge => Limited,
        _ => Failed,
    };
}

/// <summary>A usage mistake: nothing was sent.</summary>
internal sealed class RemoteUsageException(string message) : Exception(message);

/// <summary>
/// One remote command. <see cref="Usage"/> is its arguments in usage syntax, and it is also what the parser accepts: <c>--name &lt;v&gt;</c> is
/// an option, a bare <c>--name</c> a flag, anything else a positional argument (<c>a|b</c> one of the choices, a trailing <c>...</c> one or
/// more); brackets make a part optional.
/// </summary>
internal sealed record RemoteCommand(string Group, string Verb, string Usage, string Summary, string? Feature, Func<RemoteCall, Task<JsonNode?>> Run)
{
    /// <summary>The fields of a listed item shown as columns in text output; <c>#field</c> counts an array.</summary>
    public string[] Columns { get; init; } = ["id"];

    /// <summary>Text output other than the default list and field rendering.</summary>
    public Func<JsonNode?, string>? Text { get; init; }

    /// <summary>The command's name after <c>pisharp durable</c>.</summary>
    public string Name => Verb.Length == 0 ? Group : Group + " " + Verb;

    /// <summary>Whether it takes <c>--request</c>.</summary>
    public bool Writes => Usage.Contains("--request", StringComparison.Ordinal);

    internal UsageShape Shape => shape ??= UsageShape.Parse(Usage);

    private UsageShape? shape;
}

/// <summary>What a usage string accepts.</summary>
internal sealed record UsageShape(IReadOnlyDictionary<string, bool> Options, IReadOnlySet<string> Flags, IReadOnlyList<(string Name, bool Required, string[]? Choices, bool Repeats)> Positional)
{
    public static UsageShape Parse(string usage)
    {
        var options = new Dictionary<string, bool>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        var positional = new List<(string, bool, string[]?, bool)>();
        var tokens = usage.Replace("[", " [ ", StringComparison.Ordinal).Replace("]", " ] ", StringComparison.Ordinal)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var depth = 0;
        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (token == "[") depth++;
            else if (token == "]") depth--;
            else if (token.StartsWith("--", StringComparison.Ordinal))
            {
                if (i + 1 < tokens.Length && tokens[i + 1].StartsWith('<'))
                {
                    options[token] = depth == 0;
                    i++;
                }
                else flags.Add(token);
            }
            else
            {
                var rest = token.EndsWith("...", StringComparison.Ordinal);
                var name = rest ? token[..^3] : token;
                positional.Add((name, depth == 0, name.Contains('<', StringComparison.Ordinal) ? null : name.Split('|'), rest));
            }
        }
        return new UsageShape(options, flags, positional);
    }
}

/// <summary>The parsed arguments of one command.</summary>
internal sealed class RemoteArgs
{
    /// <summary>Options every remote command takes: where the device is, where its token is.</summary>
    internal static readonly string[] Connection = ["--device", "--token-file", "--data-dir"];

    private readonly Dictionary<string, List<string>> options = new(StringComparer.Ordinal);
    private readonly HashSet<string> flags = new(StringComparer.Ordinal);

    public List<string> Positional { get; } = [];

    /// <exception cref="RemoteUsageException">The arguments do not fit <paramref name="shape"/>.</exception>
    public static RemoteArgs Parse(IReadOnlyList<string> args, UsageShape shape)
    {
        var parsed = new RemoteArgs();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg is "--token" || arg.StartsWith("--token=", StringComparison.Ordinal))
                throw new RemoteUsageException("A token is never an argument: set PISHARP_DEVICE_TOKEN or pass --token-file <path>.");
            if (!arg.StartsWith("--", StringComparison.Ordinal) || arg.Length == 2)
            {
                parsed.Positional.Add(arg);
                continue;
            }
            if (arg == "--json" || shape.Flags.Contains(arg))
            {
                parsed.flags.Add(arg);
                continue;
            }
            if (!Connection.Contains(arg) && !shape.Options.ContainsKey(arg)) throw new RemoteUsageException($"Unknown option {arg}.");
            if (i + 1 >= args.Count) throw new RemoteUsageException($"{arg} needs a value.");
            if (!parsed.options.TryGetValue(arg, out var values)) parsed.options[arg] = values = [];
            values.Add(args[++i]);
        }
        foreach (var (option, required) in shape.Options)
            if (required && !parsed.options.ContainsKey(option)) throw new RemoteUsageException($"{option} is required.");
        var index = 0;
        foreach (var (name, required, choices, rest) in shape.Positional)
        {
            if (index >= parsed.Positional.Count)
            {
                if (required) throw new RemoteUsageException($"{name} is required.");
                break;
            }
            var take = rest ? parsed.Positional.Count - index : 1;
            foreach (var value in parsed.Positional.Skip(index).Take(take))
                if (choices is not null && !choices.Contains(value, StringComparer.Ordinal))
                    throw new RemoteUsageException($"{value} is not one of {string.Join(", ", choices)}.");
            index += take;
        }
        if (index < parsed.Positional.Count) throw new RemoteUsageException($"Unexpected argument {parsed.Positional[index]}.");
        return parsed;
    }

    public string? Option(string name) => options.TryGetValue(name, out var values) ? values[^1] : null;

    public IReadOnlyList<string> Options(string name) => options.TryGetValue(name, out var values) ? values : [];

    public bool Flag(string name) => flags.Contains(name);

    /// <exception cref="RemoteUsageException">The option is missing.</exception>
    public string Required(string name) => Option(name) ?? throw new RemoteUsageException($"{name} is required.");

    /// <exception cref="RemoteUsageException">The option is not a whole number.</exception>
    public long? Long(string name) => Option(name) is not { } text ? null
        : long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : throw new RemoteUsageException($"{name} must be a whole number.");
}

/// <summary>One command's call: the client, its arguments, and the request ID of a write.</summary>
internal sealed class RemoteCall(DurableDeviceClient client, RemoteArgs args, string cwd, TextReader stdin, CancellationToken cancellationToken)
{
    private string? requestId;

    public DurableDeviceClient Client => client;

    public RemoteArgs Args => args;

    public CancellationToken Token => cancellationToken;

    /// <summary>The session the command works in (<c>--session</c>, an ID or a name).</summary>
    public string Session => args.Required("--session");

    /// <summary>The write's request ID: <c>--request</c>, or one made for this call.</summary>
    public string RequestId => requestId ??= args.Option("--request") ?? "cli-" + Guid.NewGuid().ToString("n");

    /// <summary>The request ID a write used, when it made one; the CLI prints it when the write was not confirmed.</summary>
    public string? MadeRequestId => requestId is not null && args.Option("--request") is null ? requestId : null;

    public string Arg(int index) => args.Positional[index];

    public string? OptionalArg(int index) => index < args.Positional.Count ? args.Positional[index] : null;

    /// <exception cref="RemoteUsageException">The argument is not a whole number.</exception>
    public long LongArg(int index) => long.TryParse(Arg(index), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
        ? value : throw new RemoteUsageException($"{Arg(index)} is not a whole number.");

    /// <summary>A required <c>--revision</c>.</summary>
    public long Revision => args.Long("--revision") ?? throw new RemoteUsageException("--revision is required.");

    /// <summary>The JSON object in the file an option names (<c>-</c> reads standard input).</summary>
    /// <exception cref="InvalidDataException">The file does not hold a JSON object.</exception>
    public JsonObject File(string option)
    {
        var path = args.Required(option);
        var text = path == "-" ? stdin.ReadToEnd() : System.IO.File.ReadAllText(Path.GetFullPath(path, cwd));
        try
        {
            return JsonNode.Parse(text) as JsonObject ?? throw new InvalidDataException($"{path} must hold a JSON object.");
        }
        catch (JsonException failure)
        {
            throw new InvalidDataException($"{path} is not JSON: {failure.Message}", failure);
        }
    }

    /// <summary>Writes <paramref name="node"/> to <c>--out</c> when it is given; the result says where.</summary>
    public JsonNode? Save(JsonNode? node)
    {
        if (args.Option("--out") is not { } path) return node;
        var full = Path.GetFullPath(path, cwd);
        System.IO.File.WriteAllText(full, node?.ToJsonString(DurableRemote.Indented) ?? "null");
        return new JsonObject { ["written"] = full };
    }
}

/// <summary>The remote commands and their runner.</summary>
internal static partial class DurableRemote
{
    internal static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>The groups the remote commands use, so <c>pisharp durable</c> can tell a command from the TUI's arguments.</summary>
    internal static IReadOnlySet<string> Groups => groups ??= Commands.Select(command => command.Group).ToHashSet(StringComparer.Ordinal);

    private static HashSet<string>? groups;

    /// <summary>The usage of every remote command.</summary>
    internal static string Help(string? group = null)
    {
        var text = new StringBuilder();
        if (group is null)
        {
            text.AppendLine("Usage: pisharp durable <command> [arguments] [--json] [--device <url> [--token-file <path>] | --data-dir <dir>]");
            text.AppendLine("Without --device a command uses the local device (its device.json and token file). With --device the token comes from");
            text.AppendLine("PISHARP_DEVICE_TOKEN or --token-file. Exit codes: 0 ok, 1 failed, 2 usage, 3 not found, 4 conflict, 5 unauthorized,");
            text.AppendLine("6 unsupported, 7 offline (retryable), 8 invalid, 9 limited, 130 cancelled.");
            text.AppendLine();
        }
        foreach (var command in Commands.Where(command => group is null || command.Group == group))
            text.AppendLine($"  pisharp durable {command.Name} {command.Usage}".TrimEnd() + $"\n      {command.Summary}");
        return text.ToString();
    }

    /// <summary>Runs a remote command; <paramref name="args"/> start with its group.</summary>
    internal static async Task<int> RunAsync(string[] args, PiHost host, CancellationToken cancellationToken)
    {
        var group = Commands.Where(command => command.Group == args[0]).ToList();
        var command = group.FirstOrDefault(item => item.Verb.Length == 0) ?? (args.Length > 1 ? group.FirstOrDefault(item => item.Verb == args[1]) : null);
        if (command is null)
        {
            await host.Stderr.WriteLineAsync((args.Length > 1 ? $"Unknown command: {args[0]} {args[1]}\n" : "") + Help(args[0])).ConfigureAwait(false);
            return DurableExit.Usage;
        }
        var json = args.Contains("--json", StringComparer.Ordinal);
        RemoteCall? call = null;
        try
        {
            var parsed = RemoteArgs.Parse(args[(command.Verb.Length == 0 ? 1 : 2)..], command.Shape);
            var (url, token) = Connect(parsed, host);
            using var client = new DurableDeviceClient(url, token);
            if (command.Feature is { } feature)
            {
                var info = await client.DeviceAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                if (!info.Supports(feature))
                    throw new DurableApiException(DurableErrorCodes.Unsupported,
                        $"The device \"{info.Name}\" at {url} does not offer {feature} (v1); update its PiSharp Durable host or use a device that does.");
            }
            call = new RemoteCall(client, parsed, host.Cwd, host.Stdin, cancellationToken);
            var result = await command.Run(call).ConfigureAwait(false);
            await host.Stdout.WriteLineAsync(json ? result?.ToJsonString(Indented) ?? "null" : Render(command, result)).ConfigureAwait(false);
            await host.Stdout.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            return DurableExit.Ok;
        }
        catch (RemoteUsageException usage)
        {
            await host.Stderr.WriteLineAsync($"{usage.Message}\nUsage: pisharp durable {command.Name} {command.Usage}".TrimEnd()).ConfigureAwait(false);
            return DurableExit.Usage;
        }
        catch (DurableApiException error)
        {
            return await FailAsync(host, json, error.Code, error.Message, error.Fields, call).ConfigureAwait(false);
        }
        catch (HttpRequestException error)
        {
            return await FailAsync(host, json, DurableErrorCodes.Offline, error.Message, [], call).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await host.Stderr.WriteLineAsync(call?.MadeRequestId is { } id ? $"Cancelled. If the write went through, retry with --request {id} to see it." : "Cancelled.")
                .ConfigureAwait(false);
            return DurableExit.Cancelled;
        }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or UriFormatException)
        {
            return await FailAsync(host, json, DurableErrorCodes.Invalid, error.Message, [], call).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Where the command connects: <c>--device</c> with the token from <c>--token-file</c> or <c>PISHARP_DEVICE_TOKEN</c>, or else the local
    /// device's address from its device.json and its token file.
    /// </summary>
    /// <exception cref="RemoteUsageException">No device or no token.</exception>
    internal static (Uri Url, string Token) Connect(RemoteArgs args, PiHost host)
    {
        var tokenFile = args.Option("--token-file") is { } file ? Path.GetFullPath(PiPaths.NormalizePath(file, host.Home), host.Cwd) : null;
        if (args.Option("--device") is { } device)
        {
            if (!Uri.TryCreate(device, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
                throw new RemoteUsageException("--device must be an http or https URL.");
            if (url.UserInfo.Length > 0 || url.Query.Contains("token", StringComparison.OrdinalIgnoreCase))
                throw new RemoteUsageException("Put no credential in the device URL: set PISHARP_DEVICE_TOKEN or pass --token-file <path>.");
            var token = tokenFile is not null ? ReadToken(tokenFile)
                : host.GetEnvironment("PISHARP_DEVICE_TOKEN") is { Length: > 0 } fromEnvironment ? fromEnvironment.Trim()
                : throw new RemoteUsageException($"No token for {url}: set PISHARP_DEVICE_TOKEN or pass --token-file <path>.");
            return (url, token);
        }
        var directory = DataDirectory(args.Option("--data-dir"), host);
        var config = DeviceConfig.Load(directory);
        var address = config.Host is "0.0.0.0" or "::" or "" ? "127.0.0.1" : config.Host;
        var local = new Uri($"http://{(address.Contains(':', StringComparison.Ordinal) ? $"[{address}]" : address)}:{config.Port.ToString(CultureInfo.InvariantCulture)}/");
        var path = tokenFile ?? DeviceConfig.TokenPath(directory);
        if (!File.Exists(path))
            throw new RemoteUsageException($"No device token at {path}: start the device with `pisharp durable serve`, or pass --device <url>.");
        return (local, ReadToken(path));
    }

    /// <summary>The device directory: <paramref name="option"/>, or the default under the user's home.</summary>
    internal static string DataDirectory(string? option, PiHost host) =>
        Path.GetFullPath(option is { } given ? PiPaths.NormalizePath(given, host.Home) : DeviceConfig.DefaultDirectory(host.Home), host.Cwd);

    private static string ReadToken(string path)
    {
        var token = File.ReadAllText(path).Trim();
        return token.Length > 0 ? token : throw new InvalidDataException($"{path} holds no token.");
    }

    private static async Task<int> FailAsync(PiHost host, bool json, string code, string message, IReadOnlyList<ApiFieldError> fields, RemoteCall? call)
    {
        var exit = DurableExit.Of(code);
        var unconfirmed = exit is DurableExit.Offline or DurableExit.Failed && call?.MadeRequestId is not null ? call.MadeRequestId : null;
        if (json)
        {
            var error = new JsonObject { ["code"] = code, ["message"] = message };
            if (fields.Count > 0) error["fields"] = new JsonArray([.. fields.Select(field => (JsonNode)new JsonObject { ["path"] = field.Path, ["message"] = field.Message })]);
            if (unconfirmed is not null) error["requestId"] = unconfirmed;
            await host.Stderr.WriteLineAsync(new JsonObject { ["error"] = error }.ToJsonString()).ConfigureAwait(false);
        }
        else
        {
            var text = new StringBuilder($"{code}: {message}");
            foreach (var field in fields) text.Append(CultureInfo.InvariantCulture, $"\n  {field.Path}: {field.Message}");
            if (unconfirmed is not null) text.Append(CultureInfo.InvariantCulture, $"\nThe device did not confirm the write. Retry with --request {unconfirmed} so it cannot happen twice.");
            await host.Stderr.WriteLineAsync(text.ToString()).ConfigureAwait(false);
        }
        await host.Stderr.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        return exit;
    }

    // ─── Text output ───────────────────────────────────────────────────────

    internal static string Render(RemoteCommand command, JsonNode? node)
    {
        if (command.Text is { } text) return text(node);
        if (node is JsonValue value && value.TryGetValue<string>(out var plain)) return plain;
        if (node is JsonObject listed && listed["items"] is JsonArray items)
            return Table(items, command.Columns) + (listed["next"] is { } next ? "\nnext: " + Cursor(next) : "");
        if (node is JsonArray array) return Table(array, command.Columns);
        if (node is JsonObject json) return Fields(json);
        return node?.ToJsonString() ?? "";
    }

    /// <summary>A table of <paramref name="items"/>: one row each, a column per field path.</summary>
    internal static string Table(JsonArray items, IReadOnlyList<string> columns)
    {
        if (items.Count == 0) return "(none)";
        var rows = new List<string[]> { columns.Select(column => column.TrimStart('#').ToUpperInvariant()).ToArray() };
        rows.AddRange(items.Select(item => columns.Select(column => Cell(item, column)).ToArray()));
        var widths = columns.Select((_, index) => rows.Max(row => row[index].Length)).ToArray();
        return string.Join('\n', rows.Select(row => string.Join("  ", row.Select((cell, index) => index == row.Length - 1 ? cell : cell.PadRight(widths[index]))).TrimEnd()));
    }

    private static string Cell(JsonNode? item, string column)
    {
        if (column.StartsWith('#')) return (At(item, column[1..]) as JsonArray)?.Count.ToString(CultureInfo.InvariantCulture) ?? "0";
        var text = Scalar(At(item, column));
        return text.Length > 48 ? text[..47] + "…" : text;
    }

    /// <summary>The node at a dotted path.</summary>
    internal static JsonNode? At(JsonNode? node, string path)
    {
        foreach (var part in path.Split('.'))
            node = node is JsonObject json ? json[part] : null;
        return node;
    }

    private static string Scalar(JsonNode? node) => node switch
    {
        null => "-",
        JsonValue value when value.TryGetValue<string>(out var text) => text.ReplaceLineEndings(" "),
        JsonValue value when value.TryGetValue<double>(out var number) => number.ToString(CultureInfo.InvariantCulture),
        JsonValue value => value.ToJsonString(),
        _ => node.ToJsonString(),
    };

    /// <summary>The top-level fields of an object, one per line; nested values as compact JSON.</summary>
    internal static string Fields(JsonObject json) => json.Count == 0 ? "(empty)" : string.Join('\n', json.Select(pair =>
    {
        var value = pair.Value is JsonObject or JsonArray ? pair.Value.ToJsonString() : Scalar(pair.Value);
        return $"{pair.Key}: {(value.Length > 160 ? value[..159] + "…" : value)}";
    }));

    /// <summary>A list's <c>next</c> as an opaque cursor: a device's string as it is, an object cursor encoded.</summary>
    internal static string Cursor(JsonNode next) => next is JsonValue value && value.TryGetValue<string>(out var text) ? text
        : "c1." + Convert.ToBase64String(Encoding.UTF8.GetBytes(next.ToJsonString())).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>An object cursor <see cref="Cursor"/> encoded.</summary>
    /// <exception cref="RemoteUsageException">It is not one.</exception>
    internal static JsonObject? ObjectCursor(string? cursor)
    {
        if (cursor is null) return null;
        try
        {
            if (!cursor.StartsWith("c1.", StringComparison.Ordinal)) throw new FormatException();
            var base64 = cursor[3..].Replace('-', '+').Replace('_', '/');
            return JsonNode.Parse(Convert.FromBase64String(base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '='))) as JsonObject ?? throw new FormatException();
        }
        catch (Exception error) when (error is FormatException or JsonException)
        {
            throw new RemoteUsageException("--cursor is not a cursor this command printed.");
        }
    }
}
