---
title: Coding agent
description: "Embed the full coding agent: persistent sessions, settings, prompts, skills and the built-in tools."
---

`PiSharp.CodingAgent` turns the [agent loop](/docs/embed/agent/) into a coding agent you can host. It writes every run to a session file, reopens and switches sessions, loads settings, skills and prompt templates, and exports sessions to HTML. The built-in tools (`read`, `bash`, `edit`, `write`, `grep`, `find` and `ls`) live in `PiSharp.Tools`. This page shows how to put them together the way the `pisharp` CLI does.

## Install

```powershell
dotnet add package PiSharp.CodingAgent --version 1.2.0.1
dotnet add package PiSharp.Tools --version 1.2.0.1
```

`PiSharp.CodingAgent` references `PiSharp.Agent` and `PiSharp.Sessions`, but not `PiSharp.Tools`. Add `PiSharp.Tools` yourself to get the built-in tools. Your project targets `net10.0`.

Two packages are optional:

| Package | Adds |
| --- | --- |
| `PiSharp.Tools.Skia` | `SkiaImageCodec`, which decodes and resizes every image type `read` accepts. It brings SkiaSharp 4.152.1 and its native assets for Windows, macOS and Linux. See [Images](#images). |
| `PiSharp.PromptTemplates.Yaml` | YAML decoders for prompt template and skill frontmatter. It brings YamlDotNet 16.3.0. See [YAML frontmatter](#yaml-frontmatter). |

The types on this page come from these namespaces:

| Namespace | Holds |
| --- | --- |
| `PiSharp.CodingAgent` | `PersistentAgentSession`, `SessionRuntimeRegistry`, `PersistentSessionLifecycle`, `ReplaceableAgentSession`. |
| `PiSharp.CodingAgent.Configuration` | `StartupSettings`, `AgentRetryPolicy`, `PiConfigurationSchemas`. |
| `PiSharp.CodingAgent.Resources` | Prompt templates and input admission. |
| `PiSharp.CodingAgent.Resources.Skills` | Skills. |
| `PiSharp.CodingAgent.ToolSelection` | `AllowedToolSelection`. |
| `PiSharp.CodingAgent.Export` | `AgentSessionExport`, `SessionHtmlExport`, `SessionShare`. |
| `PiSharp.Tools` | `BuiltinToolCatalog`. |
| `PiSharp.Tools.Files` | The file tools, `LocalFileOperations` and `IFileOperations`. |
| `PiSharp.Tools.Processes` | `BashTool`, `ShellDiscovery`, `NativeProcessRunner`, `IProcessRunner`. |
| `PiSharp.Tools.Processes.Unix` | `UnixProcessRunner` for Linux and macOS. |
| `PiSharp.Tools.Images` | `IImageCodec`, `BuiltinImageCodec`, `ImageResizeOptions`. |
| `PiSharp.Sessions.Serialization` | `SessionEntry` and `SessionEntryCodec`, for the session header. |

## What the library leaves to you

The CLI does several jobs that are not in the library. Your host does them, or leaves them out.

- **The system prompt.** The CLI's prompt builder, with its tool list and guidelines, is internal. You write your own system message.
- **Context files.** The library doesn't read `AGENTS.md` or `CLAUDE.md`. [Context files](/docs/reference/configuration/#context-files) describes what the CLI loads; add what you want to your system message.
- **Credentials.** The library doesn't read `auth.json`. You give each provider its key (see [Authentication](/docs/embed/chat/#authentication)).
- **Tool policy.** You decide which paths and commands the tools may touch, with an `IToolActionPolicy`. The CLI's `pi` and `explicit` [tool policies](/docs/reference/configuration/#tool-policy) are internal.
- **Finding folders.** The library never looks for the [agent folder](/docs/reference/configuration/#the-agent-folder) or a project's `.pi` folder. Every API takes absolute paths.
- **Project trust.** The library doesn't know which projects you trust. You pass project paths only when you trust them (see [Project trust](/docs/reference/configuration/#project-trust)).

## Open a session in a folder

This program opens a session that works in a folder, or creates it on the first run, and sends one prompt. It uses the four tools that need nothing extra: `read`, `edit`, `write` and `ls`.

```csharp
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using PiSharp.Agent;
using PiSharp.AI;
using PiSharp.AI.Providers;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Sessions.Serialization;
using PiSharp.Tools;
using PiSharp.Tools.Files;

var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY")
    ?? throw new InvalidOperationException("Set OPENROUTER_API_KEY.");
var model = new ModelDescriptor("anthropic/claude-sonnet-4.5", "openai-completions", "openrouter");
using var provider = NativeProviderFactory.CreateCompletions(model,
    new Uri("https://openrouter.ai/api/v1/chat/completions"), key);

// The folder the agent works in, as a canonical absolute path.
var root = await new LocalFileOperations().CanonicalizeAsync(
    Path.GetFullPath(args.Length > 0 ? args[0] : "."), CancellationToken.None);
var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

// read, edit, write and ls. bash, grep and find need more setup.
var catalog = new BuiltinToolCatalog(root, home);
ImmutableArray<SessionRegisteredTool> tools =
    [.. catalog.Registered.Select(tool => new SessionRegisteredTool(tool.Declaration, tool.Adapter))];
var registry = new SessionRuntimeRegistry([new SessionModelBinding(model, provider)], tools, new WorkspacePolicy(root));

var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyAgent", "sessions");
Directory.CreateDirectory(folder);
var path = Path.Combine(folder, "demo.jsonl");

await using var session = await OpenOrCreateAsync();
using var subscription = session.Subscribe(new ConsoleSink());
var result = await session.PromptAsync(Entry("user", new
{
    role = "user",
    content = "List the files here and summarize the README.",
    timestamp = Clock(),
}));
Console.WriteLine($"\nStopped: {result.Reason}. Session file: {session.Path}");

async Task<PersistentAgentSession> OpenOrCreateAsync()
{
    if (File.Exists(path))
        return await PersistentAgentSession.OpenWithRegistryAsync(path, registry, Clock, NextId, fallbackModel: model);

    var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new
    {
        type = "session", version = 3, id = NextId(),
        timestamp = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        cwd = root,
    }));
    var created = await PersistentAgentSession.CreateAsync(path, header, registry, model, Clock, NextId);
    // The first system message holds the prompt and declares the tools the model may call.
    await created.ConfigureAsync(new SessionRuntimeUpdate(SystemMessage: Entry("system", new
    {
        role = "system",
        content = $"You are a coding assistant working in {root}. Use the tools to read and change files.",
        timestamp = Clock(),
        toolsAdded = tools.Select(tool => tool.Declaration.Value).ToArray(),
    })));
    return created;
}

static long Clock() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
static string NextId() => Guid.NewGuid().ToString("N")[..8];
static TranscriptEntry Entry(string role, object body) => new(role, JsonData.Parse(JsonSerializer.Serialize(body)));

// Allows file actions inside the working folder and nothing else.
sealed class WorkspacePolicy(string root) : IToolActionPolicy
{
    public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction finalAction,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(new ToolActionAuthorization(
            finalAction.Kind == PreparedToolActionKind.Path && IsInside(finalAction.Target)));

    private bool IsInside(string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative == "." || !(relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative));
    }
}

sealed class ConsoleSink : IAgentEventSink
{
    public ValueTask EmitAsync(AgentEvent observation, CancellationToken cancellationToken)
    {
        switch (observation)
        {
            case TurnStreamObserved { Event: TextDelta delta }:
                Console.Write(delta.Delta);
                break;
            case ToolExecutionStarted started:
                Console.WriteLine($"\n> {started.Invocation.Call.Name} {started.Invocation.Call.Arguments}");
                break;
        }
        return ValueTask.CompletedTask;
    }
}
```

A few things to note:

- The header is Pi's session header, version 3. Its `cwd` must be set. `NextId` makes 8-character entry ids, as Pi does.
- The session file path must be fully qualified. A relative path fails with `InvalidConfiguration`.
- On the next run, `OpenWithRegistryAsync` restores the model, thinking level and tools the file records, and the conversation continues.
- `ConfigureAsync` writes the system message to the file. The session takes its active tools from the message's `toolsAdded` list, as the CLI does. A tool left out of the list is registered but not active.

## Persistent sessions

`PersistentAgentSession` wraps an [`Agent`](/docs/embed/agent/) and a session file. It writes each run's messages to the file and keeps an in-memory context of the selected branch. The file format is Pi's; see [Sessions](/docs/embed/sessions/) to read and change files directly. To drive a session over Pi's JSONL RPC protocol, see [RPC host](/docs/embed/rpc/).

### Create and open

| Method | Use it for |
| --- | --- |
| `CreateAsync(path, header, registry, initialModel, clock, nextEntryId, options)` | A new file whose model, thinking level and tools come from a registry. The thinking level is the registry's default for the model. |
| `OpenWithRegistryAsync(path, registry, clock, nextEntryId, options, fallbackModel)` | An existing file. It restores what the file records. |
| `OpenWithRegistryFactoryAsync(path, registryForWorkingDirectory, ...)` | An existing file whose tools depend on its folder. The factory gets the `cwd` from the validated header and returns a registry for it. |
| `OpenWithRuntimeFactoryAsync(path, runtimeForWorkingDirectory, ...)` | The same, when the session should own the services it gets through a `SessionRuntimeLease`. |
| `CreateAsync(path, header, configuration, clock, nextEntryId, options)` | A new file with a fixed `AgentConfiguration` and no registry. |
| `OpenAsync(path, configuration, clock, nextEntryId, options)` | An existing file with a fixed `AgentConfiguration`. |

Sessions made from a fixed `AgentConfiguration` can't change model or tools later, because `ConfigureAsync` needs a registry. Use the registry methods unless your configuration never changes.

When a file records no model, `OpenWithRegistryAsync` uses `fallbackModel`. When it records a model the registry doesn't bind, opening fails with `UnknownModel`, unless `SessionRuntimeRegistryOptions.RestoreFallbackModel` picks another. A recorded tool with no binding is left out instead of failing.

`clock` returns Unix time in milliseconds. `nextEntryId` returns a new entry id each call.

### Options

`PersistentAgentSessionOptions` takes these constructor parameters:

| Parameter | Default | Meaning |
| --- | --- | --- |
| `UseLatestLeaf` | `true` | Open at the newest entry. Set it to `false` with `SelectedLeafId` to open another branch. A new file needs `true`. |
| `SelectedLeafId` | `null` | The entry to open at. |
| `AgentOptions` | `null` | Options for the inner [`Agent`](/docs/embed/agent/#configuration). |
| `SessionLogStoreOptions` | `null` | Limits for reading and writing the file. See [Limits](/docs/embed/sessions/#limits). |
| `ContextOptions` | `null` | How the branch becomes the model's context. |

It also has these `init` properties:

| Property | Meaning |
| --- | --- |
| `LifetimeToolSelection` | Which tools the session may ever activate. See [Tool selection](#tool-selection). |
| `PromptPreflight` | Runs before an idle prompt is saved or sent, with the current model. Throw `SessionPromptRejectedException` to reject the prompt. |
| `SelectedModel` | Run every session on this model instead of the one the file records, like `--model`. |
| `NewSessionModel` | Picks the model for a session that has no messages yet, when `SelectedModel` is not set. |
| `NewSessionThinkingLevel` | Picks the thinking level for a session that has no messages yet, given the model and its supported levels. |
| `UnselectedModel` | A placeholder model for a session started with no model. A new session records no `model_change` for it. |

### Prompt and steer

| Member | Does |
| --- | --- |
| `PromptAsync(entry)` or `PromptAsync(entries)` | Appends the messages and runs. Returns the `AgentLoopResult`. |
| `ContinueAsync()` | Runs again from the current context. |
| `SubmitInputAsync(input, admission)` | Runs typed input through templates, skills and your handlers first. See [Prompt templates](#prompt-templates). |
| `Steer(entry)`, `FollowUp(entry)` | Queues a message while a run is going, as on [`Agent`](/docs/embed/agent/#steering-and-follow-up). |
| `SteeringMode`, `FollowUpMode` | `OneAtATime` or `All`, from settings or your own choice. |
| `Subscribe(sink)` | Sends [agent events](/docs/embed/agent/#events) to an `IAgentEventSink` until you dispose the result. |
| `Abort()` | Stops the current run. Returns `false` when nothing was running. |
| `WaitForIdleAsync()` | Waits until the session is idle. |
| `DisposeAsync()` | Closes the session and its file. |

`Snapshot` gives the current state. `Snapshot.Log` holds the header and entries, `Snapshot.Context` the selected branch with its `LeafId` and `ThinkingLevel`, and `Snapshot.Fault` the last failure. `Path` and `WorkingDirectory` give the file and the header's `cwd`.

### Change model, thinking and tools

`ConfigureAsync` takes a `SessionRuntimeUpdate` and writes each change to the file.

| Field | Does |
| --- | --- |
| `Model` | Switches model. The thinking level is kept, clamped to what the new model supports. |
| `ThinkingLevel` | Sets the thinking level, clamped to the model. |
| `SystemMessage` | Writes a new system message. Its `toolsAdded` list changes the tools. |
| `ActiveToolNames` (`init`) | Sets the active tools by name. You can't set it together with `SystemMessage`. |

`ConfigureAsync` throws `Session is already processing.` during a run. `SetActiveToolsAsync(names)` is a shortcut for `ActiveToolNames`, and `GetActiveTools()` lists the active names. `GetSystemPrompt()` and `GetActiveToolDeclarations()` return the current system prompt and the active tools' declarations.

### Errors

Session failures throw `PersistentAgentSessionException`. Its `Fault.Failure` is a `PersistentAgentSessionFailure`, such as `InvalidConfiguration`, `UnsupportedThinkingLevel`, `ModelMismatch`, `AppendFailed`, `Faulted`, `Disposed` or `SessionReplaced`. Bad arguments, such as a relative path or a header without a `cwd`, give `InvalidConfiguration`.

## The runtime registry

A `SessionRuntimeRegistry` holds every model and tool a session may use, and the policy the tools obey. Sessions borrow it; one registry can serve many sessions.

```csharp
new SessionRuntimeRegistry(models, tools, policy, options);
```

Each model is a `SessionModelBinding`:

| Parameter | Default | Meaning |
| --- | --- | --- |
| `Model` | | The `ModelDescriptor`. |
| `Transport` | | The `IChatTransport` that serves it. See [Providers and transports](/docs/embed/chat/#providers-and-transports). |
| `ToolHooks` | `null` | [Tool hooks](/docs/embed/agent/#tool-hooks) for runs on this model. |
| `ExecutionMode` | `Parallel` | Whether tool calls run in parallel or one at a time. |
| `Hooks` | `null` | `AgentHooks` for runs on this model. |

Each tool is a `SessionRegisteredTool(Declaration, Adapter, ExecutionMode)`. The declaration's `name` must match the adapter's name. Its `init` properties:

| Property | Default | Meaning |
| --- | --- | --- |
| `Exposure` | `Direct` | How the tool reaches the model. |
| `DefaultActive` | `true` | Whether the tool starts active. |
| `IsExtension` | `false` | Marks a tool from an extension, for default selection. |
| `PromptSnippet` | `null` | The tool's line in a generated system prompt. |
| `PromptGuidelines` | empty | Guidelines the tool adds to the system prompt. |
| `OutputSchema` | `null` | The shape of the tool's structured result. |

`SessionRuntimeRegistryOptions` sets limits and hooks:

| Option | Default | Meaning |
| --- | --- | --- |
| `MaximumModels` | 128 | Most model bindings. |
| `MaximumTools` | 128 | Most tools. |
| `MaximumCharacters` | 1,048,576 | Longest model identity, prompt section or name. It does not limit the conversation. |
| `ToolInvokerOptions` | `null` | Limits for tool calls. |
| `LifetimeToolSelection` (`init`) | `null` | Which tools sessions may ever activate. |
| `InitialActiveToolNames` (`init`) | `null` | The tools a new session starts with. |
| `BlockImages` (`init`) | `null` | Returns `true` to keep every image away from the provider, like the `images.blockImages` setting. |
| `RestoreFallbackModel` (`init`) | `null` | Picks a model when a file records one the registry doesn't bind. |
| `ServiceTier` (`init`) | `null` | The service tier for requests (experimental, unreleased; see [Service tiers](/docs/guides/service-tiers/)). |

`GetSupportedThinkingLevels(model)` and `GetDefaultThinkingLevel(model)` tell you what a model accepts.

## Switch, fork and list sessions

`PersistentSessionLifecycle` creates and opens sessions from one registry. `ReplaceableAgentSession` holds the current one and swaps it, as `/new`, `/resume` and `/fork` do in the CLI.

This snippet builds on [the first sample](#open-a-session-in-a-folder). `header` and `system` are the header and system message it makes.

```csharp
var lifecycle = new PersistentSessionLifecycle(registry, Clock, NextId,
    catalog: new SessionCatalog([new SessionCatalogStore("sessions", folder)]));

var first = await lifecycle.CreateAsync(path, header, model);
await first.ConfigureAsync(new SessionRuntimeUpdate(SystemMessage: system));
await using var sessions = lifecycle.Attach(first);

// List sessions started in this folder.
var page = await sessions.ListSessionsAsync(sessions.Current, new SessionCatalogQuery(PageSize: 20, WorkingDirectory: root));
foreach (var item in page.Items)
    Console.WriteLine($"{item.SessionId}  {item.Path}");

// Start a fresh session next to the current file.
await sessions.CreateAsync(sessions.Current, new AgentSessionCreationRequest(AgentSessionCreationKind.New));
var current = sessions.Current.Session;
```

`Current` is an `AgentSessionAttachment` with the `Session`, a `Generation` number and a `LifetimeToken`. Every call takes the attachment you expect to be current. If another call replaced it first, yours fails with `Session context is stale.` instead of acting on the wrong session.

| Method | Does |
| --- | --- |
| `CreateAsync(expected, request)` | Makes a new session file and attaches it. `AgentSessionCreationKind` is `New`, `ForkBefore` or `ForkAt` (with an `EntryId`), or `Clone`. |
| `SwitchAsync(expected, new AgentSessionSwitchRequest(path))` | Opens another file by absolute path and attaches it. |
| `ResumeAsync(expected, new AgentSessionResumeRequest(catalogKey))` | Opens a session from a catalog listing. |
| `ListSessionsAsync(expected, query)` | Lists sessions from the catalog, a page at a time. |
| `Abort()` | Stops the current session's run. |

New files go next to the current one, named `<timestamp>_<id>.jsonl` with `:` and `.` in the timestamp replaced by `-`. The new session id is a UUID v7. Disposing the `ReplaceableAgentSession` closes the attached session.

`SessionCatalogQuery` takes `PageSize` (32 by default), a `Cursor` from the last page's `NextCursor`, and an optional `WorkingDirectory`. Each `SessionCatalogItem` has the `Path`, `SessionId`, `CreatedTimestamp`, `WorkingDirectory`, `ParentSessionPath` and file size. See [List sessions](/docs/embed/sessions/#list-sessions).

## Settings

`StartupSettings.LoadAsync` reads the user and project `settings.json` files and merges them. It reads only the paths you give it and writes nothing.

```csharp
var agentFolder = Path.Combine(home, ".pi", "agent");
var settings = await StartupSettings.LoadAsync(new StartupSettingsRequest(
    UserPath: Path.Combine(agentFolder, "settings.json"),
    ProjectPath: Path.Combine(root, ".pi", "settings.json")));   // Only for a trusted project.

foreach (var diagnostic in settings.Diagnostics)
    Console.Error.WriteLine($"{diagnostic.Scope} {diagnostic.Path}: {diagnostic.Code}");

session.SteeringMode = settings.SteeringMode;
session.FollowUpMode = settings.FollowUpMode;
session.ConfigureAutomaticRetry(settings.RetryPolicy);
```

| `StartupSettingsRequest` | Meaning |
| --- | --- |
| `UserPath` | The user `settings.json`, as an absolute path. |
| `ProjectPath` | The project `.pi/settings.json`, as an absolute path. |
| `Overrides` | JSON that wins over both files, like command-line options. |

The user file loads first, then the project file, then `Overrides`. Objects merge key by key; other values replace. An old `queueMode` setting becomes `steeringMode`. A missing file is skipped.

The snapshot has the merged `Values` as `JsonData`, the `SteeringMode` and `FollowUpMode`, a `RetryPolicy`, and `Diagnostics`. Read any other setting from `Values` yourself. See [Settings](/docs/reference/settings/) for the keys.

| Diagnostic code | Means |
| --- | --- |
| `ReadFailed` | The file exists but couldn't be read. |
| `InvalidJson` | The file isn't valid JSON. The file is skipped. |
| `InvalidQueueMode:<name>` | A queue mode setting has an unknown value. |

### Schemas

`PiConfigurationSchemas` carries the JSON schemas Pi publishes for its files: `models.schema.json`, `settings.schema.json`, `keybindings.schema.json` and `theme.schema.json`. `Url(fileName)` gives the address under `https://pi.dev/schemas`, and `Read(fileName)` the schema text. The `Models`, `Settings`, `Keybindings` and `Theme` properties check JSON against them.

```csharp
foreach (var error in PiConfigurationSchemas.Settings.ErrorsJson(await File.ReadAllTextAsync(settingsPath)))
    Console.WriteLine($"{error.InstancePath}: {error.Message}");
```

`CheckJson` returns only `true` or `false`. See [Schemas](/docs/reference/configuration/#schemas).

### Retry

`AgentRetryPolicy` controls automatic retry after a provider error. Settings give you one, or you make your own.

| Parameter | Default | Meaning |
| --- | --- | --- |
| `enabled` | `true` | Whether to retry. |
| `maxRetries` | 3 | Most retries. The first call doesn't count. |
| `baseDelayMs` | 2000 | The first delay. Each retry doubles it. |
| `maxAgentDelayMs` | 60000 | The longest delay. Longer delays are cut to this. |

Pass it to `session.ConfigureAutomaticRetry(policy)`. `AbortRetryAsync()` cancels a retry that is waiting. `AgentRetryPolicy.IsRetryableError(stopReason, message)` tells you whether an error counts.

## Skills

A skill is a `SKILL.md` file in its own folder, with a `description` in its frontmatter. `SkillDiscovery.LoadAsync` finds skills in the folders you grant and reads their frontmatter with the decoder you pass.

```csharp
var skills = await SkillDiscovery.LoadAsync(
    new SkillDiscoveryRequest(
        UserAgentDirectory: new SkillRootGrant(agentFolder, Trusted: true),
        ProjectWorkingDirectory: new SkillRootGrant(root, Trusted: projectTrusted),
        ExplicitPaths: []),
    SkillYamlDecoder.Decode);

foreach (var diagnostic in skills.Diagnostics)
    Console.Error.WriteLine($"{diagnostic.Code}: {diagnostic.Path}");

// The <available_skills> block for your system prompt.
var section = skills.FormatForPrompt();
```

With `IncludeDefaults` on (the default), the request searches `<agent folder>/skills` and `<working folder>/.pi/skills`. A grant with `Trusted: false` is skipped with a `DeniedSelection` warning. Add other folders as `ExplicitPaths`, each a `SkillPathSelection` with an absolute path.

:::note
The CLI also searches `~/.agents/skills` and the `.agents/skills` folder in the working folder and each parent. The library doesn't. Add those folders to `ExplicitPaths` if you want the same set.
:::

`SkillResourceSet` gives you:

| Member | Does |
| --- | --- |
| `Skills` | The loaded skills, each with `Name`, `Description`, `FilePath` and `BaseDir`. |
| `Diagnostics` | Problems found while loading, such as `DescriptionRequired` for a `SKILL.md` without a description. Each has a `Type`, `Code` and `Path`. |
| `FormatForPrompt(fileReadTool)` | The `<available_skills>` block. `fileReadTool` is `"read"` (the default) or `"bash"`, the tool the model uses to open a skill. Skills with `disable-model-invocation` are left out. |
| `ExpandAsync(text)` | Expands `/skill:name args` into the skill's content. |

`SkillResourceOptions` limits how much is read: 128 files, 4096 directory entries, a depth of 16, 64 KB per file and 1 MB in all, by default.

## Prompt templates

A prompt template is a Markdown file. Its name is the file name, and `/name args` expands it. `PromptTemplateResourceSet.LoadAsync` reads templates from the files and folders you select.

```csharp
var prompts = Path.Combine(agentFolder, "prompts");
var templates = await PromptTemplateResourceSet.LoadAsync(
[
    new PromptTemplatePathSelection(prompts, new PromptTemplateSourceInfo(
        prompts, "auto", PromptTemplateSourceScope.User, PromptTemplateSourceOrigin.TopLevel)),
], PromptTemplateYamlDecoder.Decode);

// Expand templates, then skills, then send.
var admission = templates.CreateInputAdmission(PromptTemplateInputOperation.Prompt, new SkillInputAdmission(skills));
var submitted = await session.SubmitInputAsync(new PromptInput("/review src/Program.cs"), admission);
Console.WriteLine(submitted.Disposition);
```

A folder selection loads its `.md` files. `PromptTemplateSourceInfo` records where a template came from, for listings. It grants nothing. The CLI loads `<agent folder>/prompts` and, for a trusted project, `<working folder>/.pi/prompts`.

Inside a template, `$1`, `$2` and so on are the arguments, and `$@` or `$ARGUMENTS` all of them. `${1:-text}` gives a default, and `${@:2}` or `${@:2:3}` a slice. Templates that fail to load show up in `Catalog.Diagnostics` as warnings.

`SubmitInputAsync` returns a `SubmittedInputResult`. Its `Disposition` is `Handled` (a handler dealt with the input), `Queued` (it waits for the current run) or `Started` (it started a run, which is in `Run`). `PromptTemplateInputOperation` also has `Steer`, `FollowUp` and `ExtensionMessage`, for admissions that handle those kinds of input.

To expand text without a session, call `PromptTemplateExpander.ExpandPromptTemplate(text, templates)` with a list of `PromptTemplate` values.

## YAML frontmatter

Templates and skills start with an optional YAML block between `---` lines. `PiSharp.CodingAgent` finds the block but doesn't parse YAML; you pass a decoder. `PiSharp.PromptTemplates.Yaml` has the two the CLI uses.

| Decoder | Returns | Reads |
| --- | --- | --- |
| `PromptTemplateYamlDecoder.Decode` | `PromptTemplateMetadata` | `description`, `argument-hint` |
| `SkillYamlDecoder.Decode` | `SkillMetadata` | `name`, `description`, `disable-model-invocation` |

Both take a single YAML document and reject duplicate keys and cyclic aliases. They cap alias expansion at 100, so a small block can't expand into a huge one. A decoder is a plain `Func<string, ...>`, so you can write your own over another YAML library.

Without the package, pass `PromptTemplateFrontmatterPolicy.RejectNonemptyYaml`. Templates without frontmatter load as usual. A template with frontmatter fails to load and shows up as a warning.

A template file such as `review.md` looks like this:

```text
---
description: Review a file for bugs
argument-hint: <file>
---
Review $1 for bugs and explain each one.
```

## Built-in tools

`BuiltinToolCatalog` builds the built-in tools for one working folder and home folder. Relative paths resolve against the working folder, and `~` against the home folder. The tools have Pi v1.2.0's names, parameters and output; see [Built-in tools](/docs/reference/tools/) for each one.

```csharp
new BuiltinToolCatalog(workingDirectory, homeDirectory, operations, readWriteOptions, editOptions,
    bash, find, grep, editAccessProbe, grepContextReader, piGrep, piFind, pi);
```

Everything after the two folders is optional.

| Tool | Class | Needs |
| --- | --- | --- |
| `read`, `write` | `ReadWriteTools` | Nothing. |
| `edit` | `EditTool` | Nothing. |
| `ls` | `LsTool` | Nothing. |
| `bash` | `BashTool` | A `BashTool` you build, passed as `bash`. |
| `grep` | `GrepTool` | An `IGrepExecutor`, such as `RipgrepExecutor`, passed as `grep`. |
| `find` | `FindTool` | An `IFindExecutor`, such as `FdFindExecutor`, passed as `find`. |

`Registered` lists the tools in this order: `read`, `bash`, `edit`, `write`, `grep`, `find`, `ls`, skipping any you didn't supply. `Select(names)` picks some by name, in your order, and throws for an unknown name. `write` and `edit` share one queue, so two changes to the same file never overlap.

`pi: true` lets `ls` list any path, as Pi does. `piGrep` and `piFind` take Pi-style `PiGrepTool` and `PiFindTool` tools in place of `grep` and `find`.

### Tool policy

Each tool prepares its action and asks your `IToolActionPolicy` before it acts. The policy sees a `PreparedToolAction`:

| Field | For file tools | For `bash` |
| --- | --- | --- |
| `Kind` | `Path` | `Command` |
| `ToolName`, `Operation` | The tool name. | `bash` |
| `Target` | The canonical absolute path. | The shell executable. |
| `CommandArguments` | Empty. | The shell arguments, ending with the command. |
| `WorkingDirectory` | | The folder the command runs in. |
| `Environment` | | The command's environment. |
| `Arguments` | The tool call's arguments. | The tool call's arguments. |

Return `new ToolActionAuthorization(Allow: false)` to refuse. The tool then returns a `Blocked` error, `Final tool action was denied.`, and the model sees it. `WorkspacePolicy` in [the first sample](#open-a-session-in-a-folder) allows paths inside the working folder and refuses every command.

:::caution[A command can do anything]
Once your policy allows a `bash` command, the command runs with your process's rights. Checking its working folder does not keep it inside that folder. Allow commands only when you would run them yourself.
:::

### File tool options

`ReadWriteToolOptions` sets limits for `read` and `write`:

| Option | Default | Meaning |
| --- | --- | --- |
| `MaximumReadBytes` | 64 MiB | Largest file `read` opens. |
| `MaximumWriteBytes` | 64 MiB | Largest content `write` writes. |
| `MaximumArgumentCharacters` | 8 MiB | Longest tool call arguments. |
| `MaximumPathCharacters` | 4096 | Longest path. |
| `AutoResizeImages` (`init`) | `true` | Resize large images, like `images.autoResize`. |
| `ImageResizeOptions` (`init`) | `null` | Size limits for images. See [Images](#images). |
| `ImageCodec` (`init`) | `null` | The image codec. `null` uses `BuiltinImageCodec`. |
| `CurrentModelSupportsImages` (`init`) | `null` | Returns `false` when the model can't see images, so `read` adds a note instead. |

`EditToolOptions` sets the same input, output, argument and path limits for `edit`, plus `MaximumEdits` (no limit by default).

### bash

`BashTool` runs commands through an `IProcessRunner` and a shell. `ShellDiscovery.Resolve()` finds the shell as Pi does: Git Bash or `bash.exe` on Windows, and `/bin/bash`, `bash` or `sh` elsewhere. It throws `ShellDiscoveryException` when there is none.

```csharp
var shell = ShellDiscovery.Resolve();
var spill = Path.Combine(Path.GetTempPath(), "my-agent-bash");
Directory.CreateDirectory(spill);

// Your process environment, with <agent folder>/bin added to PATH, as Pi does.
var inherited = Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
    .ToDictionary(entry => (string)entry.Key, entry => entry.Value as string ?? "");
var environment = ShellDiscovery.ShellEnvironment(inherited, ShellDiscovery.BinDirectory(), OperatingSystem.IsWindows());

IProcessRunner runner = OperatingSystem.IsWindows()
    ? new NativeProcessRunner()
    : new UnixProcessRunner(new PosixSpawnProcessAdmission());
var bash = new BashTool(runner, BashToolOptions.FromShell(shell, root, environment, spill));

var catalog = new BuiltinToolCatalog(root, home, bash: bash);
```

Long output spills to a file in the spill folder, and the model gets the tail with the file's path. Your policy must allow `Command` actions, or every command is refused.

`BashToolOptions(Executable, WorkingDirectory, Environment, SpillDirectory, MaximumCommandCharacters, MaximumArgumentCharacters)` takes these `init` properties too:

| Property | Default | Meaning |
| --- | --- | --- |
| `ShellArguments` | `["-c"]` | Arguments before the command. `FromShell` sets them. |
| `CommandTransport` | `Argv` | How the command reaches the shell. `FromShell` sets it. |
| `CommandPrefix` | `null` | Text run before every command, like the `shellCommandPrefix` setting. |
| `ExposeSessionEnvironment` | `true` | Set the `PI_SESSION_ID`, `PI_SESSION_FILE`, `PI_PROVIDER`, `PI_MODEL` and `PI_REASONING_LEVEL` variables. |
| `SessionEnvironment` | `null` | Returns the `BashSessionEnvironment` with those values. |
| `SpawnHook` | `null` | Changes the command, folder or environment just before it runs. |

`ProcessRunnerOptions.MaximumRawBytes` defaults to 64 MiB. The CLI passes `new ProcessRunnerOptions(MaximumRawBytes: int.MaxValue)` to its runner, because Pi spills any amount of output to the file.

### grep and find

`GrepTool` runs [ripgrep](https://github.com/BurntSushi/ripgrep) through an `IGrepExecutor`, and `FindTool` runs [fd](https://github.com/sharkdp/fd) through an `IFindExecutor`. `RipgrepExecutor` and `FdFindExecutor` are the built-in executors. The library doesn't find or download `rg` and `fd`; the CLI does that (see [Shell, ripgrep and fd](/docs/reference/configuration/#shell-ripgrep-and-fd)).

### Your own files and processes

The tools reach the disk only through `IFileOperations` and processes only through `IProcessRunner`. Supply your own to run the tools against a container, a remote machine or an in-memory test folder.

| Interface | Members |
| --- | --- |
| `IFileOperations` | `ExistsAsync`, `CanonicalizeAsync`, `ReadAsync(path, maximumBytes)`, `CreateDirectoryAsync`, `WriteAsync`. |
| `IDirectoryFileOperations` | Adds `IsDirectoryAsync` and `ReadDirectoryAsync`, for `ls`. The catalog takes this one. |
| `IProcessRunner` | `RunAsync(request, onUpdate)`, which returns a `ProcessRunResult`. |

`LocalFileOperations` is the default. Its `CanonicalizeAsync` resolves symbolic links and junctions, so your policy sees real paths.

## Images

`read` returns images as well as text. It recognizes PNG, JPEG, GIF, WebP and BMP files by their content. An image over the size limits is resized before it goes to the model.

The codec decides what can be resized:

| Codec | Package | Reads sizes of | Decodes and resizes |
| --- | --- | --- | --- |
| `BuiltinImageCodec` | `PiSharp.Tools` | PNG, JPEG, GIF, WebP, BMP | PNG and BMP only |
| `SkiaImageCodec` | `PiSharp.Tools.Skia` | PNG, JPEG, GIF, WebP, BMP | All five. It applies EXIF orientation, uses the first GIF frame and writes PNG or JPEG. |

With `BuiltinImageCodec`, an oversized JPEG, GIF or WebP can't be resized. The CLI uses `SkiaImageCodec`; do the same to match it:

```csharp
var catalog = new BuiltinToolCatalog(root, home,
    readWriteOptions: new ReadWriteToolOptions { ImageCodec = SkiaImageCodec.Instance });
```

`ImageResizeOptions` sets the limits:

| Option | Default |
| --- | --- |
| `MaxWidth` | 2000 |
| `MaxHeight` | 2000 |
| `MaxBytes` | 4,718,592 (4.5 MB) |
| `JpegQuality` | 80 |

See [Images](/docs/reference/tools/#images) for the steps `read` follows. To keep every image away from the provider, set `BlockImages` on `SessionRuntimeRegistryOptions`.

## Tool selection

`AllowedToolSelection.Create` builds a selection from the same grammar as `--tools`, with names, `*` patterns and `+name` or `-name` changes to the defaults. Its parameters follow the CLI's inputs: `tools` for `--tools`, `excluded` for `--exclude-tools`, `noTools` for `--no-tools` (`NoToolsMode.All`) or `--no-builtin-tools` (`NoToolsMode.Builtin`), and `configuredDefaults` for the `defaultTools` setting. Give the selection to `LifetimeToolSelection` on the session options or the registry options to limit which tools a session may ever activate. See [Selecting tools](/docs/reference/tools/#selecting-tools).

## Compaction

When the context gets close to the model's window, a session can summarize older messages. Turn it on with a summary generator:

```csharp
session.ConfigureAutomaticCompaction(
    new TransportSessionSummaryGenerator(_ => provider),
    new SessionCompactionSettings(),
    contextWindow: 200_000);
```

`TransportSessionSummaryGenerator` asks a model for the summary through the transport you return for each request. `SessionCompactionSettings` has `Enabled` (`true`), `ReserveTokens` (16,384) and `KeepRecentTokens` (20,000). `CompactAsync` compacts on demand. See [Plan a compaction](/docs/embed/sessions/#plan-a-compaction) for how the cut is chosen.

## Export and share

`AgentSessionExport` exports a session the way `/export` does.

```csharp
var host = SessionHtmlExportHost.CreateDefault();
var htmlPath = await AgentSessionExport.ExportToHtmlAsync(session, host);
var jsonlPath = AgentSessionExport.ExportToJsonl(session, Path.Combine(root, "branch.jsonl"));
```

| Method | Does |
| --- | --- |
| `ExportToHtmlAsync(session, host, outputPath, themeName)` | Writes a self-contained HTML page and returns its path. The default name is `pi-session-<file name>.html`. |
| `ExportToJsonl(session, outputPath)` | Writes the current branch as a new session file with a fresh header, and returns its path. |
| `SessionHtmlExport.ExportFromFileAsync(inputPath)` | Exports a session file without opening a session, like `--export`. |

A relative HTML output path resolves against the host's `WorkingDirectory`, or the process's current folder when that is `null`. Exporting a session whose file isn't written yet throws `Nothing to export yet - start a conversation first`.

`SessionShare` does what `/share` does:

```csharp
await new SessionShare().ShareSessionAsync(
    AgentSessionExport.ShareSession(session, host),
    new SessionShareUi(ShowStatus: Console.WriteLine, ShowError: Console.Error.WriteLine));
```

When you pass a signed-in Radius account as the `radius` argument of `ShareSession`, it uploads the session there. Otherwise it needs the GitHub CLI, logged in with `gh auth login`, and creates a secret gist of the HTML page. Status and errors go to your `SessionShareUi`.

:::caution[Sharing uploads the session]
A share sends the whole conversation, including file contents the tools read, to Radius or GitHub. Anyone with the link can open a secret gist.
:::

## Compared with Pi

- Pi's coding agent ([`packages/coding-agent`](https://github.com/earendil-works/pi/tree/v1.2.0/packages/coding-agent)) finds the agent folder, context files, settings, skills and credentials itself. PiSharp's library takes absolute paths and decoders, and the CLI does the finding.
- Pi's tools act directly. PiSharp's tools prepare each action and ask your policy first.
- Pi resizes images with Photon. PiSharp's default codec resizes only PNG and BMP; `PiSharp.Tools.Skia` handles the rest.
- YAML decoding is a separate package, so you can leave YAML out.
- Session files use Pi's version 3 format. See [How it differs from Pi](/docs/embed/sessions/#how-it-differs-from-pi) for the details.
