using System.Collections.Immutable;

namespace PiSharp.Extensions;

/// <summary>Accepted logical selection. This is not a durable checkpoint acknowledgment.</summary>
public sealed record ExtensionToolActivationSelection(long Revision, ImmutableArray<string> Names);
public interface IExtensionToolActivationContext : IExtensionContext
{
    ImmutableArray<string> GetActiveTools();
    ExtensionToolActivationSelection SetActiveTools(ImmutableArray<string> names);
}
public interface IExtensionSessionToolActivationProvider : IExtensionSessionActionProvider { }
public interface IExtensionSessionToolActivationScope : IExtensionSessionActionScope
{
    ImmutableArray<string> GetActiveTools();
    ExtensionToolActivationSelection SetActiveTools(ImmutableArray<string> names);
}
public static class ExtensionToolActivationFeatures
{ public const string Feature = "callback-tool-activation"; }
