// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/extensions/types.ts (ExtensionAPI.on with a
// result: cache_warming_decision, before_provider_request, before_provider_headers) and core/extensions/runner.ts.
using PiSharp.Contracts;
using PiSharp.Extensions.Events;

namespace PiSharp.Extensions;

/// <summary>Source <c>pi.on(topic, handler)</c> for events whose handlers return a result. The handler receives the Pi event
/// object (with <c>type</c>) and returns the Pi result object, or null for <c>undefined</c>. Supported topics:
/// <c>cache_warming_decision</c> (<c>{action}</c>), <c>before_provider_request</c> (a replacement payload) and
/// <c>before_provider_headers</c> (the edited <c>headers</c> object; a null header value deletes it).</summary>
public sealed record ExtensionEventHandlerDescriptor(string RegistrationId, string Topic,
    ExtensionReducerCallback<JsonData, JsonData> HandleAsync);

public interface IExtensionEventHandlerRegistry : IExtensionRegistry
{
    IExtensionRegistration RegisterEventHandler(ExtensionEventHandlerDescriptor descriptor);
}
