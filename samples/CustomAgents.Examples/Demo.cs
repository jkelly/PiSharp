// Decision 0012: the demo's options, its models (scripted, or a real provider through NativeDurableModels) and the commands.
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using PiSharp.CustomAgents.Scripting;
using PiSharp.CustomAgents.Teams;
using PiSharp.CustomAgents.Testing;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Contracts;
using PiSharp.Durable.Sqlite;

namespace PiSharp.CustomAgents.Examples;

/// <summary>The command line.</summary>
internal sealed record DemoOptions(string Command, string? Provider, string? Model, string? Thinking, string Directory, string? CrashIn, string? Search,
    string? SearchModel)
{
    public const string Usage = """
        Usage: CustomAgents.Examples [orders|team|web|all] [--provider <id> --model <id>] [--thinking <level>] [--dir <path>]
                                     [--crash-in load|summarize] [--search brave|tavily|firecrawl|openai|anthropic|xai|openrouter]
                                     [--search-model <id>]
          orders      runs the orders workflow for orders 42 (a VIP) and 43 (default: all)
          team        asks the lead agent to handle a customer question with its tools, a subagent and the workflow
          web         asks the lead a question that its scout subagent answers with web_search
          --provider  a real provider (for example openai); its API key comes from its environment variable. Default: scripted models
          --dir       where the SQLite storage is kept (default ./custom-agents-demo); runs resume from it
          --crash-in  exits the process inside that orders action; run the command again to resume the run
          --search    the web search backend, with its key in BRAVE_API_KEY, TAVILY_API_KEY, FireCrawl_API_KEY, OPENAI_API_KEY,
                      ANTHROPIC_API_KEY, XAI_API_KEY or OPENROUTER_API_KEY. Default: canned results on scripted models, else the
                      first of those keys that is set
          --search-model  the model that searches, for openai, anthropic, xai or openrouter (for example anthropic/claude-sonnet-5.5
                      on openrouter). Default: the backend's own default
        """;

    public static DemoOptions? Parse(string[] args)
    {
        string command = "all";
        string? provider = null, model = null, thinking = null, crashIn = null, search = null, searchModel = null;
        var directory = Path.Combine(Environment.CurrentDirectory, "custom-agents-demo");
        for (var index = 0; index < args.Length; index++)
        {
            string? Value() => index + 1 < args.Length ? args[++index] : null;
            switch (args[index])
            {
                case "orders" or "team" or "web" or "all": command = args[index]; break;
                case "--provider": provider = Value(); if (provider is null) return null; break;
                case "--model": model = Value(); if (model is null) return null; break;
                case "--thinking": thinking = Value(); if (thinking is null) return null; break;
                case "--dir": directory = Value() is { } path ? Path.GetFullPath(path) : null!; if (directory is null) return null; break;
                case "--crash-in": crashIn = Value(); if (crashIn is not (OrdersDemo.Load or OrdersDemo.Summarize)) return null; break;
                case "--search": search = Value(); if (search is null || !PiSharp.CustomAgents.Tools.WebSearchTool.Backends.Contains(search)) return null; break;
                case "--search-model": searchModel = Value(); if (searchModel is null) return null; break;
                default: return null;
            }
        }
        if ((provider is null) != (model is null)) return null;
        if (searchModel is not null && search is not ("openai" or "anthropic" or "xai" or "openrouter")) return null;
        return new DemoOptions(command, provider, model, thinking, directory, crashIn, search, searchModel);
    }
}

/// <summary>The models the demo runs on, counting the requests this process sent.</summary>
internal sealed class DemoModels(IDurableModels inner, ModelRef model, bool scripted) : IDurableModels, IDisposable
{
    private int requests;

    public ModelRef Model { get; } = model;
    public bool Scripted { get; } = scripted;
    public int Requests => Volatile.Read(ref requests);

    public DurableModel? GetModel(string provider, string modelId) => inner.GetModel(provider, modelId);

    public async IAsyncEnumerable<StreamEvent> StreamSimple(DurableModel model, ImmutableArray<JsonValue> messages, DurableStreamOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref requests);
        await foreach (var item in inner.StreamSimple(model, messages, options, cancellationToken).ConfigureAwait(false)) yield return item;
    }

    public Task<AssistantMessage> CompleteSimpleAsync(DurableModel model, ImmutableArray<JsonValue> messages, DurableStreamOptions? options,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref requests);
        return inner.CompleteSimpleAsync(model, messages, options, cancellationToken);
    }

    public IAsyncEnumerable<StreamEvent> StreamDeferred(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) =>
        inner.StreamDeferred(model, handle, cancellationToken);

    public Task<AssistantMessage> FetchDeferredAsync(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) =>
        inner.FetchDeferredAsync(model, handle, cancellationToken);

    public Task CancelDeferredAsync(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) =>
        inner.CancelDeferredAsync(model, handle, cancellationToken);

    public void Dispose() => (inner as IDisposable)?.Dispose();
}

/// <summary>Runs the commands.</summary>
internal static class Demo
{
    public static async Task<int> RunAsync(DemoOptions options, TextWriter output, Func<string, string?> environment)
    {
        var (created, exit) = await ModelsAsync(options, output, environment);
        if (created is null) return exit;
        using var models = created;
        Directory.CreateDirectory(options.Directory);
        await output.WriteLineAsync($"Models: {(models.Scripted ? "scripted (FauxAgentModels)" : models.Model.Provider + "/" + models.Model.ModelId)}");
        await output.WriteLineAsync($"Storage: {Path.Combine(options.Directory, "agents.sqlite")}");
        var (search, missing) = WebDemo.Provider(options.Search, options.SearchModel, models.Scripted, environment);
        await output.WriteLineAsync("Web search: " + (search?.Name ?? "off (" + missing + ")"));

        // One team holds the agent definitions, their tools and the workflows; each process opens it over the same SQLite file, and
        // OpenAsync resumes whatever an earlier process left running.
        var host = new CSharpScriptHost();
        var team = new AgentTeam(new AgentTeamOptions
        {
            Models = models,
            DefaultModel = models.Model,
            DefaultThinking = options.Thinking,
            Orchestrator = TeamDemo.Lead,
            OnReport = error => output.WriteLine("  [report] " + error.Message),
        });
        var orders = OrdersDemo.Build(host, new Crash(options.CrashIn, options.Directory, output));
        team.Workflows.Add(orders);
        await TeamDemo.RegisterAsync(team, orders, host, output, scout: search is not null);
        if (search is not null) WebDemo.Register(team, search, output);

        var storage = await SqliteStorage.OpenAsync(Path.Combine(options.Directory, "agents.sqlite"));
        await using var session = await team.OpenAsync(storage);
        if (options.Command is "orders" or "all") await OrdersDemo.RunAsync(session, orders, output);
        var answered = options.Command is not ("team" or "all") || await TeamDemo.RunAsync(session, output);
        if (options.Command is "web" or "all")
        {
            if (search is null) await output.WriteLineAsync($"\n== web: skipped: {missing}");
            else answered &= await WebDemo.RunAsync(session, search, output);
        }
        await output.WriteLineAsync($"Model requests sent by this process: {models.Requests}");
        return answered ? 0 : 1;
    }

    private static async Task<(DemoModels? Models, int Exit)> ModelsAsync(DemoOptions options, TextWriter output, Func<string, string?> environment)
    {
        if (options.Provider is null)
        {
            var faux = new FauxAgentModels("scripted");
            ScriptedReplies.Configure(faux);
            return (new DemoModels(faux, faux.Model(), scripted: true), 0);
        }
        // The same checks as Durable.Chat: NativeDurableModels reads the provider's API key through the environment reader the host passes.
        var variables = NativeDurableModels.ApiKeyVariables(options.Provider);
        if (variables.Count > 0 && variables.All(name => string.IsNullOrEmpty(environment(name))))
        {
            await output.WriteLineAsync($"skipped: {string.Join(" or ", variables)} is not set");
            return (null, 0);
        }
        var access = new NativeDurableModels(new NativeDurableModelsOptions { Environment = environment });
        var model = access.GetModel(options.Provider, options.Model!);
        if (model is null || !NativeDurableModels.Supports(model))
        {
            await output.WriteLineAsync(model is null
                ? $"Unknown model {options.Provider}/{options.Model}."
                : $"{options.Provider}/{options.Model} ({model.Api}) has no durable route yet.");
            access.Dispose();
            return (null, 2);
        }
        return (new DemoModels(access, new ModelRef(options.Provider, options.Model!), scripted: false), 0);
    }
}
