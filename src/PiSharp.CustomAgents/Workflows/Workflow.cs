// Decision 0012: a workflow is a named, versioned series of actions over one Data class. The builder records steps; Build validates them;
// a WorkflowCatalog compiles the result onto a durable task definition (Engine/WorkflowEngine.cs).
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PiSharp.CustomAgents.Workflows.Engine;
using PiSharp.Durable;

namespace PiSharp.CustomAgents.Workflows;

/// <summary>Entry point for defining workflows.</summary>
public static partial class Workflow
{
    /// <summary>The target that ends a run successfully, for <see cref="ActionResult.GoTo"/> and <see cref="ActionOptions.Next"/>.</summary>
    public const string End = "#end";

    /// <summary>Starts a workflow over <typeparamref name="TData"/> named <paramref name="name"/>
    /// (<c>^[A-Za-z][A-Za-z0-9_.-]{0,63}$</c>).</summary>
    public static WorkflowBuilder<TData> For<TData>(string name) where TData : class => new(name);

    /// <summary>The task kind a workflow runs as.</summary>
    public static string TaskKind(string workflow) => "pisharp.workflow." + workflow;

    internal static bool IsName(string? name) => name is not null && NamePattern().IsMatch(name);

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_.-]{0,63}$")]
    private static partial Regex NamePattern();
}

/// <summary>A built workflow, independent of its Data type.</summary>
public abstract class WorkflowDefinition
{
    private protected WorkflowDefinition(string name, int version, string? description, ImmutableArray<string> steps)
    {
        Name = name;
        Version = version;
        Description = description;
        Steps = steps;
    }

    /// <summary>The workflow's name.</summary>
    public string Name { get; }
    /// <summary>The definition version stored with every run; bump it (with a migration) when a change would break live runs.</summary>
    public int Version { get; }
    /// <summary>What the workflow does, for tools and listings.</summary>
    public string? Description { get; }
    /// <summary>The step names in order: actions and routers.</summary>
    public ImmutableArray<string> Steps { get; }
    /// <summary>The Data type.</summary>
    public abstract Type DataType { get; }
    /// <summary>A hash of the definition's shape (name, version, Data type, steps); runs record it so drift at one version is reported.</summary>
    public abstract string Fingerprint { get; }
    /// <summary>The durable task kind.</summary>
    public string TaskKind => Workflow.TaskKind(Name);

    internal abstract IWorkflowEngine Compile(Hosting.WorkflowCatalog catalog);
}

/// <summary>A built workflow over <typeparamref name="TData"/>; add it to a <see cref="Hosting.WorkflowCatalog"/> to run it.</summary>
public sealed class Workflow<TData> : WorkflowDefinition, Hosting.IHasPromptDefaults where TData : class
{
    internal Workflow(WorkflowBuilder<TData> builder, ImmutableArray<Step<TData>> steps)
        : base(builder.Name, builder.VersionNumber, builder.DescriptionText, [.. steps.Select(step => step.Name)])
    {
        Nodes = steps;
        ByName = steps.ToImmutableDictionary(step => step.Name, StringComparer.Ordinal);
        JsonOptions = builder.Json ?? DataCodec.DefaultOptions;
        Limits = builder.LimitsValue;
        Model = builder.ModelValue;
        Thinking = builder.ThinkingValue;
        Instructions = builder.InstructionsValue;
        AbortHandler = builder.AbortHandler;
        Migration = builder.MigrationValue;
        try { Results = builder.ResultTypes.ToImmutableDictionary(type => type, type => ResultTool.Create(Name, type, JsonOptions)); }
        catch (ArgumentException error) { throw new WorkflowDefinitionException($"Workflow '{Name}': {error.Message}"); }
        Fingerprint = ComputeFingerprint();
    }

    internal ImmutableDictionary<Type, ResultTool> Results { get; }

    /// <inheritdoc/>
    public override Type DataType => typeof(TData);
    /// <inheritdoc/>
    public override string Fingerprint { get; }
    /// <summary>The Data serializer options.</summary>
    public JsonSerializerOptions JsonOptions { get; }
    /// <summary>The run limits.</summary>
    public WorkflowLimits Limits { get; }
    /// <summary>The model for prompts that name none; null uses the catalog default.</summary>
    public ModelRef? Model { get; }
    /// <summary>The thinking level for prompts that name none.</summary>
    public string? Thinking { get; }
    /// <summary>Instructions for every prompt of the workflow.</summary>
    public string? Instructions { get; }

    internal ImmutableArray<Step<TData>> Nodes { get; }
    internal ImmutableDictionary<string, Step<TData>> ByName { get; }
    internal Func<TData, ActionContext, CancellationToken, Task>? AbortHandler { get; }
    internal WorkflowMigration? Migration { get; }

    internal override IWorkflowEngine Compile(Hosting.WorkflowCatalog catalog) => new WorkflowEngine<TData>(this, catalog);

    private string ComputeFingerprint()
    {
        var text = new StringBuilder().Append(Name).Append('\n').Append(Version).Append('\n').Append(typeof(TData).FullName);
        foreach (var step in Nodes) text.Append('\n').Append(step.Kind).Append(':').Append(step.Name).Append("->").Append(step.Shape);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..16];
    }
}

/// <summary>Records the steps of a workflow over <typeparamref name="TData"/>. Steps run in the order added unless an action jumps.</summary>
public sealed partial class WorkflowBuilder<TData> where TData : class
{
    private readonly List<Step<TData>> steps = [];
    private int routers;

    internal WorkflowBuilder(string name)
    {
        if (!Workflow.IsName(name)) throw new ArgumentException($"'{name}' is not a workflow name (^[A-Za-z][A-Za-z0-9_.-]{{0,63}}$).", nameof(name));
        Name = name;
    }

    internal string Name { get; }
    internal int VersionNumber { get; private set; } = 1;
    internal string? DescriptionText { get; private set; }
    internal JsonSerializerOptions? Json { get; private set; }
    internal WorkflowLimits LimitsValue { get; private set; } = WorkflowLimits.Default;
    internal ModelRef? ModelValue { get; private set; }
    internal string? ThinkingValue { get; private set; }
    internal string? InstructionsValue { get; private set; }
    internal Func<TData, ActionContext, CancellationToken, Task>? AbortHandler { get; private set; }
    internal WorkflowMigration? MigrationValue { get; private set; }

    /// <summary>Sets the definition version (default 1). Runs stored by an older version need <paramref name="migration"/>; without one
    /// they are blocked rather than run against steps they do not know.</summary>
    public WorkflowBuilder<TData> Version(int version, WorkflowMigration? migration = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        VersionNumber = version;
        MigrationValue = migration;
        return this;
    }

    /// <summary>Describes the workflow, for listings and the agent-facing workflow tool.</summary>
    public WorkflowBuilder<TData> Describe(string description)
    {
        DescriptionText = description;
        return this;
    }

    /// <summary>The model for prompts and agents that name none.</summary>
    public WorkflowBuilder<TData> WithModel(ModelRef model)
    {
        ModelValue = model ?? throw new ArgumentNullException(nameof(model));
        return this;
    }

    /// <summary>The thinking level for prompts that name none (off, minimal, low, medium, high, xhigh).</summary>
    public WorkflowBuilder<TData> WithThinking(string level)
    {
        ThinkingValue = level ?? throw new ArgumentNullException(nameof(level));
        return this;
    }

    /// <summary>Instructions rendered into every prompt of the workflow.</summary>
    public WorkflowBuilder<TData> WithInstructions(string instructions)
    {
        InstructionsValue = instructions ?? throw new ArgumentNullException(nameof(instructions));
        return this;
    }

    /// <summary>The System.Text.Json options for the Data class (default <see cref="JsonSerializerDefaults.Web"/>).</summary>
    public WorkflowBuilder<TData> WithJsonOptions(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var copy = new JsonSerializerOptions(options);
        copy.MakeReadOnly(populateMissingResolver: true);
        Json = copy;
        return this;
    }

    /// <summary>Sets the run limits.</summary>
    public WorkflowBuilder<TData> WithLimits(int maxSteps = 1000, int maxDataBytes = 256 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSteps, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDataBytes, 64);
        LimitsValue = new WorkflowLimits { MaxSteps = maxSteps, MaxDataBytes = maxDataBytes };
        return this;
    }

    /// <summary>Runs when the workflow is aborted, with the last committed Data; its Data changes are kept in the aborted result.</summary>
    public WorkflowBuilder<TData> OnAbort(Func<TData, ActionContext, CancellationToken, Task> handler)
    {
        AbortHandler = handler ?? throw new ArgumentNullException(nameof(handler));
        return this;
    }

    /// <summary>Adds an action.</summary>
    public WorkflowBuilder<TData> Then(IAction<TData> action, ActionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        return Add(new ActionStep<TData>(action.Name, "code", action, options ?? new ActionOptions()));
    }

    /// <summary>Adds an action from a delegate that decides what happens next.</summary>
    public WorkflowBuilder<TData> Then(string name, Func<TData, ActionContext, CancellationToken, Task<ActionResult>> run, ActionOptions? options = null) =>
        Then(new DelegateAction<TData>(name, run), options);

    /// <summary>Adds an action from a delegate; the workflow continues with the next action.</summary>
    public WorkflowBuilder<TData> Then(string name, Func<TData, ActionContext, CancellationToken, Task> run, ActionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        return Then(new DelegateAction<TData>(name, async (data, context, token) =>
        {
            await run(data, context, token).ConfigureAwait(false);
            return ActionResult.Next();
        }), options);
    }

    /// <summary>Adds a synchronous action that changes the Data; the workflow continues with the next action.</summary>
    public WorkflowBuilder<TData> Then(string name, Action<TData> run, ActionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        return Then(new DelegateAction<TData>(name, (data, _, _) =>
        {
            run(data);
            return Task.FromResult(ActionResult.Next());
        }), options);
    }

    /// <summary>Routes to <paramref name="ifTrue"/> or <paramref name="ifFalse"/> by <paramref name="condition"/>, evaluated against the
    /// Data when the step is reached. Routers are not checkpointed: they must be pure functions of the Data.</summary>
    public WorkflowBuilder<TData> Branch(Func<TData, bool> condition, string ifTrue, string ifFalse, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentException.ThrowIfNullOrWhiteSpace(ifTrue);
        ArgumentException.ThrowIfNullOrWhiteSpace(ifFalse);
        return Add(new RouterStep<TData>(RouterName(name, "branch"), data => condition(data) ? ifTrue : ifFalse, [ifTrue, ifFalse]));
    }

    /// <summary>Routes to the step <paramref name="select"/> names (or <see cref="Workflow.End"/>), evaluated against the Data when the
    /// step is reached. Must be a pure function of the Data.</summary>
    public WorkflowBuilder<TData> Switch(Func<TData, string> select, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(select);
        return Add(new RouterStep<TData>(RouterName(name, "switch"), select, []));
    }

    /// <summary>Validates the steps and returns the workflow.</summary>
    public Workflow<TData> Build()
    {
        if (steps.Count == 0) throw new WorkflowDefinitionException($"Workflow '{Name}' has no steps.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in steps)
            if (!names.Add(step.Name)) throw new WorkflowDefinitionException($"Workflow '{Name}' has two steps named '{step.Name}'.");
        foreach (var step in steps)
            foreach (var target in step.StaticTargets)
                if (target != Workflow.End && !names.Contains(target))
                    throw new WorkflowDefinitionException($"Step '{step.Name}' of workflow '{Name}' continues with '{target}', which is not a step.");
        foreach (var step in steps) step.Validate(Name);
        return new Workflow<TData>(this, [.. steps]);
    }

    internal WorkflowBuilder<TData> Add(Step<TData> step)
    {
        if (!step.Name.StartsWith('#') && !Workflow.IsName(step.Name))
            throw new ArgumentException($"'{step.Name}' is not an action name (^[A-Za-z][A-Za-z0-9_.-]{{0,63}}$).");
        steps.Add(step);
        return this;
    }

    private string RouterName(string? name, string kind)
    {
        routers++;
        return name ?? "#" + kind + routers;
    }
}
