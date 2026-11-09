// Pi 7d073328e44337d3292a7db9b0f0308f679c5e97 (MIT): core/package-manager.ts update result summaries.
using System.Collections.Immutable;

namespace PiSharp.Cli.Packages;

internal enum PiPackageUpdateStatus { Changed, Unchanged, Offline, Warning }
internal sealed record PiPackageUpdateResult(string Source, string Scope, PiPackageUpdateStatus Status,
    string? Before = null, string? After = null, string? Warning = null);
internal sealed record PiPackageUpdateSummary(ImmutableArray<PiPackageUpdateResult> Packages)
{
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
