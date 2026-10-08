using System.Collections.Immutable;

namespace PiSharp.Extensions;

public enum ExtensionFlagKind { Boolean, String }

/// <summary>The source boolean|string union. Null at an API boundary means an absent value.</summary>
public sealed record ExtensionFlagValue
{
    public ExtensionFlagKind Kind { get; }
    public bool? BooleanValue { get; }
    public string? StringValue { get; }
    private ExtensionFlagValue(bool value) { Kind = ExtensionFlagKind.Boolean; BooleanValue = value; }
    private ExtensionFlagValue(string value) { Kind = ExtensionFlagKind.String; StringValue = value; }
    public static ExtensionFlagValue Boolean(bool value) => new(value);
    public static ExtensionFlagValue String(string value) => new(value ?? throw new ArgumentNullException(nameof(value)));
}

public sealed record ExtensionFlagOptions(ExtensionFlagKind Type, string? Description = null,
    ExtensionFlagValue? DefaultValue = null);

public sealed record ExtensionFlagRegistrationInfo(string OwnerId, long OwnerGeneration, string Name,
    ExtensionFlagOptions Options);

/// <summary>Explicit host-owned flag values. Defaults are committed as one bounded batch after owner activation.
/// The host supplies overrides; this dependency does not parse CLI arguments or grant message/provider effects.</summary>
public interface IExtensionHostFlagValues
{
    bool TryGetValue(string name, out ExtensionFlagValue? value);
    void CommitDefaults(ImmutableArray<KeyValuePair<string, ExtensionFlagValue>> defaults);
}

/// <summary>Optional native facade bound to an actual registry owner. Registration metadata is staged with
/// native owner registration; getFlag is visible only for names this extension registered.</summary>
public interface IExtensionFlagRegistrationFacade
{
    string OwnerId { get; }
    long OwnerGeneration { get; }
    IExtensionRegistration RegisterFlag(string name, ExtensionFlagOptions options);
    ExtensionFlagValue? GetFlag(string name);
}
