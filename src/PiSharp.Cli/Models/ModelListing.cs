// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/cli/list-models.ts,
// packages/coding-agent/src/core/auth-guidance.ts (getProviderLoginHelp and its messages) and packages/tui/src/fuzzy.ts.
using System.Globalization;
using System.Text.RegularExpressions;

namespace PiSharp.Cli.Models;

/// <summary>Source fuzzyMatch/fuzzyFilter: query characters in order, lower score is better; tokens split on whitespace and <c>/</c>.</summary>
internal static partial class FuzzyFilter
{
    internal readonly record struct Match(bool Matches, double Score);

    internal static Match FuzzyMatch(string query, string text)
    {
        var queryLower = query.ToLowerInvariant(); var textLower = text.ToLowerInvariant();
        Match Run(string normalized)
        {
            if (normalized.Length == 0) return new(true, 0);
            if (normalized.Length > textLower.Length) return new(false, 0);
            int queryIndex = 0, last = -1, consecutive = 0; double score = 0;
            while (queryIndex < normalized.Length)
            {
                var index = textLower.IndexOf(normalized[queryIndex], last + 1);
                if (index == -1) break;
                var boundary = index == 0 || textLower[index - 1] is ' ' or '\t' or '\n' or '\r' or '\f' or '\v' or '-' or '_' or '.' or '/' or ':' ||
                    char.IsWhiteSpace(textLower[index - 1]);
                if (last == index - 1) { consecutive++; score -= consecutive * 5; }
                else { consecutive = 0; if (last >= 0) score += (index - last - 1) * 2; }
                if (boundary) score -= 10;
                score += index * 0.1;
                last = index; queryIndex++;
            }
            if (queryIndex < normalized.Length) return new(false, 0);
            if (normalized == textLower) score -= 100;
            return new(true, score);
        }
        var primary = Run(queryLower);
        if (primary.Matches) return primary;
        var letters = LettersDigits().Match(queryLower); var digits = DigitsLetters().Match(queryLower);
        var swapped = letters.Success ? letters.Groups[2].Value + letters.Groups[1].Value :
            digits.Success ? digits.Groups[2].Value + digits.Groups[1].Value : "";
        if (swapped.Length == 0) return primary;
        var swappedMatch = Run(swapped);
        return swappedMatch.Matches ? new(true, swappedMatch.Score + 5) : primary;
    }

    internal static List<T> Filter<T>(IReadOnlyList<T> items, string query, Func<T, string> text)
    {
        if (query.Trim().Length == 0) return [.. items];
        var tokens = Tokens().Split(query.Trim()).Where(token => token.Length > 0).ToArray();
        if (tokens.Length == 0) return [.. items];
        var results = new List<(T Item, double Score, int Index)>();
        for (var index = 0; index < items.Count; index++)
        {
            var value = text(items[index]); double total = 0; var all = true;
            foreach (var token in tokens)
            {
                var match = FuzzyMatch(token, value);
                if (!match.Matches) { all = false; break; }
                total += match.Score;
            }
            if (all) results.Add((items[index], total, index));
        }
        return [.. results.OrderBy(result => result.Score).ThenBy(result => result.Index).Select(result => result.Item)];
    }

    [GeneratedRegex("^([a-z]+)([0-9]+)$")] private static partial Regex LettersDigits();
    [GeneratedRegex("^([0-9]+)([a-z]+)$")] private static partial Regex DigitsLetters();
    [GeneratedRegex("[\\s/]+")] private static partial Regex Tokens();
}

/// <summary>Source listModels: the authenticated chat models as an aligned table, optionally fuzzy-filtered.</summary>
internal static class ModelListing
{
    /// <summary>Source getDocsPath: the installed package's <c>docs</c> folder (here the application directory's).</summary>
    internal static string DocsPath => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "docs"));

    /// <summary>Source getProviderLoginHelp.</summary>
    internal static string ProviderLoginHelp(string? docsPath = null)
    {
        var docs = docsPath ?? DocsPath;
        return "Use /login to log into a provider via OAuth or API key. See:\n" +
            "  " + Path.Combine(docs, "providers.md") + "\n  " + Path.Combine(docs, "models.md");
    }

    /// <summary>Source formatNoModelsAvailableMessage.</summary>
    internal static string NoModelsAvailableMessage(string? docsPath = null) => "No models available. " + ProviderLoginHelp(docsPath);

    /// <summary>Source formatNoApiKeyFoundMessage (agent-session.ts prompt for a provider without configured auth).</summary>
    internal static string NoApiKeyFoundMessage(string provider, string? docsPath = null) =>
        "No API key found for " + (provider == "unknown" ? "the selected model" : provider) + ".\n\n" + ProviderLoginHelp(docsPath);

    /// <summary>agent-session.ts prompt: a provider whose stored OAuth credential no longer yields configured auth.</summary>
    internal static string OAuthAuthenticationFailedMessage(string provider) =>
        $"Authentication failed for \"{provider}\". Credentials may have expired or network is unavailable. Run '/login {provider}' to re-authenticate.";

    /// <summary>Source formatTokenCount: 200000 → 200K, 1000000 → 1M, one decimal otherwise.</summary>
    internal static string FormatTokenCount(double count)
    {
        static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
        if (count >= 1_000_000) { var millions = count / 1_000_000; return (millions % 1 == 0 ? Number(millions) : millions.ToString("F1", CultureInfo.InvariantCulture)) + "M"; }
        if (count >= 1_000) { var thousands = count / 1_000; return (thousands % 1 == 0 ? Number(thousands) : thousands.ToString("F1", CultureInfo.InvariantCulture)) + "K"; }
        return Number(count);
    }

    internal const string Usage = "--list-models [search]";

    /// <summary>Source main.ts <c>--list-models [search]</c> (args.ts: the search is the next argument unless it starts with <c>-</c> or
    /// <c>@</c>): the session's registry without network refresh, listed, exit 0.</summary>
    internal static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, Commands.LiveSessionRuntime runtime,
        CancellationToken cancellationToken)
    {
        string? search = args is [_, var next] && !next.StartsWith('-') && !next.StartsWith('@') ? next : null;
        if (args is not ["--list-models"] && search is null)
        {
            await error.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(new { schemaVersion = 1, status = "failed", code = "InvalidArguments",
                message = "Usage: PiSharp.Cli " + Usage })).ConfigureAwait(false);
            return 2;
        }
        var registry = await runtime.CreateModelRegistryAsync(cancellationToken).ConfigureAwait(false);
        await ListAsync(registry, search, output, error, cancellationToken: cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        await error.FlushAsync(cancellationToken).ConfigureAwait(false);
        return 0;
    }

    /// <summary>Writes exactly what upstream prints: a models.json warning to <paramref name="error"/>, then the table (or the empty
    /// message) to <paramref name="output"/>, one <c>\n</c>-terminated line per console.log.</summary>
    internal static async Task ListAsync(ModelRegistry registry, string? searchPattern, TextWriter output, TextWriter error, string? docsPath = null,
        CancellationToken cancellationToken = default)
    {
        if (registry.GetError() is { } loadError)
            await error.WriteAsync(("Warning: errors loading models.json:\n" + loadError + "\n").AsMemory(), cancellationToken).ConfigureAwait(false);
        var models = registry.GetAvailable();
        if (models.Count == 0)
        {
            await output.WriteAsync((NoModelsAvailableMessage(docsPath) + "\n").AsMemory(), cancellationToken).ConfigureAwait(false);
            return;
        }
        var filtered = string.IsNullOrEmpty(searchPattern) ? [.. models] : FuzzyFilter.Filter(models, searchPattern, model => model.Provider + " " + model.Id);
        if (filtered.Count == 0)
        {
            await output.WriteAsync(("No models matching \"" + searchPattern + "\"\n").AsMemory(), cancellationToken).ConfigureAwait(false);
            return;
        }
        var sorted = filtered.Select((model, index) => (model, index)).OrderBy(entry => entry, Comparer<(RegistryModel Model, int Index)>.Create((a, b) =>
        {
            var provider = ModelResolver.LocaleCompare(a.Model.Provider, b.Model.Provider);
            if (provider != 0) return provider;
            var id = ModelResolver.LocaleCompare(a.Model.Id, b.Model.Id);
            return id != 0 ? id : a.Index.CompareTo(b.Index);
        })).Select(entry => entry.model).ToList();
        var rows = sorted.Select(model => new[]
        {
            model.Provider, model.Id, FormatTokenCount(model.ContextWindow), FormatTokenCount(model.MaxTokens),
            model.Reasoning ? "yes" : "no", model.DeclaresImageInput ? "yes" : "no"
        }).ToList();
        string[] headers = ["provider", "model", "context", "max-out", "thinking", "images"];
        var widths = headers.Select((header, column) => Math.Max(header.Length, rows.Max(row => row[column].Length))).ToArray();
        string Line(string[] cells) => string.Join("  ", cells.Select((cell, column) => cell.PadRight(widths[column])));
        await output.WriteAsync((Line(headers) + "\n").AsMemory(), cancellationToken).ConfigureAwait(false);
        foreach (var row in rows) await output.WriteAsync((Line(row) + "\n").AsMemory(), cancellationToken).ConfigureAwait(false);
    }
}
