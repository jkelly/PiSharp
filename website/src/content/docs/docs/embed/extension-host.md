---
title: Host extensions
description: Load and run native and Node extensions in your own app.
---

The libraries `pisharp` uses to load extensions are public, so your own app can host C# extensions too. You load the assembly, activate it in an `ExtensionRegistry`, bind the registry to an agent with `ExtensionAgentBinding`, and dispatch events at the right moments. This page follows how `pisharp` wires these pieces. For the API an extension sees, read [C# extension API](/docs/extensions/api/); for the events you raise, [Extension events](/docs/extensions/events/).

## Packages

| Package | What you use from it |
| --- | --- |
| `PiSharp.Extensions.Runtime` | `ExtensionRegistry` and its options, `RegistrationScope`, the typed event dispatchers, the MCP runtime and transports, `ResourceReloadWorkflow`, `PluginAssemblyLoader` and `ExtensionManifestReader` |
| `PiSharp.Extensions.Agent` | `ExtensionAgentBinding`, which turns registered tools and hooks into an agent's tools and hooks, `RegisteredExtensionInputAdmission`, and `ExtensionProviderTransport` |
| `PiSharp.Sessions` | `SessionSystemReplay`, which reads the system prompt back out of a transcript for the context hooks |
| `PiSharp.Compatibility.Node` | `PiNodeHost`, the process host for Pi's TypeScript extensions |
| `PiSharp.ExtensionHost` | `NodeWorkerSupervisor` and the extension worker protocol |

```powershell
dotnet add package PiSharp.Extensions.Runtime --version 1.2.0.1
dotnet add package PiSharp.Extensions.Agent --version 1.2.0.1
dotnet add package PiSharp.Sessions --version 1.2.0.1
```

## Load an extension assembly

`pisharp` reads `pisharp-extension.json`, loads the assembly into its own collectible `AssemblyLoadContext`, and creates the entry type. The load context first asks the default context, so the extension shares your copy of PiSharp's assemblies and of .NET. Anything else it resolves from the extension's folder through its `.deps.json`. `pisharp` also reads each file into memory before loading it, so the files stay replaceable while the extension runs.

A loader along the same lines:

```csharp
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using PiSharp.Extensions;

public static class ExtensionLoader
{
    public static IPiSharpExtension Load(string manifestPath)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var directory = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        var assemblyPath = Path.GetFullPath(Path.Combine(directory, manifest.RootElement.GetProperty("assembly").GetString()!));
        var entryType = manifest.RootElement.GetProperty("entryType").GetString()!;

        var context = new ExtensionLoadContext(assemblyPath);
        var type = context.LoadFromAssemblyPath(assemblyPath).GetType(entryType, throwOnError: true)!;
        if (!typeof(IPiSharpExtension).IsAssignableFrom(type) || type.GetConstructor(Type.EmptyTypes) is null)
            throw new InvalidOperationException($"Type \"{entryType}\" must be a public IPiSharpExtension with a parameterless constructor");
        return (IPiSharpExtension)Activator.CreateInstance(type)!;
    }
}

internal sealed class ExtensionLoadContext(string entryPath)
    : AssemblyLoadContext("extension:" + Path.GetFileName(entryPath), isCollectible: true)
{
    private readonly AssemblyDependencyResolver resolver = new(entryPath);

    protected override Assembly? Load(AssemblyName name)
    {
        // Share the host's assemblies: PiSharp's contracts and .NET itself.
        try { return Default.LoadFromAssemblyName(name); }
        catch (Exception error) when (error is FileNotFoundException or FileLoadException or BadImageFormatException) { }
        return resolver.ResolveAssemblyToPath(name) is { } path ? LoadFromAssemblyPath(path) : null;
    }

    protected override nint LoadUnmanagedDll(string name) =>
        resolver.ResolveUnmanagedDllToPath(name) is { } path ? LoadUnmanagedDllFromPath(path) : 0;
}
```

A collectible load context is not a sandbox. The extension runs with your process's permissions.

## Activate it in a registry

`ExtensionRegistry` owns every loaded extension, its registrations and their callbacks.

| Constructor | Use |
| --- | --- |
| `ExtensionRegistry(options = null, uiProvider = null)` | No session view. Contexts have no `SessionSnapshot`. |
| `ExtensionRegistry(options, uiProvider, sessionProvider)` | Adds an `IExtensionSessionViewProvider`, which turns on the session features its interfaces support. |
| `ExtensionRegistry(options, uiProvider, sessionProvider, facadeHost)` | Adds an `IExtensionContextReadHost` for [facade commands](/docs/extensions/api/#facade-commands). |

`ActivateAsync(ownerId, extension, token)` calls the extension's `InitializeAsync`, publishes its registrations when it returns, and gives you its `RegistrationScope`. The owner id is your name for that load: letters, digits, `.`, `_` and `-`. If initialization fails, the registry disposes the extension and throws an `ExtensionRegistrationException` with `Failure` `InitializationFailed`, or `CleanupFailed` when disposal failed too. Cancellation and registration errors are thrown as they are.

```csharp
await using var registry = new ExtensionRegistry();
var scope = await registry.ActivateAsync("ext-0", ExtensionLoader.Load(manifestPath));
```

The registry owns the extension from then on. Disposing the scope cancels the extension's lifetime token, waits for its running callbacks and calls its `DisposeAsync`; failures come back as `CleanupFailed`. Disposing the registry does this for every scope. A callback can't dispose its own scope (`ReentrantDisposal`).

| `RegistrationScope` member | Meaning |
| --- | --- |
| `OwnerId`, `OwnerGeneration`, `Features` | As the extension sees them on its registry |
| `WithdrawRegistrations(keepTools)` | Removes every registration of the owner in one step. `pisharp` calls it with `false` when `/reload` retires an extension. |
| `QuiesceAsync(token)` | Stops new callbacks and waits for running ones. Dispose the returned lease to resume. |
| `DisposeAsync()` | Unloads the owner as above. |

The scope is also the `IExtensionRegistry` the extension received, with every [optional registry](/docs/extensions/api/#optional-registries).

### Registry options

`ExtensionRegistryOptions` is a record with `init` properties. The last column is what `pisharp` sets for the registry its extensions share, so they behave as they do in Pi.

| Property | Default | `pisharp` |
| --- | --- | --- |
| `MaximumOwners` | 128 | unbounded |
| `MaximumRegistrations`, `MaximumRegistrationsPerOwner` | 1,024 and 128 | unbounded |
| `MaximumMetadataCharacters` | 1,048,576 | 64 MiB |
| `MaximumIdentifierCharacters` | 128 | 128 |
| `MaximumDescriptionCharacters` | 4,096 | 1 MiB |
| `MaximumJsonCharacters`, `MaximumJsonDepth` | 65,536 and 32 | 64 MiB and 1,000 |
| `MaximumConcurrentDispatches` | 32 | unbounded |
| `MaximumObservationCharacters` | 67,108,864 | 67,108,864 |
| `MaximumSessionBranchEntries`, `MaximumSessionCharacters`, `MaximumSessionUtf8Bytes` | 1,000,000 entries, 64 Mi characters, 64 MiB | unbounded |
| `ReservedToolNames` | `read`, `bash`, `powershell`, `edit`, `write`, `grep`, `find`, `ls` | none |
| `ReservedCommandNames` | `help`, `quit`, `exit`, `reload`, `settings`, `trust`, `permissions` | none |
| `AllowAnyCommandName` | `false`: command names are identifiers | `true`: any name without whitespace or control characters |
| `SuffixDuplicateCommandNames` | `false`: a taken name is refused | `true`: duplicates become `name:1`, `name:2` |
| `FollowCurrentSnapshot` | `false`: a dispatch uses the registrations it captured | `true`: it uses the current ones, so handlers added after a reload take part |
| `KeepsLoneSurrogates` | `false` | `true`: JSON may hold lone surrogates, as JavaScript values do |

`ExtensionRegistrationException` has `Failure`, `OwnerId` and `Operation`. `Failure` is one of `InvalidDescriptor`, `DuplicateOwner`, `DuplicateRegistrationId`, `DuplicateName`, `ReservedName`, `LimitExceeded`, `InactiveScope`, `StaleSnapshot`, `InitializationFailed`, `CleanupFailed` or `ReentrantDisposal`.

## Connect it to an agent

`ExtensionAgentBinding` captures the registry's current registrations and adapts them to the agent loop: tools become `ToolDefinition`s, and the `tool_call`, `tool_result`, `before_agent_start`, `context` and `context_with_system` handlers become agent hooks. See [Agent](/docs/embed/agent/) for the agent itself.

```csharp
using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Agent;
using PiSharp.AI;
using PiSharp.Contracts;
using PiSharp.Extensions.Agent;
using PiSharp.Extensions.Runtime;
using PiSharp.Sessions.Context;
using NativeAgent = PiSharp.Agent.Agent;

public static async Task RunAsync(string manifestPath, IChatTransport transport, ModelDescriptor model)
{
    await using var registry = new ExtensionRegistry();
    await registry.ActivateAsync("ext-0", ExtensionLoader.Load(manifestPath));

    var replay = new SessionSystemReplay();
    var binding = new ExtensionAgentBinding(registry, new AllowAllTools(),
        // The agent's tool invoker already checks arguments against each tool's JSON Schema.
        (tool, arguments, token) => ValueTask.FromResult(true),
        options: new ExtensionAgentBindingOptions
        {
            // Context and before_agent_start handlers need the system prompt the transcript holds.
            RestoreSystemMessage = (messages, token) => replay.Replay(messages, token).CurrentMessage,
            ReadSystemPrompt = (messages, token) => replay.Replay(messages, token).Prompt,
            ReportEventDiagnostic = (diagnostic, token) =>
            {
                Console.Error.WriteLine($"Extension error ({diagnostic.OwnerId}, {diagnostic.EventName}): {diagnostic.ErrorText}");
                return ValueTask.CompletedTask;
            },
        });

    await registry.DispatchObservationsAsync(registry.CaptureSnapshot(), "session_start",
        JsonData.Parse("""{"type":"session_start","reason":"startup"}"""));

    var configuration = new AgentConfiguration(model, transport, binding.Tools, Hooks: binding.Hooks);
    await using var agent = new NativeAgent(configuration, () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), new ConsoleEvents());

    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    TranscriptEntry system = binding.CreateDeclarationMessage("You are a helpful coding assistant.", now);
    var user = new TranscriptEntry("user", JsonData.Parse(JsonSerializer.Serialize(new { role = "user", content = "Say hello.", timestamp = now })));
    var result = await agent.PromptAsync([system, user]);
    Console.WriteLine(result.Reason);

    // Run a slash command the extension registered. In pisharp the arguments are the text after the name.
    await registry.InvokeCommandAsync(registry.CaptureSnapshot(), "hello", JsonData.Parse("\"world\""));
}

internal sealed class AllowAllTools : IToolActionPolicy
{
    public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action,
        CancellationToken cancellationToken) => ValueTask.FromResult(new ToolActionAuthorization(true));
}

internal sealed class ConsoleEvents : IAgentEventSink
{
    public ValueTask EmitAsync(AgentEvent observation, CancellationToken cancellationToken)
    {
        Console.WriteLine(observation.GetType().Name);
        return ValueTask.CompletedTask;
    }
}
```

`CreateDeclarationMessage(content, timestamp)` builds the system message, with the active tools in its `toolsAdded` list. The model sees the tools through it, so start the transcript with it. `SessionSystemReplay` folds the transcript's system messages back into the current prompt and tool state, as `pisharp` does. A binding holds a fixed snapshot of the registry's tools: build a new one, and a new declaration, when the extensions change.

| Constructor parameter | Meaning |
| --- | --- |
| `registry` | The registry to capture |
| `policy` | Your `IToolActionPolicy`. It authorizes each extension tool call with its final arguments, and can deny it or end the run. |
| `validateArguments` | An `ExtensionToolArgumentValidator(tool, arguments, token)` that admits the final arguments. Returning `false` rejects the call. |
| `transforms`, `resultTransforms` | `ToolActionTransform`s and `ToolResultTransform`s that run around each call |
| `invokerOptions` | `ToolInvokerOptions` for the tool invoker |
| `options` | `ExtensionAgentBindingOptions`, below |
| `sessionCancellationToken` | Cancels callbacks when your session ends |
| `capturedSnapshot` | A registry snapshot to bind instead of the current one |

| `ExtensionAgentBindingOptions` | Default | Meaning |
| --- | --- | --- |
| `MaximumTools` | 128 | Tools the binding admits |
| `MaximumDeclarationCharacters`, `MaximumDeclarationBytes` | 1,048,576 and 4,194,304 | Size of the declaration message |
| `ResultValues` | `new()` | `ToolResultValueOptions` for result admission |
| `ToolEventDispatch` | null | `ExtensionEventDispatchOptions` for the typed hooks |
| `MaximumToolEventHandlers` | 256 | Handlers per dispatch |
| `ReportEventDiagnostic` | null | Receives each `ExtensionEventDiagnostic` (handler failures and invalid results) |
| `InvocationScopes` | null | `ToolInvocationScopeOptions` for nested tool calls through `ExecuteToolAsync` |
| `RestoreSystemMessage` | null | Returns the system message to put back after a `context` handler replaced the conversation. Required once a `context` or `before_agent_start` handler is registered. |
| `ReadSystemPrompt` | null | Returns the current system prompt for `before_agent_start`. Required with such a handler. |
| `ActiveToolNames` | null | Names of the tools to activate. Null uses each tool's `DefaultActive`. |
| `ReportLoadoutDiagnostic` | null | Receives failures of `PrepareLoadout` callbacks |

| Member | Meaning |
| --- | --- |
| `Tools` | The `ToolDefinition`s to pass to `AgentConfiguration` |
| `Hooks` | `AgentHooks` with `before_agent_start` and the context hooks |
| `ContextHooks` | The context hooks alone, for hosts that build their own tool loadout |
| `PreparedHooks` | The `IPreparedToolHooks` that run `tool_call` and `tool_result` |
| `Adapters` | The schema-validating tool adapters behind `Tools` |
| `Snapshot`, `Registrations`, `ActiveRegistrations`, `RegisteredToolDeclarations` | What the binding captured |
| `LoadoutDiagnostics`, `GetLoadoutPreparation(name)` | Tool loadout state |

`pisharp` passes a validator that always returns `true`, as above, because the tool invoker validates the arguments against the schema itself.

## Dispatch events

The binding raises `tool_call`, `tool_result`, `before_agent_start`, `context` and `context_with_system`. Your host raises the other events when they happen, through the registry. Each method takes a snapshot from `CaptureSnapshot()`.

| Method | Use for |
| --- | --- |
| `DispatchObservationsAsync(captured, topic, observation, opToken, sessionToken)` | Observer events. Awaits every observer in order; an exception stops the dispatch and propagates. |
| `DispatchObservationsReportingAsync(captured, topic, observation, report, …)` | The same, but each failure goes to `report` and the next observer runs, as `pisharp` does |
| `ReduceEventAsync(captured, topic, initial, fold, report, …, stopAfter)` | Event handlers whose results build on each other. `ExtensionEventFold(currentEvent, result)` returns the next event, or null to keep it. When `stopAfter(result)` returns `true`, no later handler runs, as with a cancelling `session_before_*` result. Failures go to `report`. |
| `DispatchUntilAsync(captured, topic, event, observe, report, …)` | Event handlers where the first decisive answer wins. Return `true` from `observe(ownerId, result)` to stop. |
| `HasObservers(captured, topic)`, `HasEventHandlers(captured, topic)` | Skip building the payload when nobody listens |
| `BeforeSessionSwitchAsync`, `BeforeSessionCreationAsync`, `BeforeSessionTreeAsync` | The session vetoes. They return `false`, or a result with `Cancel`, when an extension cancelled. |
| `DispatchUserBashAsync(captured, event, …)` | `user_bash`. Returns the winning patch or null. |
| `DispatchSessionShutdownAsync(captured, observation, report, retainedSessionSnapshot)` | `session_shutdown`, with a `reason` of `quit`, `reload`, `new`, `resume` or `fork`. Returns `false` when nobody listens. |
| `DispatchSessionCompactAsync`, `DispatchSessionInfoChangedAsync`, `DispatchReplacementSessionStartAsync` | `session_compact`, `session_info_changed`, and `session_start` in a replacement session |
| `InvokeToolAsync(captured, name, arguments, …)` | Calls a tool directly, outside an agent |
| `InvokeCommandAsync(captured, name, arguments, …)`, `CompleteCommandAsync(captured, name, prefix, …)` | Runs a command, or asks for its argument completions |
| `ResolveToolRenderers(captured, toolName, …)` | The renderers the extensions chose for a tool |

For `input`, pass a `RegisteredExtensionInputAdmission(registry, snapshot)` where your session admits prompts: as the `admission` of `PersistentAgentSession.SubmitInputAsync`, or as the `inputAdmission` of the [RPC host](/docs/embed/rpc/). Without the registry, `ExtensionEventDispatcher` runs the typed reducers over handler sets you fill yourself, and `RegisteredExtensionEventDispatcher(registry, admitToolResult)` runs them over a registry's handlers.

The payloads are on [Extension events](/docs/extensions/events/). The events are Pi's: when your host does something Pi does, send the event Pi sends.

## Give extensions a UI and a session

The registry asks two providers for what callbacks see:

- **`IExtensionUiProvider`** has `OpenScope(context)`, which returns the `IExtensionUiScope` a callback uses as `Ui`. `UnavailableExtensionUiProvider` answers every dialog with `Unavailable`; `pisharp` uses it when it has no UI to offer. [`RpcExtensionUiCoordinator`](/docs/embed/rpc/#extension-ui) forwards dialogs to an RPC client.
- **`IExtensionSessionViewProvider`** has `Capture(context)`, which returns the `ExtensionSessionSnapshot` of the current branch, or null. Implement `IExtensionSessionActionProvider` too, and its derived interfaces, to give extensions session writes, replacement, creation, the catalog, context edits, compaction summaries and tool activation. The registry turns on the matching [features](/docs/extensions/api/#features) from the interfaces your provider implements.

`pisharp`'s own providers are internal; they connect to its `ReplaceableAgentSession`. See [Sessions](/docs/embed/sessions/) for the session side.

## Reload extensions

To pick up a rebuilt extension, dispose its scope and activate a fresh instance from a new load context. `pisharp`'s `/reload` withdraws the old owner's registrations, unregisters the MCP servers it added, and activates the new copy under a new owner id.

When a reload touches more than extensions, `ResourceReloadWorkflow<TGeneration>` runs it as one transaction. A generation is your own class that holds one set of loaded resources. The workflow stages the candidate, shuts the old generation down, drains it, publishes the candidate, and cleans up what is left. A candidate that was never published is rolled back, and the old generation never comes back once it was drained.

```csharp
using System.Globalization;
using PiSharp.Contracts;
using PiSharp.Extensions.Runtime;
using PiSharp.Extensions.Runtime.Reloading;

public sealed class LoadedExtensions(ExtensionRegistry registry, RegistrationScope scope, string fingerprint)
{
    public ExtensionRegistry Registry { get; } = registry;
    public RegistrationScope Scope { get; } = scope;
    public string Fingerprint { get; } = fingerprint;
}

public static async Task<ResourceReloadReceipt<LoadedExtensions>> ReloadAsync(LoadedExtensions current, string manifestPath,
    Func<LoadedExtensions, ValueTask> publish, CancellationToken hostLifetime)
{
    var fingerprint = File.GetLastWriteTimeUtc(manifestPath).Ticks.ToString(CultureInfo.InvariantCulture);
    var plan = new ResourceReloadPlan(
        [new ResourceReloadEntry("extension", manifestPath, current.Fingerprint)],
        [new ResourceReloadEntry("extension", manifestPath, fingerprint)]);
    var workflow = new ResourceReloadWorkflow<LoadedExtensions>(current, plan, new ResourceReloadOperations<LoadedExtensions>
    {
        StageAsync = async (changes, token) =>
        {
            var registry = new ExtensionRegistry();
            try
            {
                var scope = await registry.ActivateAsync("ext-0", ExtensionLoader.Load(manifestPath), token);
                return new LoadedExtensions(registry, scope, fingerprint);
            }
            catch
            {
                await registry.DisposeAsync();
                throw;
            }
        },
        ShutdownAsync = async (old, token) =>
        {
            await old.Registry.DispatchSessionShutdownAsync(old.Registry.CaptureSnapshot(),
                JsonData.Parse("""{"type":"session_shutdown","reason":"reload"}"""));
        },
        // Stop callbacks into the old extension and wait for running ones. Disposing its registry later ends the pause.
        InvalidateAndDrainAsync = async old => { await old.Scope.QuiesceAsync(); },
        PublishAsync = async (old, candidate) =>
        {
            await publish(candidate);
            return new ResourceReloadPublication(ResourceReloadAuthority.New);
        },
        CleanupAsync = generation => generation.Registry.DisposeAsync(),
        StartAndExtendAsync = (candidate, token) => candidate.Registry.DispatchObservationsAsync(candidate.Registry.CaptureSnapshot(),
            "session_start", JsonData.Parse("""{"type":"session_start","reason":"reload"}"""), token),
    }, hostLifetime);
    return await workflow.ReloadAsync();
}
```

| Step | Operation | Runs |
| --- | --- | --- |
| Stage | `StageAsync(plan, token)` | First. Builds the candidate, which must be a new object. It must clean up whatever it doesn't return. |
| Shutdown | `ShutdownAsync(old, token)` | Tells the old generation to shut down, for example with `session_shutdown`. |
| InvalidateAndDrain | `InvalidateAndDrainAsync(old)` | Rejects stale contexts and waits for running callbacks. |
| Publish | `PublishAsync(old, candidate)` | Makes the candidate current. Returns a `ResourceReloadPublication(Authority, Failure)` with `New`, or `None` or `Unknown` and the failure. A throw counts as `Unknown`. |
| RetireOld | `CleanupAsync(old)` | Whenever the old generation was drained, whatever the publication did |
| RollbackCandidate | `CleanupAsync(candidate)` | When a failure came before publication, or publication answered `None` |
| StartAndExtend | `StartAndExtendAsync(candidate, token)` | When publication answered `New` and no step failed |

`ReloadAsync(admissionToken)` returns a `ResourceReloadReceipt` with the final `Authority` (`Old`, `New`, `None` or `Unknown`), the old generation and the candidate, flags for the steps that ran, and every `ResourceReloadFailure(Phase, Cause)`. Failures don't throw; read them from the receipt. A workflow runs one attempt, and every `ReloadAsync` call returns that attempt. A callback that awaits its own reload gets an `InvalidOperationException`. `hostLifetime` must be a cancelable token; `admissionToken` only cancels the call before it starts. With `Unknown`, the receipt keeps the candidate for you to recover.

`ResourceReloadPlan(current, replacement)` compares two catalogs of `ResourceReloadEntry(Kind, Key, Fingerprint)`, up to 4,096 entries each, and lists in `Changes` what was `Added`, `Changed` or `Removed`. Kind and key identify an entry; a different fingerprint means it changed.

`pisharp session …` uses this workflow for its reload. The everyday `/reload` of plain `pisharp` uses its own path.

## Chat providers from extensions

The chat-provider contracts work only through a host that hands them to extensions; plain `pisharp` doesn't do that for C# extensions. If your host does:

| Type | Use |
| --- | --- |
| `ExtensionProviderRegistrationHost(registry, admittedBuiltins, configurationAdapter)` | Holds the provider catalog. `Bind(registry)` returns the `IExtensionProviderRegistrationFacade` for one owner, with `RegisterProvider` and `UnregisterProvider`. Call `CommitOwnerProviders(scope)` after the owner activated. |
| `IExtensionProviderConfigurationAdapter` | Your `Resolve(name, configuration)` turns Pi's `registerProvider(name, config)` into an `ExtensionProviderDefinition(Name, Models, StreamSimple)`. |
| `ExtensionProviderTransport(host)` | An `IChatTransport` and `IThinkingLevelTransport` that streams through the registered providers. Pass it to `AgentConfiguration`. |

## MCP servers

`McpServerRuntime` is the client side of one MCP server: connection, tool catalog, tool calls and resources. `pisharp` runs one per configured server, see [MCP](/docs/guides/mcp/).

| Member | Meaning |
| --- | --- |
| `McpServerRuntime(entry, options, acquire, publish)` | `entry` is an `McpServerEntry` from the configuration reader. `acquire` is an `McpAdmittedChannelFactory` that opens a request channel. `publish` is an `McpCatalogPublisher` that commits the tool catalog and returns an `McpCatalogPublicationReceipt(Generation, Revision, Published)`. |
| `ConnectAsync`, `ReconnectAsync`, `ReconfigureAsync(next, connect)`, `DisconnectAsync` | Connection control |
| `RefreshToolsAsync`, `HandleNotificationAsync(method)` | Re-lists tools, for example after `notifications/tools/list_changed` |
| `CallToolAsync(toolName, arguments, context, …)` | Calls a tool |
| `CaptureResourceServer`, `RequestResourceAsync` | Resource reads |
| `Snapshot` | The current `McpRuntimeSnapshot(Generation, Revision, Catalog, InitializeResult, OriginalTools)` |
| `CloseAsync`, `DisposeAsync` | Shutdown |

`McpRuntimeOptions(Generation, ClientVersion, Roots)` sets its `Limits`, an `McpRuntimeLimits` of 1,000 `tools/list` pages and 16 MiB per schema and per response by default.

The request channel is an `McpJsonRpcRequestChannel(wire, limits, notification, clock)` over one of two wire transports:

| Transport | Construction |
| --- | --- |
| stdio | `McpStdioTransport(lease, limits)`. `lease` is an `IMcpAdmittedDuplexLease` with the process's `Input` and `Output` streams, `StartAsync` and `CloseAsync`. Frames are LF-delimited JSON-RPC messages. |
| Streamable HTTP | `McpStreamableHttpTransport(binding, createOperation, limits, clock)`. `binding` is an `McpHttpBinding(Endpoint, Headers, Profile)`, and `McpHttpProfile(OpenGetStream, DeleteSessionOnClose, MaximumReconnects, InitialReconnectMilliseconds, MaximumReconnectMilliseconds, DeleteTimeoutMilliseconds)` sets the session behavior. |

`McpTransportLimits` defaults to 16 MiB frames and a 4,096-byte read buffer. The transports are in `PiSharp.Extensions.Runtime.Mcp.Transport`; the contracts are in `PiSharp.Extensions.Mcp.*`. Failures surface as `McpRuntimeProtocolException` and `McpRuntimeDisconnectedException`.

To let extensions add servers, set the registry's `McpServerHost` to your `IExtensionMcpServerHost` with `Register`, `Unregister` and `List`. Without it, `RegisterMcpServer` throws `NotSupportedException`. The same holds for `ModelOperationProviderHost` and classifier and image providers.

## Hash-pinned packages

`pisharp session …` keeps an older, stricter loader for extension packages whose files are pinned by hash. Its manifest is [`schemas/pisharp-extension.schema.json`](https://github.com/jkelly/PiSharp/blob/main/schemas/pisharp-extension.schema.json), an experimental schema version 0, with an id, package version, host API range, target framework, runtime identifiers, declared capabilities, and a SHA-256 for every artifact. By default the reader admits only `win-x64` packages.

| Type | Use |
| --- | --- |
| `ExtensionManifestReader(options)` | `Read(metadata)` parses and checks a manifest. `InspectAsync(metadata, packageRoot, scope, scopeId, trustDecision)` also hashes the artifacts. It never authorizes loading. |
| `ExtensionTrustDecision` | Your explicit decision to inspect a package, bound to its root and manifest hash |
| `PluginAssemblyLoader(options)` | `LoadAsync(metadata, packageRoot, scope, scopeId, inspectionDecision, executionDecision, registry, token)` copies the verified files to a snapshot folder, loads them and activates the extension. Returns a `LoadedExtension`. |
| `PluginExecutionDecision` | Your explicit decision to run that exact package, with its artifact hashes |

Failures are `ExtensionManifestDiagnostic`s and `PluginLoadException`s with a `PluginLoadFailure` code. The plain `pisharp-extension.json` loader above is simpler, and it is what plain `pisharp` uses.

## TypeScript extensions

`pisharp` runs Pi's TypeScript extensions in a Node process through `PiNodeHost`, in `PiSharp.Compatibility.Node.Pi`.

| Member | Meaning |
| --- | --- |
| `PiNodeHost.FindNode(environment)` | `PISHARP_NODE`, or `node` on `PATH` (`node.exe` or `node.cmd` on Windows) |
| `PiNodeHost.FindHostScript(environment)` | `host.mjs` in `PISHARP_NODE_BRIDGE`, or `node-bridge/pi-host/host.mjs` next to your app |
| `StartAsync(PiNodeHostLaunch, IPiNodeHostPeer, token)` | Starts `node host.mjs` and waits until it is ready |
| `RequestAsync`, `RequestStreamAsync`, `NotifyAsync` | Messages to the Node side |
| `ProcessId`, `Exited`, `HasExited`, `DisposeAsync` | Process lifetime |

`PiNodeHostLaunch(NodeExecutable, HostScript, WorkingDirectory)` also takes `Environment`, a `StandardError` callback and a `StartupTimeout` (30 seconds). Your `IPiNodeHostPeer` answers the Node side's requests in `RequestAsync(PiNodeHostRequest, token)` and takes its notifications in `NotifyAsync`. A request marked `Synchronous` must be answered without calling back into Node.

:::caution[No supported embedding yet]
`host.mjs` ships with the `pisharp` tool, not in the NuGet package, and the peer methods it calls are defined by `pisharp`'s internal extension host. To run TypeScript extensions from your app today, start `pisharp --mode rpc` as a child process and talk to it over [RPC](/docs/reference/rpc/). The Node requirements are on [TypeScript extensions](/docs/extensions/node-bridge/).
:::

`NodeExtensionUiBroker` belongs to the older worker path below. It forwards only `ui.notify`, and its constructor is internal.

## Extension workers

`PiSharp.ExtensionHost` holds an earlier design for supervised, out-of-process extension workers. `pisharp` doesn't use it; PiSharp's tests do.

`NodeWorkerSupervisor` starts a Node worker from hash-pinned scripts in the PiSharp repository and a pinned Node v24.19.0, with `StartProbeAsync`, `StartProtectedPathsAsync`, `StartHelloAsync` or `StartCommandsAndInputAsync`. It then offers `RequestAsync`, `StartRequest`, `RegisterCallback`, `RevokeOwner`, `ProbeLivenessAsync`, `InvalidateSessionAsync`, `Snapshot`, `Completion` and `Termination`.

The worker protocol is LF-delimited JSON; one CR before the LF is stripped, and a non-empty tail at end of input fails. [`schemas/extension-worker-v1.schema.json`](https://github.com/jkelly/PiSharp/blob/main/schemas/extension-worker-v1.schema.json) defines each frame:

| `kind` | Fields besides `version` (1), `workerGeneration`, `sessionGeneration` |
| --- | --- |
| `hello` | `features` |
| `request` | `id`, `method`, `value`, `handle` |
| `response`, `progress` | `id`, `value` |
| `error` | `id`, `error` (`code`, `outcome` `notSent` or `unknown`) |
| `cancel` | `id` |
| `shutdown` | none |

A `value` is `{ "presence": "absent" }`, `{ "presence": "undefined" }` or `{ "presence": "json", "data": … }`. `WorkerFrameCodec(options)` encodes and decodes frames, rejecting unknown fields; `WorkerProtocolTransport` and `WorkerProtocolConnection` run the exchange. `WorkerProtocolOptions` defaults to 1 MiB frames, depth 32, 32 pending calls and 16 callbacks.
