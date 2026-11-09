using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiSharp.Durable.Env;

namespace PiSharp.Env.Daemon;

internal static partial class DaemonErrors
{
    public static JsonObject Json(Exception error)
    {
        var failure = error as EnvDaemonException;
        string code = failure?.Code ?? error switch
        {
            OperationCanceledException => "aborted",
            FileNotFoundException or DirectoryNotFoundException => "ENOENT",
            UnauthorizedAccessException => "EACCES",
            ArgumentException or System.Text.Json.JsonException or InvalidOperationException => "EINVAL",
            NotSupportedException => "ENOTSUP",
            FileError file => CodeInMessage().Match(file.Message) is { Success: true } match ? match.Value : file.Code switch
            {
                FileErrorCode.Aborted => "aborted", FileErrorCode.NotFound => "ENOENT", FileErrorCode.PermissionDenied => "EACCES",
                FileErrorCode.NotDirectory => "ENOTDIR", FileErrorCode.IsDirectory => "EISDIR", FileErrorCode.Invalid => "EINVAL",
                FileErrorCode.NotSupported => "ENOTSUP", _ => "unknown"
            },
            IOException io => (io.HResult & 0xffff) switch { 2 or 3 => "ENOENT", 5 or 13 => "EACCES", 17 or 80 or 183 => "EEXIST", 20 or 267 => "ENOTDIR", 21 => "EISDIR", 28 or 112 => "ENOSPC", 32 => "EBUSY", 39 or 145 => "ENOTEMPTY", _ => "unknown" },
            _ => "unknown"
        };
        var json = new JsonObject { ["code"] = code, ["message"] = error.Message };
        if ((failure?.Path ?? (error as FileError)?.Path) is { } path) json["path"] = path;
        if (failure?.Syscall is { } syscall) json["syscall"] = syscall;
        if (failure is not null) foreach (var item in failure.Details) json[item.Key] = item.Value?.DeepClone();
        return json;
    }
    [GeneratedRegex(@"\b(?:ERR_FS_EISDIR|E[A-Z]{2,})\b")]
    private static partial Regex CodeInMessage();
}
