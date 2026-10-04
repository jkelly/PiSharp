using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.Contracts;
using PiSharp.Sessions.Context;

namespace PiSharp.CodingAgent;

public sealed record SessionModelBinding(ModelDescriptor Model, IChatTransport Transport,
    IToolHooks? ToolHooks = null, ToolExecutionMode ExecutionMode = ToolExecutionMode.Parallel, AgentHooks? Hooks = null);
public sealed record SessionRegisteredTool(JsonData Declaration, IPreparedToolAdapter Adapter,
    ToolExecutionMode ExecutionMode = ToolExecutionMode.Parallel)
{
    public ToolExposure Exposure { get; init; } = ToolExposure.Direct;
    public ToolNamespace? Namespace { get; init; }
    public bool DefaultActive { get; init; } = true;
    public Func<ToolLoadout, ToolLoadoutChanges?>? PrepareLoadout { get; init; }
}
public sealed record SessionRuntimeRegistryOptions(int MaximumModels = 128, int MaximumTools = 128,
    int MaximumMessages = 1024, int MaximumDeclarations = 4096, int MaximumCharacters = 1_048_576,
    int MaximumJsonDepth = 32, ToolInvokerOptions? ToolInvokerOptions = null)
{
    /// <summary>Borrowed prepared hooks for all active native and extension adapters in the single final-action invoker.</summary>
    public IPreparedToolHooks? PreparedToolHooks { get; init; }
    public ToolInvocationScopeOptions? InvocationScopes { get; init; }
    /// <summary>Bind nested calls to the actual ReplaceableAgentSession attachment before use.</summary>
    public bool BindNestedCallsToSessionOwner { get; init; }
    public Action<string, Exception>? ReportLoadoutDiagnostic { get; init; }
}
public sealed record SessionRuntimeSelection(AgentConfiguration Configuration, ImmutableArray<JsonData> ActiveToolDeclarations)
{
    public ImmutableArray<ToolLoadoutDiagnostic> LoadoutDiagnostics { get; init; } = [];
}
public sealed record SessionRuntimeUpdate(ModelDescriptor? Model = null, string? ThinkingLevel = null,
    TranscriptEntry? SystemMessage = null)
{
    /// <summary>Exact ordered model-active selection, committed at the existing durable idle boundary.</summary>
    public ImmutableArray<string>? ActiveToolNames { get; init; }
}
public enum SessionRuntimeRegistryFailure
{
    InvalidRegistration, InvalidTranscript, UnknownModel, UnknownTool, DeclarationMismatch,
    UnsupportedThinkingLevel, UnsupportedDeclaration, ResourceLimit
}
public sealed class SessionRuntimeRegistryException : Exception
{
    public SessionRuntimeRegistryFailure Failure { get; }
    internal SessionRuntimeRegistryException(SessionRuntimeRegistryFailure failure) : base(failure switch
    {
        SessionRuntimeRegistryFailure.UnknownModel => "Session model has no explicit runtime binding.",
        SessionRuntimeRegistryFailure.UnknownTool => "Session tool has no explicit runtime binding.",
        SessionRuntimeRegistryFailure.DeclarationMismatch => "Session tool declaration does not match its registered binding.",
        SessionRuntimeRegistryFailure.UnsupportedThinkingLevel => "Session thinking level requires unfinished runtime support.",
        SessionRuntimeRegistryFailure.ResourceLimit => "Session runtime binding exceeds configured limits.",
        _ => "Session runtime binding is invalid or requires unfinished support."
    }) => Failure = failure;
}

/// <summary>Explicit borrowed model/adapters and mandatory final-action policy; no executable binding is read from disk.</summary>
public sealed class SessionRuntimeRegistry
{
    private readonly ImmutableDictionary<(string Provider, string Id), SessionModelBinding> _models;
    private readonly ImmutableDictionary<string, SessionRegisteredTool> _tools;
    private readonly ImmutableArray<SessionRegisteredTool> _registeredTools;
    private readonly IToolActionPolicy _policy;
    private readonly SessionRuntimeRegistryOptions _options;

    public SessionRuntimeRegistry(ImmutableArray<SessionModelBinding> models, ImmutableArray<SessionRegisteredTool> tools,
        IToolActionPolicy policy, SessionRuntimeRegistryOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        _options = options ?? new();
        if (_options.MaximumModels <= 0 || _options.MaximumTools <= 0 || _options.MaximumMessages <= 0 ||
            _options.MaximumDeclarations <= 0 || _options.MaximumCharacters <= 0 || _options.MaximumJsonDepth is < 1 or > 64)
            throw new ArgumentOutOfRangeException(nameof(options), "Invalid session runtime binding limits.");
        if (models.IsDefaultOrEmpty || tools.IsDefault) throw Error(SessionRuntimeRegistryFailure.InvalidRegistration);
        if (models.Length > _options.MaximumModels || tools.Length > _options.MaximumTools)
            throw Error(SessionRuntimeRegistryFailure.ResourceLimit);
        var modelIndex = ImmutableDictionary.CreateBuilder<(string, string), SessionModelBinding>();
        long modelCharacters = 0;
        foreach (var binding in models)
        {
            if (binding?.Model is { } bounded &&
                (long)(bounded.Provider?.Length ?? 0) + (bounded.Id?.Length ?? 0) + (bounded.Api?.Length ?? 0) >
                    _options.MaximumCharacters - modelCharacters)
                throw Error(SessionRuntimeRegistryFailure.ResourceLimit);
            if (binding is null || binding.Model is null || binding.Transport is null ||
                !Identity(binding.Model.Id) || !Identity(binding.Model.Provider) || !Identity(binding.Model.Api) ||
                binding.ExecutionMode is not (ToolExecutionMode.Parallel or ToolExecutionMode.Sequential) ||
                !modelIndex.TryAdd((binding.Model.Provider, binding.Model.Id), binding))
                throw Error(SessionRuntimeRegistryFailure.InvalidRegistration);
            modelCharacters += (long)binding.Model.Provider.Length + binding.Model.Id.Length + binding.Model.Api.Length;
            if (modelCharacters > _options.MaximumCharacters) throw Error(SessionRuntimeRegistryFailure.ResourceLimit);
        }
        var toolIndex = ImmutableDictionary.CreateBuilder<string, SessionRegisteredTool>(StringComparer.Ordinal);
        long characters = 0;
        foreach (var registration in tools)
        {
            if (registration is null || registration.Declaration is null || registration.Adapter is null ||
                registration.ExecutionMode is not (ToolExecutionMode.Parallel or ToolExecutionMode.Sequential) || !Enum.IsDefined(registration.Exposure))
                throw Error(SessionRuntimeRegistryFailure.InvalidRegistration);
            var declaration = registration.Declaration.Value;
            Charge(declaration, ref characters, default);
            if (registration.Namespace is { } grouping) Charge(JsonSerializer.SerializeToElement(grouping), ref characters, default);
            var name = Declaration(declaration);
            string adapterName;
            try { adapterName = registration.Adapter.Name; }
            catch (Exception) { throw Error(SessionRuntimeRegistryFailure.InvalidRegistration); }
            if (name != adapterName || !toolIndex.TryAdd(name, registration with { Adapter = new NamedAdapter(name, registration.Adapter) }))
                throw Error(SessionRuntimeRegistryFailure.InvalidRegistration);
        }
        _models = modelIndex.ToImmutable(); _tools = toolIndex.ToImmutable(); _policy = policy;
        _registeredTools = tools.Select(tool => _tools[Name(tool.Declaration.Value)]).ToImmutableArray();
        // Validate invocation limits even when this registry initially declares no active tools.
        _ = _options.InvocationScopes is { } initialScopes
            ? ToolInvoker.WithNestedCalls([], policy, initialScopes, _options.PreparedToolHooks, options: _options.ToolInvokerOptions)
            : _options.PreparedToolHooks is { } initialHooks
            ? ToolInvoker.WithPreparedHooks([], policy, initialHooks, options: _options.ToolInvokerOptions)
            : new ToolInvoker([], policy, options: _options.ToolInvokerOptions);
    }

    internal bool RequiresInvocationOwner => _options.BindNestedCallsToSessionOwner;

    private SessionRuntimeRegistry(SessionRuntimeRegistry source, ToolInvocationScopeOptions scopes)
    {
        _models = source._models; _tools = source._tools; _registeredTools = source._registeredTools; _policy = source._policy;
        _options = source._options with { InvocationScopes = scopes };
    }
    internal SessionRuntimeRegistry BindInvocationOwner(ToolInvocationScopeOptions scopes) => new(this, scopes);

    internal ImmutableArray<string> NormalizeActiveTools(ImmutableArray<string> names, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (names.IsDefault || names.Length > _options.MaximumDeclarations)
            throw Error(SessionRuntimeRegistryFailure.ResourceLimit);
        var selected = ImmutableArray.CreateBuilder<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        long nameCharacters = 0;
        foreach (var name in names)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (name is null || name.Length > _options.MaximumCharacters - nameCharacters)
                throw Error(SessionRuntimeRegistryFailure.ResourceLimit);
            nameCharacters += name.Length;
            if (!Identity(name)) throw Error(SessionRuntimeRegistryFailure.InvalidRegistration);
            if (seen.Add(name) && _tools.TryGetValue(name, out var tool) && tool.Exposure != ToolExposure.Hidden)
                selected.Add(name);
        }
        return selected.ToImmutable();
    }

    internal TranscriptEntry? CreateActivationMessage(ImmutableArray<string> names, ImmutableArray<string> previous,
        long timestamp, CancellationToken cancellationToken)
    {
        var selected = NormalizeActiveTools(names, cancellationToken);
        if (previous.SequenceEqual(selected, StringComparer.Ordinal)) return null;
        var body = JsonData.Parse(JsonSerializer.Serialize(new { role = "system", content = "", timestamp,
            toolsRemoved = previous.Select(name => new { name }),
            toolsAdded = selected.Select(name => _tools[name].Declaration.Value) }));
        long characters = 0; Charge(body.Value, ref characters, cancellationToken);
        return new("system", body);
    }

    internal ToolLoadoutPresentation? PrepareActiveLoadout(ImmutableArray<string> names, CancellationToken token)
    {
        if (!_registeredTools.Any(tool => tool.PrepareLoadout is not null)) return null;
        var activeNames = names.ToImmutableHashSet(StringComparer.Ordinal);
        var loadout = new ToolLoadout(names.Select(name => Metadata(_tools[name])).ToImmutableArray(),
            _registeredTools.Where(tool => ToolExposureSemantics.IsCallable(tool.Exposure, activeNames.Contains(Name(tool.Declaration.Value))))
                .Select(Metadata).ToImmutableArray(), _registeredTools.Select(Metadata).ToImmutableArray());
        return ToolLoadoutPresentation.Prepare(loadout, (name, original) => _tools[name].PrepareLoadout?.Invoke(original),
            _options.ReportLoadoutDiagnostic, _options.MaximumCharacters, token);
        static ToolLoadoutTool Metadata(SessionRegisteredTool tool) => new(tool.Declaration, tool.Exposure) { Namespace = tool.Namespace };
    }

    public SessionRuntimeSelection Resolve(SessionContextProjection context, ModelDescriptor? fallbackModel = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        SessionModelBinding model;
        if (context.Model is { } selected)
        {
            if (!_models.TryGetValue((selected.Provider, selected.ModelId), out model!))
                throw Error(SessionRuntimeRegistryFailure.UnknownModel);
        }
        else model = Model(fallbackModel);
        return Resolve(model.Model, context.LlmMessages, context.ThinkingLevel, cancellationToken);
    }

    public SessionRuntimeSelection Resolve(ModelDescriptor model, ImmutableArray<TranscriptEntry> messages,
        string thinkingLevel = "off", CancellationToken cancellationToken = default, bool prepareLoadout = true,
        ToolLoadoutPresentation? preparedLoadout = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var binding = Model(model);
        if (thinkingLevel != "off") throw Error(SessionRuntimeRegistryFailure.UnsupportedThinkingLevel);
        if (messages.IsDefault) throw Error(SessionRuntimeRegistryFailure.InvalidTranscript);
        if (messages.Length > _options.MaximumMessages) throw Error(SessionRuntimeRegistryFailure.ResourceLimit);
        var active = new Dictionary<string, JsonData>(StringComparer.Ordinal);
        var order = new List<string>(); var declarations = 0; long characters = 0;
        foreach (var message in messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (message is null || message.WireBody is null) throw Error(SessionRuntimeRegistryFailure.InvalidTranscript);
            var body = message.WireBody.Value;
            Charge(body, ref characters, cancellationToken);
            if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("role", out var role) ||
                role.ValueKind != JsonValueKind.String || role.GetString() != message.Role)
                throw Error(SessionRuntimeRegistryFailure.InvalidTranscript);
            if (message.Role != "system") continue;
            if (body.TryGetProperty("toolsRemoved", out var removed))
                foreach (var reference in Array(removed))
                {
                    Count();
                    var name = Name(reference);
                    if (active.Remove(name)) order.Remove(name);
                }
            if (body.TryGetProperty("toolsAdded", out var added))
                foreach (var declaration in Array(added))
                {
                    Count(); cancellationToken.ThrowIfCancellationRequested();
                    var name = Declaration(declaration);
                    if (!active.ContainsKey(name))
                    {
                        if (active.Count >= _options.MaximumTools) throw Error(SessionRuntimeRegistryFailure.ResourceLimit);
                        order.Add(name);
                    }
                    active[name] = JsonData.FromElement(declaration);
                }
        }
        var resolved = ImmutableArray.CreateBuilder<SessionRegisteredTool>();
        var rawDeclarations = ImmutableArray.CreateBuilder<JsonData>();
        foreach (var name in order)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_tools.TryGetValue(name, out var tool)) throw Error(SessionRuntimeRegistryFailure.UnknownTool);
            if (tool.Exposure == ToolExposure.Hidden) throw Error(SessionRuntimeRegistryFailure.UnsupportedDeclaration);
            if (!Same(active[name].Value, tool.Declaration.Value, cancellationToken))
                throw Error(SessionRuntimeRegistryFailure.DeclarationMismatch);
            resolved.Add(tool); rawDeclarations.Add(active[name]);
        }
        var activeNames = order.ToImmutableHashSet(StringComparer.Ordinal);
        var callable = _registeredTools.Where(tool => ToolExposureSemantics.IsCallable(tool.Exposure,
            activeNames.Contains(Name(tool.Declaration.Value)))).ToImmutableArray();
        var executable = resolved.Concat(callable.Where(tool => !activeNames.Contains(Name(tool.Declaration.Value)))).ToImmutableArray();
        var limits = (_options.ToolInvokerOptions ?? new ToolInvokerOptions()) with { AllowedRootTools = activeNames,
            AllowedNestedTools = callable.Select(tool => Name(tool.Declaration.Value)).ToImmutableHashSet(StringComparer.Ordinal) };
        var invoker = _options.InvocationScopes is { } scopes
            ? ToolInvoker.WithNestedCalls(executable.Select(tool => tool.Adapter), _policy,
                scopes with { ExecutionMode = binding.ExecutionMode,
                    SequentialTools = executable.Where(tool => tool.ExecutionMode == ToolExecutionMode.Sequential)
                        .Select(tool => Name(tool.Declaration.Value)).ToImmutableHashSet(StringComparer.Ordinal) },
                _options.PreparedToolHooks, options: limits)
            : _options.PreparedToolHooks is { } preparedHooks
            ? ToolInvoker.WithPreparedHooks(executable.Select(tool => tool.Adapter), _policy, preparedHooks, options: limits)
            : new ToolInvoker(executable.Select(tool => tool.Adapter), _policy, options: limits);
        var definitions = resolved.Select(tool => new ToolDefinition(Name(tool.Declaration.Value), invoker, tool.ExecutionMode)).ToImmutableArray();
        var hooks = binding.Hooks;
        ImmutableArray<ToolLoadoutDiagnostic> loadoutDiagnostics = [];
        var presentation = preparedLoadout ?? (prepareLoadout ? PrepareActiveLoadout(order.ToImmutableArray(), cancellationToken) : null);
        if (presentation is not null)
        {
            loadoutDiagnostics = presentation.Diagnostics;
            var priorFinal = hooks?.FinalTransformRequestMessages;
            hooks = (hooks ?? new()) with { FinalTransformRequestMessages = async (requestMessages, token) =>
            {
                var transformed = priorFinal is null ? requestMessages : await priorFinal(requestMessages, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested(); return presentation.Project(transformed);
            } };
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(new(binding.Model, binding.Transport, definitions, binding.ToolHooks, binding.ExecutionMode, hooks),
            rawDeclarations.ToImmutable()) { LoadoutDiagnostics = loadoutDiagnostics };

        void Count()
        {
            if (++declarations > _options.MaximumDeclarations) throw Error(SessionRuntimeRegistryFailure.ResourceLimit);
        }
    }

    private SessionModelBinding Model(ModelDescriptor? model)
    {
        if (model is null || model.Provider is null || model.Id is null || model.Api is null)
            throw Error(SessionRuntimeRegistryFailure.UnknownModel);
        if ((long)model.Provider.Length + model.Id.Length + model.Api.Length > _options.MaximumCharacters)
            throw Error(SessionRuntimeRegistryFailure.ResourceLimit);
        if (!_models.TryGetValue((model.Provider, model.Id), out var binding) || binding.Model != model)
            throw Error(SessionRuntimeRegistryFailure.UnknownModel);
        return binding;
    }
    private void Charge(JsonElement value, ref long characters, CancellationToken token)
    {
        var size = value.GetRawText().Length;
        if (size > _options.MaximumCharacters - characters) throw Error(SessionRuntimeRegistryFailure.ResourceLimit);
        characters += size; CheckJson(value, 0, token);
    }
    private void CheckJson(JsonElement value, int depth, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            if (++depth > _options.MaximumJsonDepth) throw Error(SessionRuntimeRegistryFailure.ResourceLimit);
            if (value.ValueKind == JsonValueKind.Object)
                foreach (var property in value.EnumerateObject())
                { if (!Unicode(property.Name)) throw Error(SessionRuntimeRegistryFailure.InvalidTranscript); CheckJson(property.Value, depth, token); }
            else foreach (var child in value.EnumerateArray()) CheckJson(child, depth, token);
        }
        else if (value.ValueKind == JsonValueKind.String)
        {
            try { if (!Unicode(value.GetString()!)) throw Error(SessionRuntimeRegistryFailure.InvalidTranscript); }
            catch (InvalidOperationException) { throw Error(SessionRuntimeRegistryFailure.InvalidTranscript); }
        }
    }
    private static string Declaration(JsonElement value)
    {
        var name = Name(value);
        if (!value.TryGetProperty("description", out var description) || description.ValueKind != JsonValueKind.String ||
            !value.TryGetProperty("parameters", out var parameters) || parameters.ValueKind != JsonValueKind.Object ||
            value.TryGetProperty("type", out var type) && (type.ValueKind != JsonValueKind.String || type.GetString() != "function"))
            throw Error(SessionRuntimeRegistryFailure.UnsupportedDeclaration);
        return name;
    }
    private static string Name(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("name", out var name) ||
            name.ValueKind != JsonValueKind.String || !Identity(name.GetString()!))
            throw Error(SessionRuntimeRegistryFailure.UnsupportedDeclaration);
        return name.GetString()!;
    }
    private static IEnumerable<JsonElement> Array(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array) throw Error(SessionRuntimeRegistryFailure.InvalidTranscript);
        return value.EnumerateArray();
    }
    private static bool Same(JsonElement left, JsonElement right, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (left.ValueKind != right.ValueKind) return false;
        if (left.ValueKind == JsonValueKind.Object)
        {
            var properties = right.EnumerateObject().ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
            var count = 0;
            foreach (var property in left.EnumerateObject())
                if (++count > properties.Count || !properties.TryGetValue(property.Name, out var other) || !Same(property.Value, other, token)) return false;
            return count == properties.Count;
        }
        if (left.ValueKind == JsonValueKind.Array)
            return left.GetArrayLength() == right.GetArrayLength() &&
                left.EnumerateArray().Zip(right.EnumerateArray()).All(pair => Same(pair.First, pair.Second, token));
        return left.ValueKind == JsonValueKind.String ? left.GetString() == right.GetString() : left.GetRawText() == right.GetRawText();
    }
    private static bool Identity(string? value) => !string.IsNullOrWhiteSpace(value) && Unicode(value) && !value.Any(char.IsControl);
    private static bool Unicode(string value)
    {
        for (var index = 0; index < value.Length; index++)
            if (char.IsHighSurrogate(value[index]))
            { if (++index >= value.Length || !char.IsLowSurrogate(value[index])) return false; }
            else if (char.IsLowSurrogate(value[index])) return false;
        return true;
    }
    private sealed class NamedAdapter(string name, IPreparedToolAdapter inner) : IInvocationPreparedToolAdapter, IInitialToolArgumentPreparationAdapter
    {
        public string Name => name;
        public ValueTask<JsonData> PrepareInitialArgumentsAsync(ToolInvocation invocation, CancellationToken token) =>
            inner is IInitialToolArgumentPreparationAdapter initial
                ? initial.PrepareInitialArgumentsAsync(invocation, token)
                : ValueTask.FromResult(invocation.Call.Arguments);
        public ValueTask<PreparedToolAction> PrepareAsync(ToolInvocation invocation, CancellationToken token) => inner.PrepareAsync(invocation, token);
        public ValueTask<bool> ValidateAsync(PreparedToolAction action, CancellationToken token) => inner.ValidateAsync(action, token);
        public ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action, CancellationToken token) => inner.ExecuteAsync(action, token);
        public ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action, ToolProgressCallback onProgress,
            CancellationToken token) => inner.ExecuteAsync(action, onProgress, token);
        public ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, PreparedToolAction action,
            ToolProgressCallback onProgress, CancellationToken token) => inner is IInvocationPreparedToolAdapter aware
                ? aware.ExecuteAsync(invocation, action, onProgress, token)
                : inner.ExecuteAsync(action, onProgress, token);
    }
    private static SessionRuntimeRegistryException Error(SessionRuntimeRegistryFailure failure) => new(failure);
}
