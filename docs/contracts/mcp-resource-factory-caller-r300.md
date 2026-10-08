# MCP actual resource factory caller R300

The existing McpSessionRuntimeFactory admission gains an optional ResourceRegistration init property. With no dependency, its behavior and positional constructor remain unchanged. With an explicitly admitted McpAdmittedResourceRegistration, activation first acquires its existing server captures, plans exposure, then stages ordinary resource descriptors through ExtensionRegistry.PrepareToolCatalogReplacement and ExtensionAgentBinding before calling PrepareDiscovery. Genuine codemode/search identities therefore see the final catalog; resources never mint discovery identities.

The registration receives the actual registry/resource scope, exact final policy, one schema validator, one hook composer, explicit output saver and bounded options. It rejects multicast inputs before effects and consumes preparation once. It preserves existing catalog entries, checks scope/registration metadata and exact declaration/adapter references after discovery preparation, then binds its dispatcher only after existing capture owner binding through the existing SessionRuntimeLease.BindOwner callback. The caller must include the resource registry/scope in admitted NativeResources or DiscoveryResources so failed acquisition and ordinary session retirement join their callbacks. There is no extra activation or resource lifetime owner.

McpPreOpenServerCapture now forwards CaptureResourceServer only for the actual current, live, bound attachment with matching reserved/runtime/session generations and stable close fence. It borrows the existing runtime request path. Resource owner identity belongs to the dedicated resource scope; its OwnerGeneration is not equated with runtime/session generation.

Four authored controls use the actual factory and PersistentSessionLifecycle: resource descriptors are present before discovery and all three prepared methods reach the channel with actual identity; absent dependency preserves an empty ordinary catalog; owning shutdown joins a held saver; multicast validator/composer/saver are rejected before effects. These controls remain unregistered/unexecuted. Test Program/startup/host admission remain parent-owned.

## Static type and signature audit

| Use | Existing source authority |
|---|---|
| ExtensionToolDescriptor, IPiSharpExtension, IExtensionRegistry | Abstractions/RegistrationContracts.cs, namespace PiSharp.Extensions |
| ExtensionToolRegistrationInfo, ExtensionRegistry, RegistrationScope, replacement PreviewSnapshot and Commit returning snapshot | Runtime/Registration/ExtensionRegistrySnapshot.cs, RegistrationScope.cs, ExtensionRegistry.ToolCatalogPublication.cs, namespace PiSharp.Extensions.Runtime |
| ExtensionToolArgumentValidator and ExtensionAgentBinding constructor, Registrations, Adapters, PreparedHooks, declarations, GetLoadoutPreparation | Extensions.Agent/ExtensionAgentBinding.cs |
| SessionRegisteredTool, registry WithToolCatalog, RegisteredTools, PreparedToolHooks, UsesFinalActionPolicy, InvocationOwnerGeneration and UsesCapturedToolBinding | CodingAgent/SessionRuntimeRegistry.cs, .Catalog.cs, .CapturedBinding.cs |
| McpPreparedHookComposer | CLI/Mcp/McpPreparedServer.cs |
| McpToolCatalogPlan.ResourceToolsExposure, McpCatalogPlanner.ToToolExposure, McpExposure | Abstractions/Mcp/Configuration/McpCatalogPlanner.cs and McpConfigurationModels.cs |
| Resource delegate/result/server/limits | Abstractions/Mcp/Resources/McpResourceContracts.cs; Runtime/Mcp/Resources/McpResourceTools.cs |
| Prepared invocation context and identity | Abstractions/ToolInvocationContracts.cs, Mcp/Runtime/McpRuntimeContracts.cs |
| Lifecycle ctor named backend/runtimeForAttachment, CreateAsync/AttachAsync | CodingAgent/PersistentSessionLifecycle.cs |
| Async stream event constructors/messages and ToolInvocation | Contracts/Messages/AssistantMessage.cs, Contracts/Streaming/StreamEvents.cs, Agent tool contracts; same constructors used by frozen R298 controls |
| Session backend constructor and InMemory mode, header codec, attachment/disposal | Sessions/Storage/SessionStorageBackend.cs, Sessions/Serialization/SessionEntryCodec.cs, CodingAgent/ReplaceableAgentSession.cs |

All new explicit imports resolve to the exact local source namespace. CLI SDK project references Runtime and Extensions.Agent; transitive references include Abstractions/Contracts/Agent. CodingAgent.Tests references CLI/CodingAgent. Default SDK Compile globs include both new .cs files; no excluded globs or new project/package edits. Directory.Build.props targets net10.0 with nullable and implicit usings enabled and warnings as errors. global.json pins SDK10.0.401. No Span/ReadOnlySpan/ref-struct local survives await; Interlocked ref targets only an int field before async stream suspension. New registration methods are synchronous; borrowed asynchronous validators execute through the existing prepared binding pipeline. Existing dependency pins and additional source/project hashes accompany the immutable handoff. Whitespace check passed; no compiler/build/tests/native/network execution occurred.

Remaining host work: parent must supply opt-in registration and admitted saver from its actual startup acquisition, import/register these authored controls and qualify the composed snapshot. No external server, saver policy or production startup authority is inferred here.

