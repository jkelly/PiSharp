using PiSharp.Contracts;

namespace PiSharp.Extensions.Facade.Context;

/// <summary>Optional reads from the exact admitted session host. This capability grants no settings
/// publication or thinking mutation authority. A runtime that has not been bound must refuse reads.</summary>
public interface IExtensionSettingsThinkingReadHost : IExtensionContextReadHost
{
    JsonData GetSettings(IExtensionContext context);
    string GetThinkingLevel(IExtensionContext context);
}

/// <summary>Live reads valid only inside the originating admitted callback. Settings are an owned
/// immutable copy of the whole effective settings object; thinking is the acknowledged current level.</summary>
public interface IExtensionSettingsThinkingReadFacade
{
    JsonData GetSettings();
    string GetThinkingLevel();
}
