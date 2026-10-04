using System.Collections.Immutable;
using System.Text.Json;

namespace PiSharp.Contracts;

/// <summary>Request-only presentation. Canonical declarations and executable admission remain unchanged.</summary>
public sealed class ToolLoadoutPresentation
{
    private readonly ImmutableDictionary<string, string> descriptions;
    public ImmutableHashSet<string> HiddenDeclarations { get; }
    public ImmutableArray<ToolLoadoutDiagnostic> Diagnostics { get; }
    private ToolLoadoutPresentation(ImmutableDictionary<string, string> descriptions, ImmutableHashSet<string> hidden,
        ImmutableArray<ToolLoadoutDiagnostic> diagnostics)
    { this.descriptions = descriptions; HiddenDeclarations = hidden; Diagnostics = diagnostics; }

    public static ToolLoadoutPresentation Prepare(ToolLoadout loadout,
        Func<string, ToolLoadout, ToolLoadoutChanges?> prepare,
        Action<string, Exception>? report = null, int maximumCharacters = 1_048_576,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(loadout); ArgumentNullException.ThrowIfNull(prepare);
        if (maximumCharacters <= 0) throw new ArgumentOutOfRangeException(nameof(maximumCharacters));
        var descriptions = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        var hidden = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        var diagnostics = ImmutableArray.CreateBuilder<ToolLoadoutDiagnostic>();
        foreach (var tool in loadout.Declared)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var changes = prepare(tool.Name, loadout);
                cancellationToken.ThrowIfCancellationRequested();
                if (changes is null) continue;
                if (changes.Descriptions is null || changes.HiddenDeclarations.IsDefault ||
                    changes.Descriptions.Count > loadout.Registered.Length || changes.HiddenDeclarations.Length > loadout.Registered.Length)
                    throw new ArgumentException("Loadout changes exceed the registered metadata bounds.");
                long size = 0;
                foreach (var pair in changes.Descriptions)
                { Validate(pair.Key); Validate(pair.Value); }
                foreach (var name in changes.HiddenDeclarations) Validate(name);
                var prospectiveDescriptions = descriptions.ToImmutable().SetItems(changes.Descriptions);
                var prospectiveHidden = hidden.ToImmutable().Union(changes.HiddenDeclarations);
                if (prospectiveDescriptions.Sum(pair => (long)pair.Key.Length + pair.Value.Length) +
                    prospectiveHidden.Sum(name => (long)name.Length) > maximumCharacters)
                    throw new ArgumentException("Combined loadout changes exceed the presentation bounds.");
                // Validate a callback's whole result before merging it. Later active callbacks win descriptions.
                foreach (var pair in changes.Descriptions) descriptions[pair.Key] = pair.Value;
                hidden.UnionWith(changes.HiddenDeclarations);
                void Validate(string value)
                {
                    if (value is null || (size += value.Length) > maximumCharacters)
                        throw new ArgumentException("Loadout changes exceed the presentation bounds.");
                    for (var index = 0; index < value.Length; index++)
                        if (char.IsSurrogate(value[index]) && (!char.IsHighSurrogate(value[index]) ||
                            ++index == value.Length || !char.IsLowSurrogate(value[index])))
                            throw new ArgumentException("Loadout changes contain invalid scalar Unicode.");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error)
            { diagnostics.Add(new(tool.Name, error is ArgumentException ? "InvalidChanges" : "CallbackFailure")); report?.Invoke(tool.Name, error); }
        }
        return new(descriptions.ToImmutable(), hidden.ToImmutable(), diagnostics.ToImmutable());
    }

    public ImmutableArray<TranscriptEntry> Project(ImmutableArray<TranscriptEntry> messages)
    {
        if (descriptions.IsEmpty && HiddenDeclarations.IsEmpty) return messages;
        return messages.Select(message =>
        {
            if (message.Role != "system") return message;
            var fields = new Dictionary<string, JsonElement>();
            foreach (var property in message.WireBody.Value.EnumerateObject())
            {
                if (property.Name is not ("toolsAdded" or "toolsRemoved")) { fields[property.Name] = property.Value; continue; }
                var projected = property.Value.EnumerateArray()
                    .Where(tool => !HiddenDeclarations.Contains(tool.GetProperty("name").GetString()!))
                    .Select(tool => property.Name == "toolsAdded" ? Describe(tool) : tool).ToArray();
                if (projected.Length > 0) fields[property.Name] = JsonSerializer.SerializeToElement(projected);
            }
            return new TranscriptEntry("system", JsonData.Parse(JsonSerializer.Serialize(fields)));
        }).ToImmutableArray();
    }
    private JsonElement Describe(JsonElement tool)
    {
        if (!descriptions.TryGetValue(tool.GetProperty("name").GetString()!, out var description)) return tool;
        var fields = tool.EnumerateObject().ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
        fields["description"] = JsonSerializer.SerializeToElement(description);
        return JsonSerializer.SerializeToElement(fields);
    }
}
