using System.Globalization;
using System.Text;
using System.Text.Json;
using PiSharp.Sessions.Lifecycle;
using PiSharp.CodingAgent;

namespace PiSharp.Cli.Commands;

/// <summary>Explicit-store readonly header discovery; no provider, tools, plugin or writer is acquired.</summary>
public static class SessionCatalogCommand
{
    public const string Flags = "[--session-store <storeId=absoluteDirectory> (repeatable)]";
    public const string Usage = "session list --session-store <storeId=absoluteDirectory> [--session-store ...] [--page-size 1..128] [--cursor <cursor>] [--cwd <working directory>]";
    internal static SessionCatalogStore ParseStore(string value)
    {
        var separator = value.IndexOf('=');
        if (separator is < 1 or > 64) throw new SessionCommandException(SessionCommandFailure.InvalidArguments);
        var id = value[..separator];
        if (id.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
            throw new SessionCommandException(SessionCommandFailure.InvalidArguments);
        return new(id, SessionCommands.Absolute(value[(separator + 1)..]));
    }
    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(stdout); ArgumentNullException.ThrowIfNull(stderr);
        try
        {
            if (args is not ["session", "list", ..] || args.Length is < 4 or > 76 || args.Length % 2 != 0)
                throw new SessionCommandException(SessionCommandFailure.InvalidArguments);
            var stores = new List<SessionCatalogStore>(); var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var index = 2; index < args.Length; index += 2)
                if (args[index] == "--session-store") stores.Add(ParseStore(args[index + 1]));
                else if (args[index] is not ("--page-size" or "--cursor" or "--cwd") || !values.TryAdd(args[index], args[index + 1]))
                    throw new SessionCommandException(SessionCommandFailure.InvalidArguments);
            var size = 32;
            if (values.TryGetValue("--page-size", out var pageSize) &&
                (!int.TryParse(pageSize, NumberStyles.None, CultureInfo.InvariantCulture, out size) || size is < 1 or > 128))
                throw new SessionCommandException(SessionCommandFailure.InvalidArguments);
            values.TryGetValue("--cursor", out var cursor); values.TryGetValue("--cwd", out var cwd);
            var page = await new SessionLifecycleReadOnly(catalog: new SessionCatalog(stores)).ListAsync(new(size, cursor, cwd), token).ConfigureAwait(false);
            var output = JsonSerializer.Serialize(new { schemaVersion = 1, headerOnly = true, page },
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            if (Encoding.UTF8.GetByteCount(output) > 1_048_576) throw new SessionCatalogException(SessionCatalogFailure.ResourceLimit);
            token.ThrowIfCancellationRequested(); await stdout.WriteLineAsync(output.AsMemory(), token).ConfigureAwait(false);
            await stdout.FlushAsync(token).ConfigureAwait(false); return 0;
        }
        catch (Exception error)
        {
            var message = error is SessionCatalogException or SessionCommandException ? error.Message :
                error is OperationCanceledException ? "Session listing canceled after reader cleanup." : "Session listing failed.";
            await stderr.WriteLineAsync(JsonSerializer.Serialize(new { schemaVersion = 1, status = "failed", message })).ConfigureAwait(false);
            return error is SessionCommandException or ArgumentException ? 2 : 1;
        }
    }
}
