using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

internal sealed class SessionBoundaryFixture
{
    internal sealed record Raw(Task Original, AggregateException? Fault, Exception? Direct);
    internal static readonly List<SessionBoundaryFixture> Cases = [];
    private readonly Dictionary<Task,Raw> cached = new(ReferenceEqualityComparer.Instance);
    internal ImmutableArray<Raw> CapturedOriginals { get { lock(originalGate) return Originals.Distinct<Task>(ReferenceEqualityComparer.Instance).Select(task=>cached.GetValueOrDefault(task) ?? new Raw(task,null,null)).ToImmutableArray(); } }
    internal SessionBoundaryFixture() { lock(Cases) Cases.Add(this); }
    internal static readonly ModelDescriptor Model = new("fixture", "openai-responses", "boundary");
    internal readonly TaskCompletionSource StorageEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly TaskCompletionSource StorageRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal bool HoldStorage;
    internal Task? StorageOriginal;
    internal readonly List<Task> Originals = [];
    private readonly object originalGate = new();
    private readonly HashSet<Task> acknowledged = new(ReferenceEqualityComparer.Instance);
    internal ReplaceableAgentSession Owner = null!;
    internal PersistentAgentSession Session => Owner.Current.Session;
    internal Task<T> Keep<T>(Task<T> original) { lock(originalGate) Originals.Add(original); return original; }
    internal Task Keep(Task original) { lock(originalGate) Originals.Add(original); return original; }
    internal void Retain(SessionBoundaryOriginalEvidence row)
    {
        _ = Keep(row.Original);
        if(row.Observed is null)return;
        lock(originalGate) if(!cached.ContainsKey(row.Original)) cached.Add(row.Original,new(row.Original,row.Fault,row.Observed));
    }
    internal async Task<Raw> Observe(Task task)
    {
        _ = Keep(task); Exception? direct=null;
        try { await task; } catch(Exception error) { direct=error; }
        lock(originalGate)
        {
            if(!cached.TryGetValue(task,out var row))
                cached.Add(task,row=new(task,task.IsFaulted?task.Exception:null,direct));
            return row;
        }
    }
    internal void AckFault(Task task, Func<Raw,bool> fullGraphPredicate)
    {
        Raw row; lock(originalGate) row=cached[task];
        Check(task.IsFaulted&&row.Fault is not null&&fullGraphPredicate(row),"Exact full fault graph acknowledgment refused.");
        acknowledged.Add(task);
    }
    internal void AckCanceled(Task task,CancellationToken exactToken)
    {
        Raw row;lock(originalGate)row=cached[task];
        Check(task.IsCanceled&&row.Direct is OperationCanceledException canceled&&canceled.CancellationToken==exactToken,
            "Exact canceled original/token acknowledgment refused.");
        acknowledged.Add(task);
    }
    internal readonly List<Exception> SynchronousCleanupFailures=[];
    internal void StartCleanup(Func<ValueTask> close)
    { try { _ = Keep(close().AsTask()); } catch(Exception error) { SynchronousCleanupFailures.Add(error); } }
    internal static SessionEntry Record(string type, string id, string? parent, object fields)
    {
        using var bytes = new MemoryStream();
        using(var writer = new Utf8JsonWriter(bytes))
        {
            writer.WriteStartObject(); writer.WriteString("type", type); writer.WriteString("id", id);
            writer.WriteString("parentId", parent); writer.WriteString("timestamp", "2026-10-07T00:00:00.000Z");
            foreach(var property in JsonSerializer.SerializeToElement(fields).EnumerateObject())
            { writer.WritePropertyName(property.Name); property.Value.WriteTo(writer); }
            writer.WriteEndObject();
        }
        return new SessionEntryCodec().Parse(System.Text.Encoding.UTF8.GetString(bytes.ToArray()));
    }
    internal static async Task<SessionBoundaryFixture> Open()
    {
        var f = new SessionBoundaryFixture();
        var directory = Path.Combine(Path.GetTempPath(), "boundary-memory-" + Guid.NewGuid().ToString("N"));
        var backend = new SessionStorageBackend(directory, SessionStorageMode.InMemory);
        var factory = new Factory(f, backend); var storeOptions = new SessionLogStoreOptions(StorageFactory: factory);
        var path = Path.Combine(directory, "source.jsonl");
        var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new { type="session",version=3,id="source",
            timestamp="2026-10-07T00:00:00.000Z",cwd=directory }));
        var registry = new SessionRuntimeRegistry([new(Model,new NoTransport())],[],new NeverPolicy());
        var id = 0;
        PersistentAgentSession? unowned = null;
        try
        {
            var create = SessionLogStore.CreateNewAsync(path, header, storeOptions); _ = f.Keep(create);
            var store = await create;
            try
            {
                var seed = store.AppendAsync([
                    Record("model_change","model",null,new {provider=Model.Provider,modelId=Model.Id}),
                    Record("message","left","model",new {message=new {role="user",timestamp=0,content="left"}}),
                    Record("message","right","model",new {message=new {role="user",timestamp=0,content="right"}})]);
                _ = f.Keep(seed); await seed;
            }
            finally { await f.Keep(store.DisposeAsync().AsTask()); }
            var options = new PersistentAgentSessionOptions(UseLatestLeaf:false,SelectedLeafId:"right",SessionLogStoreOptions:storeOptions);
            var open = f.Keep(PersistentAgentSession.OpenWithRegistryAsync(path,registry,()=>0,()=>"entry-"+ ++id,options,Model));
            var session = await open; unowned = session;
            var lifecycle = new PersistentSessionLifecycle(registry,()=>0,()=>"entry-"+ ++id,
                options with {UseLatestLeaf=true,SelectedLeafId=null},()=>"new-"+Guid.NewGuid().ToString("N"),fileSystem:backend);
            f.Owner = new(session,(request,token)=>PersistentAgentSession.OpenWithRegistryAsync(request.Path,registry,
                ()=>0,()=>"entry-"+ ++id,options with {UseLatestLeaf=request.UseLatestLeaf,SelectedLeafId=request.SelectedLeafId},Model,token),lifecycle);
            unowned = null;
            return f;
        }
        catch(Exception error) { if(unowned is not null) f.StartCleanup(()=>unowned.DisposeAsync()); await f.Finish(error); throw; }
    }
    internal async Task Gate(Task gate, Task terminal)
    {
        using var deadline = new CancellationTokenSource();
        var delay = Task.Delay(TimeSpan.FromSeconds(10),deadline.Token); _ = Keep(delay);
        var race = Keep(Task.WhenAny(gate,terminal,delay));
        try
        {
            await race;
            if(gate.IsCompletedSuccessfully){await gate;return;}
            if(terminal.IsCompleted){await terminal;throw new IOException("Operation completed before expected gate.");}
            throw new TimeoutException("Bounded held-original diagnostic expired.");
        }
        finally
        {
            deadline.Cancel();
            var row=await Observe(delay);
            if(delay.IsCanceled) AckCanceled(delay,deadline.Token);
            else if(row.Direct is not null) throw row.Direct;
        }
    }
    internal async Task Finish(Exception? primary)
    {
        StorageRelease.TrySetResult();
        if(StorageOriginal is not null) _ = Keep(StorageOriginal);
        var errors = new List<Exception>(); if(primary is not null) errors.Add(primary);
        if(Owner is not null) StartCleanup(()=>Owner.DisposeAsync());
        errors.AddRange(SynchronousCleanupFailures);
        var joined = new HashSet<Task>(ReferenceEqualityComparer.Instance);
        for(var index=0;;index++)
        {
            Task task;lock(originalGate){if(index>=Originals.Count)break;task=Originals[index];}if(!joined.Add(task))continue;
            var row=await Observe(task);
            if(row.Direct is not null)
            {
                if(acknowledged.Contains(task)) continue;
                var actual = (Exception?)row.Fault ?? row.Direct;
                if(!errors.Any(item=>ReferenceEquals(item,actual))) errors.Add(actual);
            }
        }
        if(errors.Count!=0) throw new FixtureFailure(this,errors);
    }
    internal sealed class FixtureFailure(SessionBoundaryFixture fixture,IEnumerable<Exception> failures)
        : AggregateException("Boundary fixture primary and full original cleanup faults.",failures)
    { internal SessionBoundaryFixture Fixture {get;}=fixture; }
    internal static void Check(bool value,string message) { if(!value) throw new IOException(message); }
    private sealed class Factory(SessionBoundaryFixture fixture, ISessionLogStorageFactory underlying) : ISessionLogStorageFactory
    {
        public async ValueTask<ISessionLogStorage> OpenAsync(string path,bool createNew,CancellationToken token)
        {var original=fixture.Keep(underlying.OpenAsync(path,createNew,token).AsTask());return new Storage(fixture,await original);}
    }
    private sealed class Storage(SessionBoundaryFixture f,ISessionLogStorage inner) : ISessionLogStorage
    {
        public Stream ReadStream=>inner.ReadStream; public SessionLogStorageDurability Durability=>inner.Durability;
        public long Length=>inner.Length; public void PositionForAppend(long length)=>inner.PositionForAppend(length);
        public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes)=>new(f.Keep(inner.WriteAsync(bytes).AsTask()));
        public ValueTask FlushAsync()=>new(f.Keep(inner.FlushAsync().AsTask())); public void FlushToDisk()=>inner.FlushToDisk();
        public async ValueTask BeforeCheckpointAsync()
        {
            if(f.HoldStorage)
            { f.StorageOriginal=f.StorageRelease.Task;f.StorageEntered.TrySetResult();await f.StorageOriginal; }
            await f.Keep(inner.BeforeCheckpointAsync().AsTask());
        }
        public ValueTask DisposeAsync()=>new(f.Keep(inner.DisposeAsync().AsTask()));
    }
    private sealed class NoTransport : IChatTransport
    {
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request,[EnumeratorCancellation] CancellationToken token=default)
        { await Task.FromException(new IOException("No provider transport admitted in synthetic boundary fixture."));yield break; }
    }
    private sealed class NeverPolicy : IToolActionPolicy
    {
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation,PreparedToolAction action,CancellationToken token)
            => throw new IOException("No tool effects admitted.");
    }
}
