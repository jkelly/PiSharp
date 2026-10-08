using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Extensions;
using PiSharp.Cli.Prompts;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Runtime;
using PiSharp.Extensions.Facade.Context;
using PiSharp.Extensions.Runtime.Facade.Context;
using PiSharp.Sessions.Serialization;

// Source-authored only. No shared runner registration or qualification credit.
internal static class OriginalSystemPromptConstructionTests
{
    private sealed record Raw(string Phase, Task Original, AggregateException? Aggregate, Exception? Direct);
    private static readonly object gate = new(); private static readonly List<Raw> rows = [];
    internal static (string Phase, Task Original, AggregateException? Aggregate, Exception? Direct)[] CapturedOriginals
    { get { lock (gate) return rows.Select(row => (row.Phase, row.Original, row.Aggregate, row.Direct)).ToArray(); } }
    internal static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("original prompt admitted constructor inputs are frozen and render original ordered sections", Builder),
        ("original prompt actual profile construction and acknowledged selection drive getter and request", Profile),
        ("original prompt actual provider install refresh and retirement retain decorator and provider originals", Catalog),
        ("original prompt prior callback distinguishes genuine cancellation from faulted and synchronous OCE", HookClasses),
        ("original forced prompt projects after prior hook and preserves its final inert tool state", ForcedOrder)
    ];
    private static void Check(bool value) { if (!value) throw new IOException("Original prompt control failed."); }
    private static OriginalSystemPromptAdmission Input(string custom = "admitted custom") => new()
    {
        CustomPrompt = custom, AppendSystemPrompt = "admitted append",
        ToolSnippets = [KeyValuePair.Create("read", "Read supplied files")],
        ToolGuidelines = [KeyValuePair.Create("read", ImmutableArray.Create(" keep ", "keep"))],
        PromptGuidelines = ["keep", "original caller guideline"],
        Sections = [KeyValuePair.Create("addendum", "override append"), KeyValuePair.Create("extra", "extra text")],
        ContextFiles = [new("admitted instructions", "context content")],
        Skills = [new("<skill>", "&description", "admitted/skill.md", "admitted", JsonData.Parse("{\"source\":\"supplied\"}"), false),
            new("disabled", "hidden", "not read", "not read", JsonData.EmptyObject, true)],
        Documentation = new("admitted/readme", "admitted/docs", "admitted/examples")
    };
    private static Task Builder()
    {
        var storage = new[] { "keep", "original caller guideline" };
        var input = Input("") with { PromptGuidelines = ImmutableCollectionsMarshal.AsImmutableArray(storage) };
        var snapshot = OriginalSystemPromptBuilder.Capture(input, Path.GetFullPath(Path.GetTempPath()), ["read"]);
        storage[0] = "mutated after admission";
        Check(snapshot.Input.PromptGuidelines[0] == "keep");
        Check(!ReferenceEquals(snapshot.Input.ContextFiles[0], input.ContextFiles[0]));
        var sections = OriginalSystemPromptBuilder.Sections(snapshot);
        Check(sections.Select(row => row.Key).SequenceEqual(new[] { "preamble", "tools", "rules", "docs", "addendum", "project_context", "skills", "cwd", "extra" }));
        Check(sections.Single(row => row.Key == "addendum").Value == "<addendum>\noverride append\n</addendum>");
        var rules = sections.Single(row => row.Key == "rules").Value;
        Check(rules.Split("- keep", StringSplitOptions.None).Length == 2 && !rules.Contains("mutated", StringComparison.Ordinal));
        var skills = sections.Single(row => row.Key == "skills").Value;
        Check(skills.Contains("&lt;skill&gt;", StringComparison.Ordinal) && skills.Contains("&amp;description", StringComparison.Ordinal) && !skills.Contains("disabled", StringComparison.Ordinal));
        Check(snapshot.Options.Value.GetProperty("contextFiles")[0].GetProperty("content").GetString() == "context content");
        Check(!snapshot.Options.Value.TryGetProperty("forceSystemPrompt", out _));
        var forced = OriginalSystemPromptBuilder.Capture(Input() with { ForceSystemPrompt = "" }, snapshot.Cwd, ["bash"]);
        Check(forced.Options.Value.GetProperty("forceSystemPrompt").GetString() == "" &&
            OriginalSystemPromptBuilder.Message(forced, 0).Value.GetProperty("content").GetString() == "");
        Check(OriginalSystemPromptBuilder.Message(forced, 0, false).Value.GetProperty("sections").GetProperty("preamble").GetString() == "admitted custom");
        return Task.CompletedTask;
    }
    private static async Task Profile()
    {
        var root = Fresh(); OfflineSessionProfile? profile = null; ExtensionRegistry? readRegistry = null; ExtensionProviderRegistrationHost? actionProviders = null; var errors = new List<Exception>();
        try
        {
            profile = await Own("profile:create", OfflineSessionProfile.CreateAsync(root, Path.Combine(root, "session.jsonl"), null, [], [], [], default, originalSystemPrompt: Input()));
            Check(profile.InitialSystem.Value.GetProperty("sections").GetProperty("preamble").GetString() == "admitted custom");
            var id = 0; var life = profile.CreateLifecycle(() => 1, () => "prompt-" + ++id);
            var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new { type = "session", version = 3, id = "prompt", timestamp = "2026-10-07T00:00:00.000Z", cwd = root }));
            var session = await Own("profile:session-create", life.CreateAsync(Path.Combine(root, "session.jsonl"), header, profile.SelectedModel));
            await Join("profile:attach", profile.AttachOwnerAsync(session, lifecycle: life));
            var attached = profile.Sessions!.Current;
            Check(profile.CaptureOriginalSystemPrompt(attached).Input.CustomPrompt == "admitted custom");
            var actualProfile = profile;
            var snapshots = new NativeSessionSnapshotProvider(); snapshots.Attach(profile.Sessions);
            var readHost = new NativeExtensionContextFacadeHost();
            readHost.ConfigureOriginalSystemPromptReads(attachment => actualProfile.CaptureOriginalSystemPrompt(attachment).Options);
            readHost.Attach(profile.Sessions);
            readRegistry = new(null, null, snapshots, new NativeExtensionRegistrationFacadeHost(readHost, new NoActions()));
            actionProviders = new(readRegistry, [], new Configuration());
            IExtensionSystemPromptOptionsReadFacade? saved = null; IExtensionSystemPromptOptionsReadFacade? savedActions = null; var reads = 0;
            await Own("profile:actual-getter-activation", readRegistry.ActivateAsync("prompt-reader", new Extension(registry =>
            {
                registry.RegisterCommand(ExtensionCommandFacade.CreateCommand("prompt", "prompt-options", "Actual prompt options", (_, view, _) =>
                {
                    saved = (IExtensionSystemPromptOptionsReadFacade)view;
                    Check(saved.GetSystemPromptOptions().Value.GetProperty("customPrompt").GetString() == "admitted custom");
                    reads++; return ValueTask.CompletedTask;
                }));
                registry.RegisterCommand(ExtensionRegistrationCommands.Create("prompt-actions", "prompt-options-actions", "Actual registration prompt options", actionProviders!, (_, actions, _, _) =>
                {
                    savedActions = (IExtensionSystemPromptOptionsReadFacade)actions;
                    Check(savedActions.GetSystemPromptOptions().Value.GetProperty("customPrompt").GetString() == "admitted custom");
                    reads++; return ValueTask.CompletedTask;
                }));
                return ValueTask.CompletedTask;
            })));
            await Join("profile:actual-getter-command", readRegistry.InvokeCommandAsync(readRegistry.CaptureSnapshot(), "prompt-options", JsonData.EmptyObject).AsTask());
            await Join("profile:actual-registration-getter-command", readRegistry.InvokeCommandAsync(readRegistry.CaptureSnapshot(), "prompt-options-actions", JsonData.EmptyObject).AsTask());
            Check(reads == 2 && saved is not null && savedActions is not null);
            var refused = false; try { saved!.GetSystemPromptOptions(); } catch (InvalidOperationException) { refused = true; }
            Check(refused);
            refused = false; try { savedActions!.GetSystemPromptOptions(); } catch (InvalidOperationException) { refused = true; }
            Check(refused);
            var selection = session.SetActiveToolsAsync([]); await Join("profile:actual-tool-selection", selection);
            Check(profile.CaptureOriginalSystemPrompt(attached).SelectedTools.IsEmpty);
            var binding = profile.Registry.CaptureModelCatalog().Bindings.Single(row => row.Model == profile.SelectedModel);
            Check(ReferenceEquals(binding, profile.DecorateOriginalPromptBinding(binding)));
            var projected = binding.Hooks!.TransformRequestMessages!([new("system", profile.InitialSystem)], default).AsTask();
            var result = await Own("profile:operative-request-projection", projected);
            Check(result[0].WireBody.Value.GetProperty("sections").GetProperty("preamble").GetString() == "admitted custom");
            Check(profile.InitialSystem.Value.GetProperty("sections").GetProperty("preamble").GetString() == "admitted custom");
        }
        catch (Exception error) { errors.Add(error); }
        finally { try { actionProviders?.Dispose(); } catch (Exception error) { errors.Add(error); } if (readRegistry is not null) { try { await Join("profile:getter-registry-close", readRegistry.DisposeAsync().AsTask()); } catch (Exception error) { errors.Add(error); } } if (profile is not null) { try { await Join("profile:close", profile.DisposeAsync().AsTask()); } catch (Exception error) { errors.Add(error); } } Clean(root, errors); }
        if (errors.Count != 0) throw new FixtureFailure(CapturedOriginals, errors.ToArray());
    }
    private static async Task Catalog()
    {
        var root = Fresh(); OfflineSessionProfile? profile = null; ExtensionRegistry? native = null;
        NativeExtensionRegistrationBridge? bridge = null; RegistrationScope? scope = null; var errors = new List<Exception>();
        var release = new TaskCompletionSource<ImmutableArray<TranscriptEntry>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var expected = new IOException("actual admitted prior transform failure"); Task<ImmutableArray<TranscriptEntry>>? prior = null; Task<ImmutableArray<TranscriptEntry>>? transformed = null;
        try
        {
            profile = await Own("catalog:profile-create", OfflineSessionProfile.CreateAsync(root, Path.Combine(root, "session.jsonl"), null, [], [], [], default, originalSystemPrompt: Input()));
            var basis = profile.Registry.CaptureModelCatalog().Bindings[0];
            var originalHooks = new AgentHooks { TransformRequestMessages = (_, _) => { prior = release.Task; return new(release.Task); } };
            var undecorated = basis with { Hooks = originalHooks };
            var decorated = profile.DecorateOriginalPromptBinding(undecorated);
            Check(ReferenceEquals(decorated.Transport, undecorated.Transport) && ReferenceEquals(decorated.ToolHooks, undecorated.ToolHooks));
            Check(ReferenceEquals(decorated, profile.DecorateOriginalPromptBinding(undecorated)) && ReferenceEquals(decorated, profile.DecorateOriginalPromptBinding(decorated)));
            native = new(); bridge = new(native, new ExtensionHostFlagValues(new Dictionary<string, ExtensionFlagValue>()), [], new Configuration());
            var runtime = new SessionRuntimeRegistry([decorated], [], new Deny());
            bridge.ConfigureModelCatalog(runtime, runtime.CaptureModelCatalog().Bindings, profile.DecorateOriginalPromptBinding);
            NativeExtensionRegistrationFacade? facade = null;
            var activated = await Own("catalog:actual-provider-activation", bridge.ActivateOwnerAsync("prompt-provider", new Extension(_ =>
            { facade!.RegisterProvider(Definition()); return ValueTask.CompletedTask; }), value => facade = value));
            scope = activated.Scope;
            var current = runtime.CaptureModelCatalog();
            Check(current.Bindings.Length == 2 && current.Bindings.All(value => ReferenceEquals(value, profile.DecorateOriginalPromptBinding(value))));
            bridge.RefreshModelCatalog();
            Check(runtime.CaptureModelCatalog().Bindings.All(value => ReferenceEquals(value, profile.DecorateOriginalPromptBinding(value))));
            transformed = decorated.Hooks!.TransformRequestMessages!([], default).AsTask();
            Check(ReferenceEquals(prior, release.Task) && !transformed.IsCompleted);
            release.TrySetException(expected);
            Exception? observed = null; try { await Join("catalog:decorated-prior-hook", transformed); } catch (Exception error) { observed = error; }
            var failure = FindHook(observed);
            Check(failure is not null && ReferenceEquals(failure.Original, release.Task) && ReferenceEquals(failure.Direct, expected) &&
                failure.Aggregate is not null && ReferenceEquals(failure.Aggregate.InnerExceptions.Single(), expected) && OnlyHook(observed!, failure));
            var acknowledged = failure ?? throw new IOException("Missing actual prior hook fault.");
            Check(!OnlyHook(new AggregateException(acknowledged, new IOException("foreign hook sibling")), acknowledged));
            Check(!OnlyHook(new IOException("unknown hook wrapper", acknowledged), acknowledged));
            Check(!OnlyHook(new AggregateException(), acknowledged));
            Record("catalog:actual-prior-hook", acknowledged.Original!, acknowledged.Direct, acknowledged.Aggregate);
            Check(acknowledged.Original is { IsFaulted: true, IsCanceled: false });
            await Join("catalog:actual-retire-publication", bridge.RetireOwnerAsync(scope)); scope = null;
            Check(runtime.CaptureModelCatalog().Bindings.Length == 1 && ReferenceEquals(runtime.CaptureModelCatalog().Bindings[0], decorated));
        }
        catch (Exception error) { errors.Add(error); }
        finally
        {
            release.TrySetException(expected);
            try { await Join("catalog:prior-final-join", release.Task); } catch (Exception error) { if (!ReferenceEquals(error, expected)) errors.Add(error); }
            if (transformed is not null)
            {
                try { await Join("catalog:decorator-final-join", transformed); }
                catch (Exception error)
                {
                    var retained = FindHook(error);
                    if (retained is null || !ReferenceEquals(retained.Original, release.Task) || !ReferenceEquals(retained.Direct, expected) ||
                        retained.Aggregate is null || retained.Aggregate.InnerExceptions.Count != 1 || !ReferenceEquals(retained.Aggregate.InnerExceptions[0], expected) || !OnlyHook(error, retained)) errors.Add(error);
                }
            }
            if (scope is not null && bridge is not null) { try { await Join("catalog:retire-final", bridge.RetireOwnerAsync(scope)); } catch (Exception error) { errors.Add(error); } }
            try { bridge?.Dispose(); } catch (Exception error) { errors.Add(error); }
            if (native is not null) { try { await Join("catalog:registry-close", native.DisposeAsync().AsTask()); } catch (Exception error) { errors.Add(error); } }
            if (profile is not null) { try { await Join("catalog:profile-close", profile.DisposeAsync().AsTask()); } catch (Exception error) { errors.Add(error); } }
            Clean(root, errors);
        }
        if (errors.Count != 0) throw new FixtureFailure(CapturedOriginals, errors.ToArray());
    }
    private static async Task HookClasses()
    {
        var root = Fresh(); OfflineSessionProfile? profile = null; var errors = new List<Exception>();
        try
        {
            // The decorator is also installed on the preserved literal baseline.
            profile = await Own("hook-classes:literal-profile", OfflineSessionProfile.CreateAsync(root, Path.Combine(root, "session.jsonl"), null, [], [], [], default));
            var basis = profile.Registry.CaptureModelCatalog().Bindings[0];
            foreach (var mode in new[] { "genuinely-canceled", "faulted-oce", "synchronous-oce" })
            {
                using var tokenOwner = new CancellationTokenSource();
                var callback = new TaskCompletionSource<ImmutableArray<TranscriptEntry>>(TaskCreationOptions.RunContinuationsAsynchronously);
                var injected = new OperationCanceledException("supplied callback OCE", tokenOwner.Token);
                Task<ImmutableArray<TranscriptEntry>>? wrapper = null; Exception? direct = null;
                try
                {
                    var binding = profile.DecorateOriginalPromptBinding(basis with { Hooks = new AgentHooks
                    { TransformRequestMessages = (_, _) => mode == "synchronous-oce" ? throw injected : new(callback.Task) } });
                    wrapper = binding.Hooks!.TransformRequestMessages!([], tokenOwner.Token).AsTask();
                    if (mode != "synchronous-oce")
                    {
                        Check(!wrapper.IsCompleted); tokenOwner.Cancel();
                        if (mode == "genuinely-canceled") callback.TrySetCanceled(tokenOwner.Token); else callback.TrySetException(injected);
                    }
                    try { await Join("hook-classes:" + mode + ":wrapper", wrapper); } catch (Exception error) { direct = error; }
                    if (mode == "genuinely-canceled")
                    {
                        Check(wrapper.IsCanceled && !wrapper.IsFaulted && direct is OfflineSessionProfile.OriginalPromptHookCancellation);
                        var canceled = (OfflineSessionProfile.OriginalPromptHookCancellation)direct!;
                        Check(ReferenceEquals(canceled.Original, callback.Task) && canceled.Original.IsCanceled && canceled.Aggregate is null &&
                            canceled.CancellationToken == tokenOwner.Token && canceled.Direct is OperationCanceledException originalCancel && originalCancel.CancellationToken == tokenOwner.Token);
                        Record("hook-classes:canceled-original", canceled.Original, canceled.Direct);
                    }
                    else
                    {
                        var failure = FindHook(direct); Check(wrapper.IsFaulted && !wrapper.IsCanceled && failure is not null && OnlyHook(direct!, failure));
                        Check(ReferenceEquals(failure!.Direct, injected));
                        if (mode == "synchronous-oce") Check(failure.Original is null && failure.Aggregate is null);
                        else
                        {
                            Check(ReferenceEquals(failure.Original, callback.Task) && callback.Task.IsFaulted && !callback.Task.IsCanceled &&
                                failure.Aggregate is { InnerExceptions.Count: 1 } && ReferenceEquals(failure.Aggregate.InnerExceptions[0], injected));
                            Record("hook-classes:faulted-original", callback.Task, failure.Direct, failure.Aggregate);
                        }
                    }
                }
                finally
                {
                    // Only a callback Task actually returned by the factory is an original.
                    if (mode != "synchronous-oce")
                    {
                        if (!callback.Task.IsCompleted) callback.TrySetException(injected);
                        try { await Join("hook-classes:" + mode + ":original-final", callback.Task); }
                        catch (Exception error)
                        {
                            if (mode == "genuinely-canceled") { if (!callback.Task.IsCanceled || error is not OperationCanceledException oce || oce.CancellationToken != tokenOwner.Token) errors.Add(error); }
                            else if (!ReferenceEquals(error, injected)) errors.Add(error);
                        }
                    }
                    if (wrapper is not null)
                    {
                        try { await Join("hook-classes:" + mode + ":wrapper-final", wrapper); }
                        catch (Exception error)
                        {
                            if (mode == "genuinely-canceled")
                            {
                                if (error is not OfflineSessionProfile.OriginalPromptHookCancellation canceled || !wrapper.IsCanceled ||
                                    !ReferenceEquals(canceled.Original, callback.Task) || canceled.CancellationToken != tokenOwner.Token) errors.Add(error);
                            }
                            else
                            {
                                var retained = FindHook(error);
                                if (retained is null || !OnlyHook(error, retained) || !ReferenceEquals(retained.Direct, injected) ||
                                    (mode == "synchronous-oce" ? retained.Original is not null || retained.Aggregate is not null : !ReferenceEquals(retained.Original, callback.Task) ||
                                        retained.Aggregate is null || retained.Aggregate.InnerExceptions.Count != 1 || !ReferenceEquals(retained.Aggregate.InnerExceptions[0], injected))) errors.Add(error);
                            }
                        }
                    }
                }
            }
        }
        catch (Exception error) { errors.Add(error); }
        finally { if (profile is not null) { try { await Join("hook-classes:profile-close", profile.DisposeAsync().AsTask()); } catch (Exception error) { errors.Add(error); } } Clean(root, errors); }
        if (errors.Count != 0) throw new FixtureFailure(CapturedOriginals, errors.ToArray());
    }
    private static async Task ForcedOrder()
    {
        var root = Fresh(); OfflineSessionProfile? profile = null; var errors = new List<Exception>();
        Task<ImmutableArray<TranscriptEntry>>? actualPrior = null, transformed = null;
        try
        {
            profile = await Own("forced-order:profile-create", OfflineSessionProfile.CreateAsync(root, Path.Combine(root, "session.jsonl"), null, [], [], [], default,
                originalSystemPrompt: Input() with { ForceSystemPrompt = "exact admitted force" }));
            var originalBody = System.Text.Json.Nodes.JsonNode.Parse(profile.InitialSystem.ToString())!.AsObject();
            originalBody["content"] = "previous hook replacement"; originalBody.Remove("sections");
            var declarations = profile.InitialSystem.Value.GetProperty("toolsAdded").EnumerateArray().ToArray();
            Check(declarations.Length > 0);
            originalBody["toolsAdded"] = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(new[] { declarations[0] }));
            var previousOutput = ImmutableArray.Create(new TranscriptEntry("system", JsonData.Parse(originalBody.ToJsonString())));
            actualPrior = Task.FromResult(previousOutput); var sawUnforced = false;
            var basis = profile.Registry.CaptureModelCatalog().Bindings[0];
            var decorated = profile.DecorateOriginalPromptBinding(basis with { Hooks = new AgentHooks { TransformRequestMessages = (messages, _) =>
            { sawUnforced = messages[0].WireBody.Value.GetProperty("sections").GetProperty("preamble").GetString() == "admitted custom"; return new(actualPrior!); } } });
            transformed = decorated.Hooks!.TransformRequestMessages!([new("system", profile.InitialSystem)], default).AsTask();
            var result = await Own("forced-order:actual-decorated-transform", transformed);
            await Own("forced-order:actual-prior-original", actualPrior);
            Check(sawUnforced && result.Length == 1 && result[0].WireBody.Value.GetProperty("content").GetString() == "exact admitted force" &&
                !result[0].WireBody.Value.TryGetProperty("sections", out _) && result[0].WireBody.Value.GetProperty("toolsAdded").GetArrayLength() == 1 &&
                result[0].WireBody.Value.GetProperty("toolsAdded")[0].GetRawText() == declarations[0].GetRawText());
            Check(profile.InitialSystem.Value.GetProperty("sections").GetProperty("preamble").GetString() == "admitted custom");
        }
        catch (Exception error) { errors.Add(error); }
        finally
        {
            if (actualPrior is not null) { try { await Own("forced-order:prior-final-join", actualPrior); } catch (Exception error) { errors.Add(error); } }
            if (transformed is not null) { try { await Own("forced-order:transform-final-join", transformed); } catch (Exception error) { errors.Add(error); } }
            if (profile is not null) { try { await Join("forced-order:profile-close", profile.DisposeAsync().AsTask()); } catch (Exception error) { errors.Add(error); } }
            Clean(root, errors);
        }
        if (errors.Count != 0) throw new FixtureFailure(CapturedOriginals, errors.ToArray());
    }
    private static bool OnlyHook(Exception root, OfflineSessionProfile.OriginalPromptHookFailure expected)
    {
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance); var found = false; var edges = 0;
        bool Visit(Exception value)
        {
            if (!seen.Add(value)) return true; if (seen.Count > 1024) return false;
            if (ReferenceEquals(value, expected)) { found = true; return true; }
            if (value is AggregateException all)
            { edges += all.InnerExceptions.Count; return edges <= 4096 && all.InnerExceptions.Count != 0 && all.InnerExceptions.All(Visit); }
            return false;
        }
        return Visit(root) && found;
    }
    private static OfflineSessionProfile.OriginalPromptHookFailure? FindHook(Exception? error)
    {
        if (error is OfflineSessionProfile.OriginalPromptHookFailure found) return found;
        if (error is AggregateException all) { foreach (var child in all.InnerExceptions) { var foundChild = FindHook(child); if (foundChild is not null) return foundChild; } }
        return error?.InnerException is { } inner ? FindHook(inner) : null;
    }
    private static readonly ModelDescriptor Late = new("prompt-late", "prompt-api", "prompt-provider");
    private sealed class Configuration : IExtensionProviderConfigurationAdapter
    { public ExtensionProviderDefinition Resolve(string name, JsonData configuration) => Definition(); }
    private static ExtensionProviderDefinition Definition() => new(Late.Provider, [new(Late, JsonData.Parse("{\"id\":\"prompt-late\",\"api\":\"prompt-api\",\"provider\":\"prompt-provider\"}"))], Stream);
    private static async IAsyncEnumerable<StreamEvent> Stream(ExtensionProviderStreamRequest request, IExtensionContext? context, [EnumeratorCancellation] CancellationToken token)
    { token.ThrowIfCancellationRequested(); await Task.CompletedTask; yield break; }
    private sealed class Extension(Func<IExtensionRegistry, ValueTask> initialize) : IPiSharpExtension
    { public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token) => initialize(registry); public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    private sealed class NoActions : IExtensionRegistrationActionHost
    {
        public ValueTask<bool> SetModelAsync(ModelDescriptor model, CancellationToken token) => throw new IOException("No model action admitted.");
        public ValueTask SendMessageAsync(ExtensionCustomMessage message, ExtensionMessageOptions? options, CancellationToken token) => throw new IOException("No message action admitted.");
        public ValueTask SendUserMessageAsync(JsonData content, ExtensionUserMessageOptions? options, CancellationToken token) => throw new IOException("No user action admitted.");
    }
    private sealed class Deny : IToolActionPolicy
    { public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token) => ValueTask.FromResult(new ToolActionAuthorization(false)); }
    private static string Fresh() { var path = Path.Combine(Path.GetTempPath(), "pisharp-original-prompt-control-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private static void Clean(string path, List<Exception> errors)
    {
        try { if (Path.GetDirectoryName(path) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) || !Path.GetFileName(path).StartsWith("pisharp-original-prompt-control-", StringComparison.Ordinal)) throw new IOException("Fixture boundary changed."); Directory.Delete(path, true); }
        catch (Exception error) { errors.Add(error); }
    }
    private static async Task Join(string phase, Task task)
    { Exception? direct = null; try { await task.ConfigureAwait(false); } catch (Exception error) { direct = error; throw; } finally { Record(phase, task, direct); } }
    private static async Task<T> Own<T>(string phase, Task<T> task)
    { Exception? direct = null; try { return await task.ConfigureAwait(false); } catch (Exception error) { direct = error; throw; } finally { Record(phase, task, direct); } }
    private static void Record(string phase, Task task, Exception? direct, AggregateException? supplied = null)
    { lock (gate) { var cached = rows.FirstOrDefault(row => ReferenceEquals(row.Original, task)); rows.Add(new(phase, task, cached is null ? supplied ?? task.Exception : cached.Aggregate, cached is null ? direct : cached.Direct)); } }
    private sealed class FixtureFailure((string Phase, Task Original, AggregateException? Aggregate, Exception? Direct)[] originals, Exception[] failures)
        : IOException("Original prompt controls failed.", new AggregateException(failures))
    { internal (string Phase, Task Original, AggregateException? Aggregate, Exception? Direct)[] Originals { get; } = originals; internal Exception[] Failures { get; } = failures; }
}