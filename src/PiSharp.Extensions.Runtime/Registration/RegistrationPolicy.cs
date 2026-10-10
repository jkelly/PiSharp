using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Extensions;

namespace PiSharp.Extensions.Runtime;

public sealed record ExtensionRegistryOptions
{
    public int MaximumOwners { get; init; } = 128;
    public int MaximumRegistrations { get; init; } = 1_024;
    public int MaximumRegistrationsPerOwner { get; init; } = 128;
    public int MaximumMetadataCharacters { get; init; } = 1_048_576;
    public int MaximumIdentifierCharacters { get; init; } = 128;
    public int MaximumDescriptionCharacters { get; init; } = 4_096;
    public int MaximumJsonCharacters { get; init; } = 65_536;
    public int MaximumJsonDepth { get; init; } = 32;
    public int MaximumConcurrentDispatches { get; init; } = 32;
    /// <summary>Bound of one host-built agent/session event observation (Pi events carry whole messages and context previews).</summary>
    public int MaximumObservationCharacters { get; init; } = 67_108_864;
    /// <summary>Bounds of the session branch view each callback context captures (<see cref="ExtensionSessionSnapshotLimits"/> by
    /// default). A host following Pi, whose runner.ts hands every handler the whole session manager, lifts them.</summary>
    public int MaximumSessionBranchEntries { get; init; } = ExtensionSessionSnapshotLimits.MaximumBranchEntries;
    public long MaximumSessionCharacters { get; init; } = ExtensionSessionSnapshotLimits.MaximumCharacters;
    public long MaximumSessionUtf8Bytes { get; init; } = ExtensionSessionSnapshotLimits.MaximumUtf8Bytes;
    public ImmutableArray<string> ReservedToolNames { get; init; } =
        ["read", "bash", "powershell", "edit", "write", "grep", "find", "ls"];
    public ImmutableArray<string> ReservedCommandNames { get; init; } =
        ["help", "quit", "exit", "reload", "settings", "trust", "permissions"];
    /// <summary>Command names as Pi accepts them: any non-empty name without whitespace or control characters (Pi's registerCommand
    /// takes any name, and a duplicate becomes <c>name:N</c>). Registration ids stay identifiers.</summary>
    public bool AllowAnyCommandName { get; init; }
    /// <summary>Pi runner resolveRegisteredCommands: owners may register the same command name; every occurrence of a name registered
    /// more than once (in extension load order) is invoked as <c>name:1</c>, <c>name:2</c>…, skipping taken names. Within one owner
    /// a name stays unique.</summary>
    public bool SuffixDuplicateCommandNames { get; init; }
    /// <summary>Dispatches resolve against the registry's current registrations instead of the captured revision they were given
    /// (Pi's runner reads its extensions' live handler maps: an owner activated or a handler registered after the session bound, as
    /// a reload rebuilds the extension runtime, takes part in the next dispatch).</summary>
    public bool FollowCurrentSnapshot { get; init; }
}

public enum ExtensionRegistrationFailure
{
    InvalidDescriptor,
    DuplicateOwner,
    DuplicateRegistrationId,
    DuplicateName,
    ReservedName,
    LimitExceeded,
    InactiveScope,
    StaleSnapshot,
    InitializationFailed,
    CleanupFailed,
    ReentrantDisposal
}

/// <summary>Bounded host diagnostics. Plugin exceptions remain inner exceptions, not interpolated messages.</summary>
public sealed class ExtensionRegistrationException : Exception
{
    public ExtensionRegistrationFailure Failure { get; }
    public string OwnerId { get; }
    public string Operation { get; }

    internal ExtensionRegistrationException(ExtensionRegistrationFailure failure, string ownerId,
        string operation, Exception? inner = null)
        : base($"Extension '{ownerId}' operation '{operation}' failed: {failure}.", inner)
    {
        Failure = failure;
        OwnerId = ownerId;
        Operation = operation;
    }
}

internal static class RegistrationPolicy
{
    internal static void ValidateOptions(ExtensionRegistryOptions options)
    {
        if (options.MaximumOwners <= 0 || options.MaximumRegistrations <= 0 ||
            options.MaximumRegistrationsPerOwner <= 0 || options.MaximumMetadataCharacters <= 0 ||
            options.MaximumIdentifierCharacters <= 0 || options.MaximumDescriptionCharacters < 0 ||
            options.MaximumJsonCharacters <= 0 || options.MaximumJsonDepth is < 1 or > PiSharp.Contracts.JsonData.MaximumDepth ||
            options.MaximumConcurrentDispatches <= 0 || options.MaximumSessionBranchEntries <= 0 || options.MaximumSessionCharacters <= 0 ||
            options.MaximumSessionUtf8Bytes <= 0 || options.ReservedToolNames.IsDefault ||
            options.ReservedCommandNames.IsDefault || options.ReservedToolNames.Length > 256 ||
            options.ReservedCommandNames.Length > 256)
            throw new ArgumentOutOfRangeException(nameof(options));
        foreach (var name in options.ReservedToolNames.Concat(options.ReservedCommandNames))
            if (!Identifier(name, options.MaximumIdentifierCharacters))
                throw new ArgumentException("Reserved names must be bounded ASCII identifiers.", nameof(options));
    }

    internal static bool Identifier(string? value, int maximum) =>
        value is { Length: > 0 } && value.Length <= maximum &&
        value.All(character => character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or
            >= '0' and <= '9' or '.' or '_' or '-');

    internal static bool CommandName(string? value, int maximum) =>
        value is { Length: > 0 } && value.Length <= maximum && !value.Any(character => char.IsWhiteSpace(character) || char.IsControl(character));

    internal static bool Description(string? value, ExtensionRegistryOptions options) =>
        value is not null && value.Length <= options.MaximumDescriptionCharacters &&
        !value.Contains('\0') && Scalars(value);

    internal static bool Json(JsonData? value, ExtensionRegistryOptions options, bool requireObject = false,
        bool retainOpaqueNumbers = false)
    {
        if (value is null) return false;
        try
        {
            var raw = value.ToString();
            if (raw.Length > options.MaximumJsonCharacters || !Scalars(raw)) return false;
            // FromElement may retain permissive comments/trailing commas. Validate retained syntax before traversal.
            using var document = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = options.MaximumJsonDepth });
            return (!requireObject || document.RootElement.ValueKind == JsonValueKind.Object) &&
                JsonValue(document.RootElement, retainOpaqueNumbers);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    private static bool JsonValue(JsonElement value, bool retainOpaqueNumbers)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject())
                    if (!Scalars(property.Name) || !names.Add(property.Name) || !JsonValue(property.Value, retainOpaqueNumbers)) return false;
                return true;
            case JsonValueKind.Array:
                return value.EnumerateArray().All(item => JsonValue(item, retainOpaqueNumbers));
            case JsonValueKind.String:
                return Scalars(value.GetString()!);
            case JsonValueKind.Number:
                return retainOpaqueNumbers || value.TryGetDouble(out var number) && double.IsFinite(number);
            default:
                return value.ValueKind is JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null;
        }
    }

    internal static bool Scalars(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (!char.IsSurrogate(value[index])) continue;
            if (!char.IsHighSurrogate(value[index]) || index + 1 == value.Length ||
                !char.IsLowSurrogate(value[++index])) return false;
        }
        return true;
    }
}
