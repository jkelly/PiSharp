// Decision 0011 (run W2, WU4): the settings sections the M0 pages need (mockups settings-defaults, settings-tools and settings-models).
// Defaults: the provider, model and thinking level new chats start with (a chat keeps its own afterwards). Tools: the folder tools work in
// (StartDirectories of the device host; default the user's home), the web-approval mode and its allow-list. Models: models hidden from the
// pickers. Later milestones add sections of their own (Spend in W3); none of these hold a secret.
using PiSharp.AI;

namespace PiSharp.WebAgent.Contracts;

/// <summary>What new chats start with (Settings › Defaults). Both <see cref="Provider"/> and <see cref="Model"/> are set, or neither (then
/// the first catalog model is used).</summary>
/// <param name="Provider">The default provider id.</param>
/// <param name="Model">The default model id of <paramref name="Provider"/>.</param>
/// <param name="Thinking">The default thinking level (<see cref="ThinkingLevels.Ordered"/>), or null for the model's default; clamped to what
/// a model supports when a chat starts.</param>
public sealed record DefaultsSettings(string? Provider = null, string? Model = null, string? Thinking = null) : ISettingsSection<DefaultsSettings>
{
    /// <inheritdoc/>
    public static string SectionName => "defaults";

    /// <inheritdoc/>
    public static DefaultsSettings CreateDefault() => new();

    /// <inheritdoc/>
    public DefaultsSettings Normalize()
    {
        var provider = Blank(Provider);
        var model = Blank(Model);
        if ((provider is null) != (model is null)) throw new ArgumentException("Choose both a provider and a model, or neither.");
        var thinking = Blank(Thinking);
        if (thinking is not null && !ThinkingLevels.Ordered.Contains(thinking, StringComparer.Ordinal))
            throw new ArgumentException($"Unknown thinking level \"{thinking}\": use one of {string.Join(", ", ThinkingLevels.Ordered)}.");
        return new(provider, model, thinking);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>When a tool call waits for the user's approval (the <c>web-approval</c> extension). Read-only tools never ask.</summary>
public enum ToolApprovalMode
{
    /// <summary>Ask before every tool call that is not read-only.</summary>
    Ask,

    /// <summary>As <see cref="Ask"/>, except calls that match <see cref="ToolsSettings.AllowList"/>.</summary>
    AllowList,

    /// <summary>Never ask: every call runs straight away.</summary>
    Auto,
}

/// <summary>A tool call that runs without asking in <see cref="ToolApprovalMode.AllowList"/> mode (and that "Allow always" adds).</summary>
/// <param name="Tool">The tool's name, such as <c>bash</c> or <c>edit</c>.</param>
/// <param name="Command">For <c>bash</c>, a command pattern where <c>*</c> matches any arguments (such as <c>dotnet test *</c>); a chained
/// command must match for every part. Null allows every call of <paramref name="Tool"/>.</param>
public sealed record ToolAllowRule(string Tool, string? Command = null);

/// <summary>Where tools work and when they ask (Settings › Tools &amp; workspace).</summary>
/// <param name="WorkingDirectory">The folder new chats work in, an absolute path; default the user's home.</param>
/// <param name="ApprovalMode">When a tool call asks first; default <see cref="ToolApprovalMode.Ask"/>.</param>
/// <param name="AllowList">The calls <see cref="ToolApprovalMode.AllowList"/> lets through, without duplicates.</param>
public sealed record ToolsSettings(string? WorkingDirectory = null, ToolApprovalMode ApprovalMode = ToolApprovalMode.Ask,
    IReadOnlyList<ToolAllowRule>? AllowList = null) : ISettingsSection<ToolsSettings>
{
    /// <inheritdoc/>
    public static string SectionName => "tools";

    /// <summary>The working directory when none is set: the user's home folder.</summary>
    public static string DefaultWorkingDirectory => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <inheritdoc/>
    public static ToolsSettings CreateDefault() => new(DefaultWorkingDirectory, ToolApprovalMode.Ask, []);

    /// <summary>The working directory, or <see cref="DefaultWorkingDirectory"/> when none is set.</summary>
    public string EffectiveWorkingDirectory => string.IsNullOrWhiteSpace(WorkingDirectory) ? DefaultWorkingDirectory : WorkingDirectory;

    /// <inheritdoc/>
    public ToolsSettings Normalize()
    {
        var directory = EffectiveWorkingDirectory.Trim();
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException($"The working directory must be a full path, such as {DefaultWorkingDirectory}.");
        if (!Enum.IsDefined(ApprovalMode)) throw new ArgumentException($"Unknown approval mode {(int)ApprovalMode}.");
        var rules = new List<ToolAllowRule>();
        foreach (var rule in AllowList ?? [])
        {
            if (rule is null || string.IsNullOrWhiteSpace(rule.Tool)) throw new ArgumentException("Every allow-list entry needs a tool name.");
            var normalized = new ToolAllowRule(rule.Tool.Trim(), string.IsNullOrWhiteSpace(rule.Command) ? null : rule.Command.Trim());
            if (!rules.Contains(normalized)) rules.Add(normalized);
        }
        return new(directory, ApprovalMode, rules);
    }
}

/// <summary>Models hidden from the pickers (Settings › Models). A hidden model stays usable by a chat that already has it.</summary>
/// <param name="Hidden">The hidden models as <c>provider/model</c> keys (<see cref="Key"/>), without duplicates.</param>
public sealed record ModelsSettings(IReadOnlyList<string>? Hidden = null) : ISettingsSection<ModelsSettings>
{
    /// <inheritdoc/>
    public static string SectionName => "models";

    /// <inheritdoc/>
    public static ModelsSettings CreateDefault() => new([]);

    /// <summary>The key of model <paramref name="modelId"/> of <paramref name="providerId"/>: <c>provider/model</c>.</summary>
    public static string Key(string providerId, string modelId) => providerId + "/" + modelId;

    /// <summary>Whether model <paramref name="modelId"/> of <paramref name="providerId"/> is hidden.</summary>
    public bool IsHidden(string providerId, string modelId) => Hidden?.Contains(Key(providerId, modelId), StringComparer.Ordinal) == true;

    /// <inheritdoc/>
    public ModelsSettings Normalize()
    {
        var keys = new List<string>();
        foreach (var key in Hidden ?? [])
        {
            var trimmed = key?.Trim() ?? "";
            var slash = trimmed.IndexOf('/', StringComparison.Ordinal);
            if (slash <= 0 || slash == trimmed.Length - 1) throw new ArgumentException($"\"{key}\" is not a provider/model key.");
            if (!keys.Contains(trimmed, StringComparer.Ordinal)) keys.Add(trimmed);
        }
        return new(keys);
    }
}
