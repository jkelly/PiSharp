// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/src/tools/index.ts
// Upstream's createXTool functions are CodingTools.CreateXTool; its CodingTools extension is CodingTools.Extension. Each call creates a new
// registration, as upstream's factories do. None sets `replay`, so every coding tool keeps the default, unsafe: a call interrupted by a crash
// or close is answered as interrupted instead of rerun.
using PiSharp.Durable.Chord;

namespace PiSharp.Durable.Tools;

/// <summary>The durable coding tools: <c>read</c>, <c>write</c>, <c>edit</c>, <c>bash</c> and <c>powershell</c>. Each reaches files and
/// processes only through the execution environment the Harness builds for the call (<c>HarnessOptions.Env</c>); without one it fails with an
/// error result.</summary>
public static class CodingTools
{
    private static readonly Lazy<Extension> Default = new(() => Define.Extension(new Extension
    {
        Name = "coding-tools",
        Tools = [CreateReadTool(), CreateWriteTool(), CreateEditTool(), CreateBashTool()],
    }));

    /// <summary><c>read</c>, <c>write</c>, <c>edit</c> and <c>bash</c> as the extension <c>coding-tools</c> (upstream <c>CodingTools</c>);
    /// nothing installs it automatically. <see cref="CreatePowerShellTool"/> adds <c>powershell</c>.</summary>
    public static Extension Extension => Default.Value;

    /// <summary>Reads text files with <c>offset</c> and <c>limit</c> in lines; truncation and continuation remarks are diagnostics and the
    /// content is only file text. Images are reported as unsupported.</summary>
    public static ToolRegistration CreateReadTool() => ReadTool.Create();

    /// <summary>Writes a file, creating parent directories.</summary>
    public static ToolRegistration CreateWriteTool() => WriteTool.Create();

    /// <summary>Edits a file by exact-text replacements matched against the original; details carry a display <c>diff</c>, a unified
    /// <c>patch</c> and <c>firstChangedLine</c>.</summary>
    public static ToolRegistration CreateEditTool() => EditTool.Create();

    /// <summary>Runs a command through the environment's shell, keeping the tail of its output and spilling the rest to a file.</summary>
    public static ToolRegistration CreateBashTool(BashToolOptions? options = null) => BashTool.CreateBash(options);

    /// <summary>Runs a command through PowerShell (<c>pwsh</c>, else Windows PowerShell), started directly with the command as an argument.</summary>
    public static ToolRegistration CreatePowerShellTool(PowerShellToolOptions? options = null) => BashTool.CreatePowerShell(options);
}

/// <summary>pi-ai tool result content.</summary>
internal static class ToolContent
{
    /// <summary>One text block.</summary>
    public static JsonArrayValue Text(string text) => JsonValue.Array(JsonValue.Object([new("type", "text"), new("text", text)]));
}
