using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PiSharp.Agent.Tools;
using PiSharp.Contracts;

namespace PiSharp.CodingAgent.Execution;

public sealed record UserBashExecutionRequest(string Command, string WorkingDirectory,
    bool? ExcludeFromContext = null, string? Id = null);
public sealed record UserBashResult(
    [property: JsonPropertyName("output")] string Output,
    [property: JsonPropertyName("exitCode"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ExitCode,
    [property: JsonPropertyName("cancelled")] bool Cancelled,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("fullOutputPath"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FullOutputPath = null);
public sealed record UserBashExecutionUpdate(string? Id, string Delta);
public delegate Task UserBashProgress(string sanitizedDelta);

/// <summary>An explicitly admitted capability. Return the original task that joins physical shutdown,
/// process/output cleanup and accepted progress callbacks. Prefix/shell/environment/spill permissions
/// belong to the host. No native capability is supplied by the session or these contracts.</summary>
public interface IUserBashExecutor
{
    Task<UserBashResult> ExecuteAsync(UserBashExecutionRequest request, UserBashProgress progress,
        CancellationToken cancellationToken);
}

/// <summary>Bounded sanitized wire profile derived from pinned Pi BashResult/BashExecutionMessage.
/// This class neither sanitizes raw process bytes nor acquires spill storage.</summary>
public static class UserBash
{
    public static void Validate(UserBashExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Source executeBash has no command length limit: the operating system's spawn limit applies. The bound only caps memory,
        // as the model's bash tool does (BashToolOptions.MaximumCommandCharacters).
        // A NUL byte reaches the shell operations, which fail with Node's spawn argument error as the source does.
        if (!Scalar(request.Command, allowNul: true) || request.Command.Length > 96_000 || !Absolute(request.WorkingDirectory) ||
            request.Id is not null && (!Scalar(request.Id) || request.Id.Length > 4096))
            throw new ArgumentException("User Bash request exceeds the admitted text/path profile.", nameof(request));
    }
    public static void Validate(UserBashResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!Sanitized(result.Output) || result.Output.Length > 51_200 || ToolOutputTruncator.Tail(result.Output).Truncated ||
            result.Cancelled && result.ExitCode is not null || result.Truncated && result.FullOutputPath is null ||
            result.FullOutputPath is not null && !Absolute(result.FullOutputPath))
            throw new ArgumentException("User Bash result is not a bounded sanitized finalized tail.", nameof(result));
    }
    public static void ValidateDelta(string delta)
    {
        if (!Sanitized(delta) || delta.Length > 65_536)
            throw new ArgumentException("User Bash progress must be a bounded sanitized original delta.", nameof(delta));
    }
    public static JsonData RpcData(UserBashResult result)
    { Validate(result); return JsonData.Parse(JsonSerializer.Serialize(result)); }
    internal static JsonData Message(UserBashExecutionRequest request, UserBashResult result, long timestamp)
    {
        Validate(request); Validate(result);
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes))
        {
            writer.WriteStartObject(); writer.WriteString("role", "bashExecution");
            writer.WriteString("command", request.Command); writer.WriteString("output", result.Output);
            if (result.ExitCode is { } exit) writer.WriteNumber("exitCode", exit);
            writer.WriteBoolean("cancelled", result.Cancelled); writer.WriteBoolean("truncated", result.Truncated);
            if (result.FullOutputPath is { } full) writer.WriteString("fullOutputPath", full);
            writer.WriteNumber("timestamp", timestamp);
            if (request.ExcludeFromContext is { } excluded) writer.WriteBoolean("excludeFromContext", excluded);
            writer.WriteEndObject();
        }
        return JsonData.Parse(Encoding.UTF8.GetString(bytes.ToArray()));
    }
    private static bool Absolute(string? text)
    {
        if (!Scalar(text) || text!.Length is < 1 or > 4096) return false;
        try { return Path.IsPathFullyQualified(text) && Path.GetFullPath(text) == text; }
        catch (Exception error) when (error is ArgumentException or NotSupportedException) { return false; }
    }
    private static bool Sanitized(string? text) => Scalar(text) && !text!.Any(character =>
        character is <= '\x08' or '\x0b' or '\x0c' or '\r' or >= '\x0e' and <= '\x1f' or '\x9b' or >= '\ufff9' and <= '\ufffb');
    private static bool Scalar(string? text, bool allowNul = false)
    {
        if (text is null || !allowNul && text.Contains('\0')) return false;
        for (var index = 0; index < text.Length; index++)
        {
            if (char.IsHighSurrogate(text[index]))
            { if (++index >= text.Length || !char.IsLowSurrogate(text[index])) return false; }
            else if (char.IsLowSurrogate(text[index])) return false;
        }
        return true;
    }
}
