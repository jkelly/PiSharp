// Decision 0011 (run W4, unit R9): Settings › Data & storage's export and import, in parts. An area that has something to move to another
// machine registers an IDataExportPart (TryAddEnumerable): this unit registers the settings sections and the custom providers; Presets (R8)
// adds its own. A part exports one JSON value under its name in the file, and on import it compares the file's value with what this app has
// now, item by item (a settings section, a custom provider, a preset), so the user reviews every changed key before anything is saved, and
// applies only the items the user kept. A part never exports a secret: credentials are never in the file (a custom provider keeps a $VAR
// reference, never a key), and an item's before and after texts are shown on the page, so they hold no secret either.
using System.Text.Json.Nodes;

namespace PiSharp.WebAgent.DataStorage;

/// <summary>Something Settings › Data &amp; storage exports and imports.</summary>
public interface IDataExportPart
{
    /// <summary>The part's key in the file (<c>settings</c>, <c>customProviders</c>, <c>presets</c>): letters only, unique.</summary>
    string Name { get; }

    /// <summary>What the page calls it, in a list ("settings", "custom providers").</summary>
    string Title { get; }

    /// <summary>The part's value for an export, or null when there is nothing to export. Never a secret.</summary>
    JsonNode? Export();

    /// <summary>What importing <paramref name="value"/> (the file's value under <see cref="Name"/>) would change, item by item.</summary>
    /// <exception cref="ArgumentException">The value is not in this part's form.</exception>
    DataImportPartPlan Plan(JsonNode? value);

    /// <summary>Imports the items of <paramref name="value"/> whose ids are in <paramref name="accepted"/> (ids from <see cref="Plan"/>; an
    /// item with a problem is never imported).</summary>
    Task<DataImportPartResult> ImportAsync(JsonNode? value, IReadOnlySet<string> accepted, CancellationToken cancellationToken);
}

/// <summary>One changed key of an import item, as the review shows it.</summary>
/// <param name="Key">The key (<c>approvalMode</c>), or empty when the item is one value.</param>
/// <param name="Before">What this app has now, as short text (JSON for a setting), or null when it has none.</param>
/// <param name="After">What the file has, or null when the file removes it.</param>
public sealed record DataImportChange(string Key, string? Before, string? After);

/// <summary>One thing an import would change (a settings section, a custom provider, a preset).</summary>
/// <param name="Id">The item's id, unique in the file: <c>&lt;part&gt;/&lt;item&gt;</c> (<c>settings/tools</c>).</param>
/// <param name="Part">The part's name.</param>
/// <param name="Title">What the page calls it ("Tools &amp; workspace").</param>
/// <param name="Changes">Its changed keys.</param>
/// <param name="Problem">Why it can't be imported here (a folder this machine doesn't have), or null.</param>
public sealed record DataImportItem(string Id, string Part, string Title, IReadOnlyList<DataImportChange> Changes, string? Problem = null);

/// <summary>What importing one part would do.</summary>
/// <param name="Items">The items that would change.</param>
/// <param name="Unchanged">How many of the file's items are the same here already.</param>
/// <param name="Notes">What the user should know (left out, kept as it is).</param>
public sealed record DataImportPartPlan(IReadOnlyList<DataImportItem> Items, int Unchanged, IReadOnlyList<string> Notes);

/// <summary>What importing one part did.</summary>
/// <param name="Applied">The ids of the items imported.</param>
/// <param name="Failed">The items that could not be imported, with why.</param>
/// <param name="Notes">What the user should know (from the part's own import).</param>
public sealed record DataImportPartResult(IReadOnlyList<string> Applied, IReadOnlyList<DataImportFailure> Failed, IReadOnlyList<string> Notes);

/// <summary>An item that could not be imported.</summary>
public sealed record DataImportFailure(string Id, string Title, string Error);

/// <summary>Something "Reset all settings" sets back to its defaults (Settings › Data &amp; storage's danger zone). An export part that holds
/// settings implements it too (the settings sections, the custom providers; Presets may).</summary>
public interface IDataResetPart
{
    /// <summary>The part's name (as <see cref="IDataExportPart.Name"/>).</summary>
    string Name { get; }

    /// <summary>What a reset removes or changes, as a phrase for the confirm dialog ("the 2 custom providers of models.json"), or empty when
    /// there is nothing to reset.</summary>
    string ResetText();

    /// <summary>Sets the part back to its defaults.</summary>
    /// <exception cref="InvalidOperationException">It can't be reset now (the message says why).</exception>
    Task ResetAsync(CancellationToken cancellationToken);
}
