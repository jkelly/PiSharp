// PiSharp-only (decision 0008): the optional extensions durable-pi's Extend tab offers (apps/dpi-backend/src/addons.ts, MIT), off by default
// and switched per agent. system-prompts adds checked prompt files to the system prompt; command-guard checks every bash and powershell
// command before it runs and blocks destructive ones. durable-pi's guard asks a third-party API (Jev) and runs the command when it cannot
// reach it; this one applies offline rules, then an optional judge the host supplies, and fails open or closed as the host chooses. Each
// verdict is a durable memo of the call, so a restart never judges twice.
using System.Text.RegularExpressions;
using PiSharp.Durable.Chord;

namespace PiSharp.Durable.Hosting;

/// <summary>One prompt file of the <c>system-prompts</c> extension.</summary>
/// <param name="Id">Its file name, for example <c>release-voice.md</c>.</param>
/// <param name="Description">Its first line, without a leading <c>#</c>.</param>
/// <param name="Text">The whole file.</param>
public sealed record PromptFile(string Id, string Description, string Text);

/// <summary>The <c>system-prompts</c> extension: per agent, a checklist of the device's prompt files.</summary>
public static partial class SystemPrompts
{
    /// <summary>The extension name.</summary>
    public const string ExtensionName = "system-prompts";

    /// <summary>The agent's checklist: <c>{enabled: [ids]}</c>; a fork starts with its parent's current list.</summary>
    public static ConversationDocToken ChecklistDoc { get; } = Documents.DefineConversationDoc("pisharp.system-prompts", 1, DocumentFork.Current,
        () => JsonObjectValue.Empty);

    /// <summary>The prompt files of <paramref name="directory"/> (<c>*.md</c>, by name); none when it does not exist.</summary>
    public static IReadOnlyList<PromptFile> Load(string? directory)
    {
        if (directory is null || !Directory.Exists(directory)) return [];
        return [.. Directory.EnumerateFiles(directory, "*.md").OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase).Select(path =>
        {
            var text = File.ReadAllText(path);
            var first = text.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0) ?? "";
            return new PromptFile(Path.GetFileName(path), first.TrimStart('#', ' '), text);
        })];
    }

    /// <summary>The checked prompt IDs of a checklist value; <paramref name="defaults"/> when the agent never changed it.</summary>
    public static IReadOnlyList<string> Enabled(JsonObjectValue? checklist, IReadOnlyList<string> defaults) =>
        checklist is not null && checklist.TryGetValue("enabled", out var value) && value is JsonArrayValue ids
            ? [.. ids.OfType<JsonStringValue>().Select(id => id.Value)]
            : defaults;

    internal static Extension Create(string? directory, IReadOnlyList<string> defaults) => Define.Extension(new Extension
    {
        Name = ExtensionName,
        Sections =
        [
            Define.Section("system_prompts", async (input, context) =>
            {
                var checklist = await input.Read.SnapshotAsync(ChecklistDoc, input.ConversationId, context).ConfigureAwait(false);
                var enabled = Enabled(checklist, defaults);
                var prompts = Load(directory).Where(prompt => enabled.Contains(prompt.Id)).Select(prompt => prompt.Text.Trim()).ToList();
                return prompts.Count == 0 ? null : string.Join("\n\n", prompts);
            }),
        ],
    });
}

/// <summary>A command guard's judgement of one command.</summary>
/// <param name="Block">Whether the command must not run.</param>
/// <param name="Reason">Why, for the agent.</param>
public sealed record CommandVerdict(bool Block, string Reason);

/// <summary>The <c>command-guard</c> extension.</summary>
public static partial class CommandGuard
{
    /// <summary>The extension name.</summary>
    public const string ExtensionName = "command-guard";

    private const string Memo = "command-guard";

    private static readonly (Regex Pattern, string Reason)[] Rules =
    [
        (RecursiveForcedDelete(), "recursive forced delete"),
        (PowerShellRecursiveDelete(), "recursive forced delete"),
        (WindowsRecursiveDelete(), "recursive delete"),
        (ForcePush(), "force push"),
        (HardReset(), "hard reset discards work"),
        (Clean(), "deletes untracked files"),
        (FileSystemFormat(), "formats a disk"),
        (RawDiskWrite(), "writes a raw disk"),
        (Shutdown(), "shuts the machine down"),
        (ForkBomb(), "fork bomb"),
        (WorldWritable(), "makes everything world-writable"),
    ];

    /// <summary>The offline rules: recursive forced deletes, force pushes, hard resets, <c>git clean</c>, formatting or writing disks,
    /// shutdowns, fork bombs and recursive <c>chmod 777</c>. Null when no rule matches.</summary>
    public static CommandVerdict? Offline(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        foreach (var (pattern, reason) in Rules)
            if (pattern.IsMatch(command)) return new CommandVerdict(true, $"Blocked by the command guard ({reason}): {command}");
        return null;
    }

    internal static Extension Create(Func<string, CancellationToken, Task<CommandVerdict?>>? judge, bool failOpen) => Define.Extension(new Extension
    {
        Name = ExtensionName,
        Hooks =
        [
            Define.Hook(HarnessTasks.ToolTask, new ToolHooks
            {
                BeforeTool = async (call, api, context) =>
                {
                    if (!(call.TryGetValue("name", out var name) && name is JsonStringValue { Value: "bash" or "powershell" })) return null;
                    var command = call.TryGetValue("arguments", out var arguments) && arguments is JsonObjectValue args &&
                        args.TryGetValue("command", out var text) && text is JsonStringValue value ? value.Value : null;
                    if (command is null) return null;
                    var verdict = await api.MemoAsync(Memo, context).ConfigureAwait(false) as JsonObjectValue;
                    if (verdict is null)
                    {
                        var judged = Offline(command);
                        if (judged is null && judge is not null)
                        {
                            try { judged = await judge(command, context.CancellationToken).ConfigureAwait(false); }
                            catch (Exception error) when (error is not OperationCanceledException)
                            {
                                judged = failOpen ? null : new CommandVerdict(true, $"Blocked: the command guard could not judge the command ({error.Message}).");
                            }
                        }
                        judged ??= new CommandVerdict(false, "allowed");
                        verdict = (JsonObjectValue)await api.MemoAsync(Memo, JsonValue.Object([new("block", judged.Block), new("reason", judged.Reason)]), context)
                            .ConfigureAwait(false);
                    }
                    return verdict.TryGetValue("block", out var block) && block is JsonBooleanValue { Value: true } &&
                        verdict.TryGetValue("reason", out var reason) && reason is JsonStringValue because
                        ? new BeforeToolDecision(Block: because.Value)
                        : null;
                },
            }),
        ],
    });

    [GeneratedRegex(@"\brm\s+(-[a-zA-Z]*r[a-zA-Z]*f|-[a-zA-Z]*f[a-zA-Z]*r|-r\s+-f|-f\s+-r|--recursive\s+--force|--force\s+--recursive)\b")]
    private static partial Regex RecursiveForcedDelete();

    [GeneratedRegex(@"\bRemove-Item\b(?=.*-Recurse)(?=.*-Force)", RegexOptions.IgnoreCase)]
    private static partial Regex PowerShellRecursiveDelete();

    [GeneratedRegex(@"\b(rd|rmdir|del|erase)\b\s+(/[a-z]\s+)*/s\b", RegexOptions.IgnoreCase)]
    private static partial Regex WindowsRecursiveDelete();

    [GeneratedRegex(@"\bgit\s+push\b.*(\s--force\b|\s-f\b|\s--force-with-lease\b)")]
    private static partial Regex ForcePush();

    [GeneratedRegex(@"\bgit\s+reset\s+--hard\b")]
    private static partial Regex HardReset();

    [GeneratedRegex(@"\bgit\s+clean\s+-[a-zA-Z]*[fdx]")]
    private static partial Regex Clean();

    [GeneratedRegex(@"\b(mkfs(\.\w+)?|format\s+[a-z]:|Format-Volume)\b", RegexOptions.IgnoreCase)]
    private static partial Regex FileSystemFormat();

    [GeneratedRegex(@"\bdd\b.*\bof=/dev/|>\s*/dev/(sd|nvme|disk)")]
    private static partial Regex RawDiskWrite();

    [GeneratedRegex(@"(^|[;&|(]\s*)(sudo\s+)?(shutdown|reboot|poweroff|halt)\b|\b(Stop-Computer|Restart-Computer)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Shutdown();

    [GeneratedRegex(@":\(\)\s*\{\s*:\s*\|\s*:\s*&\s*\}\s*;\s*:")]
    private static partial Regex ForkBomb();

    [GeneratedRegex(@"\bchmod\s+(-R\s+)?777\s+/(\s|$)")]
    private static partial Regex WorldWritable();
}
