// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/core/tools/edit-diff.ts.
// computeEditsDiff only, the preview the edit renderer shows before the tool runs. Matching and the display diff reuse the edit
// tool's own implementation (PiSharp.Tools.Files: EditPlan, EditMatcher, DiffFormatter) with the edit tool's bounds, so the
// preview agrees with what the tool will write.
using System.Text;
using PiSharp.Tools.Files;

namespace PiSharp.Cli.Interactive.Mode.Components;

/// <summary>An edit: oldText replaced by newText.</summary>
internal sealed record EditTextPair(string OldText, string NewText);

/// <summary>EditDiffResult ({diff, firstChangedLine}) or EditDiffError ({error}).</summary>
internal sealed record EditDiffPreview(string? Diff, int? FirstChangedLine, string? Error)
{
    public bool IsError => Error is not null;
    public static EditDiffPreview Result(string diff, int? firstChangedLine) => new(diff, firstChangedLine, null);
    public static EditDiffPreview Failure(string error) => new(null, null, error);
}

internal static class EditDiff
{
    /// <summary>
    /// Compute the diff for one or more edit operations without applying them.
    /// Used for preview rendering in the TUI before the tool executes.
    /// </summary>
    public static async Task<EditDiffPreview> ComputeEditsDiffAsync(string path, IReadOnlyList<EditTextPair> edits, string cwd, CancellationToken cancellationToken = default)
    {
        var absolutePath = ToolPaths.ResolveToCwd(path, cwd);

        try
        {
            // Check if file exists and is readable
            string rawContent;
            try
            {
                // Read the file (utf-8; a BOM is stripped before matching: the LLM won't include an invisible BOM in oldText)
                rawContent = await File.ReadAllTextAsync(absolutePath, new UTF8Encoding(false, false), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // access(R_OK) succeeds on a directory; reading it fails with EISDIR.
                if (Directory.Exists(absolutePath)) return EditDiffPreview.Failure("EISDIR: illegal operation on a directory, read");
                return EditDiffPreview.Failure($"Could not edit file: {path}. Error code: {ErrorCode(error)}.");
            }

            if (rawContent.Length > 0 && rawContent[0] == (char)0xFEFF) rawContent = rawContent[1..];
            var normalizedContent = EditMatcher.NormalizeToLF(rawContent);
            var plan = EditPlan.Create(normalizedContent, [.. edits.Select(edit => new TextEdit(edit.OldText, edit.NewText))], path,
                new EditPlanOptions(MaximumCharacters: 256 * 1024 * 1024, MaximumEdits: 1_000_000));

            // Generate the diff
            var display = DiffFormatter.FormatDisplay(plan.BaseContent, plan.NewContent, EditToolOptions.DefaultDiffOptions, cancellationToken);
            return EditDiffPreview.Result(display.Diff, display.FirstChangedLine);
        }
        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
        {
            return EditDiffPreview.Failure(error.Message);
        }
    }

    /// <summary>The Node error code of a failed access/read.</summary>
    private static string ErrorCode(Exception error) => error switch
    {
        FileNotFoundException or DirectoryNotFoundException => "ENOENT",
        UnauthorizedAccessException => "EACCES",
        PathTooLongException => "ENAMETOOLONG",
        _ => "EIO"
    };
}
