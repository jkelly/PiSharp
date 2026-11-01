using PiSharp.CustomAgents.Testing;
using PiSharp.CustomAgents.Workflows;
using PiSharp.CustomAgents.Workflows.Hosting;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Sqlite;
using PiSharp.Durable.Storage;

/// <summary>A Harness with a workflow catalog and runner over faux models, on memory or SQLite storage.</summary>
internal sealed class TestHost : IAsyncDisposable
{
    private TestHost(IHarness harness, IRegistry registry, WorkflowCatalog catalog, FauxAgentModels models, List<Exception> reports)
    {
        Harness = harness;
        Registry = registry;
        Catalog = catalog;
        Runner = new WorkflowRunner(harness, catalog);
        Models = models;
        Reports = reports;
    }

    public IHarness Harness { get; }
    public IRegistry Registry { get; }
    public WorkflowCatalog Catalog { get; }
    public WorkflowRunner Runner { get; }
    public FauxAgentModels Models { get; }
    /// <summary>Errors the Harness reported (HarnessOptions.OnReport).</summary>
    public List<Exception> Reports { get; }

    public static DurableContext Context => DurableContext.Background;

    /// <summary>Opens a host; <paramref name="setup"/> adds workflows before scheduling resumes.</summary>
    public static async Task<TestHost> OpenAsync(Action<WorkflowCatalog> setup, IStorage? storage = null, FauxAgentModels? models = null,
        WorkflowCatalogOptions? options = null)
    {
        models ??= new FauxAgentModels();
        var registry = PiSharp.Durable.Registry.Create();
        var catalog = new WorkflowCatalog(registry, options ?? new WorkflowCatalogOptions { DefaultModel = models.Model() });
        setup(catalog);
        var reports = new List<Exception>();
        var harness = await PiSharp.Durable.Harness.OpenAsync(storage ?? new MemoryStorage(), new HarnessOptions
        {
            Models = models,
            Registry = registry,
            OnReport = error => { lock (reports) reports.Add(error); },
        }, Context);
        harness.Resume();
        return new TestHost(harness, registry, catalog, models, reports);
    }

    /// <summary>A SQLite file in a fresh temp directory.</summary>
    public static string SqlitePath(string name) => Path.Combine(Program.Temp(name), "agents.sqlite");

    public static async Task<IStorage> SqliteAsync(string path) => await SqliteStorage.OpenAsync(path);

    public async ValueTask DisposeAsync() => await Harness.CloseAsync(Context);
}
