// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/coding-agent/src/core/extensions/types.ts (ExtensionAPI.on for the
// events whose handlers return a result) and core/extensions/runner.ts.
using PiSharp.Contracts;
using PiSharp.Extensions.Events;

namespace PiSharp.Extensions;

/// <summary>Source <c>pi.on(topic, handler)</c> for events whose handlers return a result. The handler receives the Pi event
/// object (with <c>type</c>) and returns the Pi result object, or null for <c>undefined</c>. The CLI dispatches nine topics:
/// <c>project_trust</c> (<c>{trusted, remember}</c>; the first result that is not <c>trusted: "undecided"</c> decides),
/// <c>resources_discover</c> (<c>skillPaths</c>, <c>promptPaths</c> and <c>themePaths</c>, collected from every handler),
/// <c>session_before_compact</c> (<c>{cancel}</c> or <c>{compaction}</c>; the last result wins and a cancel ends the dispatch),
/// <c>cache_warming_decision</c> (<c>{action}</c>), <c>before_provider_request</c> (a replacement payload),
/// <c>before_provider_headers</c> (the edited <c>headers</c> object; a null header value deletes it), <c>turn_end</c> and
/// <c>agent_before_settle</c> (<c>{entries, continue}</c>: the boundary entries to commit and whether the run continues) and
/// <c>message_end</c> (<c>{message}</c>: a replacement message with the same role).</summary>
public sealed record ExtensionEventHandlerDescriptor(string RegistrationId, string Topic,
    ExtensionReducerCallback<JsonData, JsonData> HandleAsync);

public interface IExtensionEventHandlerRegistry : IExtensionRegistry
{
    IExtensionRegistration RegisterEventHandler(ExtensionEventHandlerDescriptor descriptor);
}
