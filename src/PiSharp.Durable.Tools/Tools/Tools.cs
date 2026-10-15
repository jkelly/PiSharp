// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/src/tools/index.ts
// Upstream's createXTool functions are CodingTools.CreateXTool; createCodingTools is CodingTools.CreateCodingTools and its CodingTools
// extension is CodingTools.Extension. Each call creates a new registration, as upstream's factories do. None sets `replay`, so every coding
// tool keeps the default, unsafe: a call interrupted by a crash or close is answered as interrupted instead of rerun.
using PiSharp.Durable.Chord;

namespace PiSharp.Durable.Tools;

/// <summary>The durable coding tools: <c>read</c>, <c>write</c>, <c>edit</c>, <c>bash</c> and <c>powershell</c>. Each reaches files and
/// processes only through the execution environment the Harness builds for the call (<c>HarnessOptions.Env</c>); without one it fails with an
/// error result.</summary>
public static class CodingTools
{
    private static readonly Lazy<Extension> Default = new(() => CreateCodingTools());

    /// <summary>Upstream <c>DEFAULT_IMAGE_LIMITS</c>: the image limits <c>read</c> keeps unless the conversation's model sets its own.</summary>
    public static ImageLimits DefaultImageLimits => ImageLimits.Default;

    /// <summary>
    /// The <c>coding-tools</c> extension: <c>read</c>, <c>write</c>, <c>edit</c>, and <c>bash</c>; nothing installs it automatically.
    /// <see cref="CreatePowerShellTool"/> adds <c>powershell</c>. <see cref="ReadToolOptions.Images"/> prepares the images <c>read</c> returns.
    /// </summary>
    public static Extension CreateCodingTools(ReadToolOptions? options = null) => Define.Extension(new Extension
    {
        Name = "coding-tools",
        Tools = [CreateReadTool(options), CreateWriteTool(), CreateEditTool(), CreateBashTool()],
    });

    /// <summary><see cref="CreateCodingTools"/> without an image processor (upstream <c>CodingTools</c>).</summary>
    public static Extension Extension => Default.Value;

    /// <summary>Reads text files with <c>offset</c> and <c>limit</c> in lines, and images (PNG, JPEG, GIF, WebP, and BMP with an image processor)
    /// as one image block within the conversation model's image limits; truncation, continuation and image remarks are diagnostics.</summary>
    public static ToolRegistration CreateReadTool(ReadToolOptions? options = null) => ReadTool.Create(options);

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
