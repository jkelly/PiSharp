// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/src/env/index.ts (ShellOutputWindow and ShellOutputSkip only).
// The tool output buffer (output.ts) and the tool execution API name these two shapes of the Shell half type-only, as upstream's output.ts and
// types.ts import them, before the execution environment lands. They keep upstream's names and namespace so the environment's Shell types
// (ShellExecOptions.window, ShellOutputInfo.skipped) use them unchanged. MERGE NOTE: the environment branch defines the same two records in
// Env/Shell.cs; when it merges, delete this file and keep that definition.
namespace PiSharp.Durable.Env;

/// <summary>
/// The tail of the combined output a caller keeps, and how often it samples it (upstream <c>ShellOutputWindow</c>). An environment that
/// transfers output over a slow link uses it to omit what the caller would drop anyway and to send no faster than the caller commits.
/// </summary>
/// <param name="MaxBytes">UTF-8 bytes of decoded text kept at the end of the output.</param>
/// <param name="MaxLines">Lines kept at the end of the output.</param>
/// <param name="MinIntervalMs">Minimum pause between the caller's samples of the output.</param>
/// <param name="BytesPerSecond">Each sample also pauses the caller in proportion to its size at this rate.</param>
public sealed record ShellOutputWindow(long MaxBytes, long MaxLines, double MinIntervalMs, double BytesPerSecond);

/// <summary>
/// Output an environment omitted, measured on the decoded text the output callback would have received: every U+FFFD counts as three
/// bytes (upstream <c>ShellOutputSkip</c>).
/// </summary>
/// <param name="Bytes">UTF-8 byte length of the omitted text.</param>
/// <param name="Newlines">Newlines (U+000A) in the omitted text.</param>
/// <param name="EndsWithNewline">Whether the omitted text ends with a newline.</param>
public sealed record ShellOutputSkip(long Bytes, long Newlines, bool EndsWithNewline);
