// Decision 0011 (run W4, R5): why an agent compare request can't go ahead, with the HTTP status and code /api/compare/agent answers
// ({error: {code, message}}).
namespace PiSharp.WebAgent.AgentCompare.Runner;

/// <summary>An agent compare request that can't go ahead; <see cref="Code"/> is one of the constants here.</summary>
public sealed class AgentCompareException(int status, string code, string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>A value is missing or not allowed (400).</summary>
    public const string Invalid = "invalid";

    /// <summary>No such run or model (404).</summary>
    public const string NotFound = "not-found";

    /// <summary>The model (or the run) is still running (409).</summary>
    public const string Busy = "busy";

    /// <summary>The model's workspace was discarded (409).</summary>
    public const string Discarded = "discarded";

    /// <summary>The model continues as a chat, whose working directory is its workspace (409).</summary>
    public const string InChat = "in-chat";

    /// <summary>A copy is larger than the warning size and was not confirmed (409, with the size in the message).</summary>
    public const string TooLarge = "too-large";

    /// <summary>Worktrees were asked for outside a git repository (400).</summary>
    public const string NotGit = "not-git";

    /// <summary>The project folder is missing (400).</summary>
    public const string ProjectMissing = "project-missing";

    /// <summary>The approval is no longer waiting (409).</summary>
    public const string NotPending = "not-pending";

    /// <summary>The HTTP status.</summary>
    public int Status { get; } = status;

    /// <summary>The code.</summary>
    public string Code { get; } = code;

    /// <summary>A 400 with <see cref="Invalid"/>.</summary>
    public static AgentCompareException BadRequest(string message) => new(400, Invalid, message);

    /// <summary>A 404 with <see cref="NotFound"/>.</summary>
    public static AgentCompareException Missing(string message) => new(404, NotFound, message);

    /// <summary>A 409 with <paramref name="code"/>.</summary>
    public static AgentCompareException Conflict(string code, string message) => new(409, code, message);
}
