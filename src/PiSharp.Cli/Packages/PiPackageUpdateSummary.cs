// Pi 7d073328e44337d3292a7db9b0f0308f679c5e97 (MIT): core/package-manager.ts update result summaries.
using System.Collections.Immutable;

namespace PiSharp.Cli.Packages;

internal enum PiPackageUpdateStatus { Changed, Unchanged, Offline, Warning }
internal sealed record PiPackageUpdateResult(string Source, string Scope, PiPackageUpdateStatus Status,
    string? Before = null, string? After = null, string? Warning = null);
internal sealed record PiPackageUpdateSummary(ImmutableArray<PiPackageUpdateResult> Packages, bool Offline = false)
{
    /// <summary>Only changed package identities produce result events; warnings can coexist with a real change.</summary>
    internal IEnumerable<(string Source, string Message)> Results()
    {
        foreach (var package in Packages)
        {
            if (package.Status == PiPackageUpdateStatus.Offline || package.Before == package.After) continue;
            var parsed = PiPackageSource.Parse(package.Source);
            if (parsed is not (PiNpmSource or PiGitSource)) continue;
            if (parsed is PiGitSource && package.After is null) continue;
            string Format(string? value) => value is null ? "(none)" : parsed is PiGitSource ? value[..Math.Min(value.Length, 7)] : value;
            yield return (package.Source, $"{package.Source}{(package.Scope == "project" ? " (project)" : "")} {Format(package.Before)} -> {Format(package.After)}");
        }
    }

    internal IEnumerable<string> CliLines(string? source = null, int warningCount = 0)
    {
        var changed = Results().ToArray();
        if (Offline || Packages.Any(package => package.Status == PiPackageUpdateStatus.Offline))
            yield return "Skipped package updates (offline mode)";
        else if (changed.Length > 0)
        {
            yield return $"Updated {changed.Length} package{(changed.Length == 1 ? "" : "s")}:";
            foreach (var (_, message) in changed) yield return "  " + message;
        }
        else if (warningCount > 0 || Packages.Any(package => package.Status == PiPackageUpdateStatus.Warning))
            yield return source is null ? "No packages were updated, see the warnings above" : $"{source} was not updated, see the warnings above";
        else yield return source is null ? "Packages are already up to date" : $"{source} is already up to date";
    }

    // The detailed native receipt remains available independently of the shell presentation.
    internal IEnumerable<string> Lines()
    {
        if (Packages.IsEmpty) yield return "No packages configured for update.";
        foreach (var package in Packages)
        {
            var versions = package.Before == package.After ? package.After is null ? "" : $" ({package.After})"
                : $" ({package.Before ?? "not installed"} -> {package.After ?? "unknown"})";
            yield return package.Status switch
            {
                PiPackageUpdateStatus.Offline => $"Skipped {package.Source} [{package.Scope}]: offline mode.",
                PiPackageUpdateStatus.Changed => $"Updated {package.Source} [{package.Scope}]{versions}",
                PiPackageUpdateStatus.Warning => $"Warning: {package.Source} [{package.Scope}]{versions}: {package.Warning}",
                _ => $"Unchanged {package.Source} [{package.Scope}]{versions}"
            };
        }
    }
}
