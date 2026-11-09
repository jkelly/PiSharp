using System.Reflection;
using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Host;
using PiSharp.Chord.Hosting.Http;
using PiSharp.Chord.Store;
using DurableContext = PiSharp.Durable.Chord.DurableContext;
using PiSharp.Durable.Store;
using PiSharp.Env;
using PiSharp.Env.Protocol;
using PiSharp.Tools.Processes;

// This file is copied into a fresh external project. It has no ProjectReference, friend assembly name or source import.
static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
Check(typeof(OwnedChildProcess).Assembly.GetName().Name == "PiSharp.Tools", "public shared process containment facade resolves transitively");
var frames = FrameCodec.Encode(new ProtocolFrame(FrameType.Request, 3, new JsonObject { ["op"] = "stat" }, new byte[] { 1, 2 }));
var decoded = await FrameCodec.ReadAsync(new MemoryStream(frames));
Check(decoded is { Id: 3, Type: FrameType.Request } && decoded.Payload.Span.SequenceEqual(new byte[] { 1, 2 }), "public protocol roundtrip");
var info = new RemoteInfo { Protocol = 1, Version = "1.5.0", Os = "windows", Arch = "x86_64", Home = "C:\\", Tmpdir = "C:\\", Separator = "\\", Cwd = "C:\\", Pid = 1 };
Check(RemotePlatformNames.NodePlatform(info) == ("win32", "x64"), "public environment platform mapping");
var secret = new byte[] { 1, 2, 3, 4 };
var cookie = SignedCookies.Create("test", "value", secret, secure: false);
Check(SignedCookies.Read(cookie.Split(';')[0], "test", secret) == "value", "public hosting cookie roundtrip");
var contract = Contract.Local("consumer.bytes", new() { Version = 1, Params = new(), Members = new() { ["echo"] = Schemas.Method(Schemas.Bytes(), Schemas.Bytes()) } });
await using (var store = (ChordStore)await Tables.OpenStoreAsync(await StoreBackends.OpenSqliteAsync(":memory:"), ChordContext.Background))
{
    var errors = new List<HostError>();
    await using (var host = Hosts.Create(new() { Store = store, OnError = errors.Add }))
    {
        var facet = Facet.Define((api, _) => api.Serve(contract, (_, _, _) => new(new Dictionary<string, object?> { ["echo"] = (HostMethod)((args, _) => Task.FromResult(args)) })));
        var loaded = await host.LoadAsync([new("@consumer/core", facet)], ChordContext.Background);
        Check(loaded.Failed.Count == 0, "public facet load");
        using var reference = await host.GetAsync(contract, new(), ChordContext.Background);
        var reply = (JsonNode?)await reference.CallAsync("echo", JsonValue.Create(new byte[] { 7, 9 }), ChordContext.Background);
        Check(reply is JsonValue value && value.TryGetValue<byte[]>(out var bytes) && bytes.SequenceEqual(new byte[] { 7, 9 }), "byte-bearing schema and call");
        var storage = await StoreStorage.OpenAsync(store, "consumer.storage", DurableContext.Background);
        await storage.CloseAsync(DurableContext.Background);
    }
    Check(errors.Count == 0, "host errors");
}
// Check public signature visibility across the admitted assemblies. Internal runtime values behind object are not public API types.
var assemblies = new[] { typeof(Hosts).Assembly, typeof(SignedCookies).Assembly, typeof(StoreStorage).Assembly, typeof(FrameCodec).Assembly, typeof(RemoteInfo).Assembly };
static void Visible(Type type)
{
    if (type.IsGenericParameter) return;
    if (type.HasElementType) { Visible(type.GetElementType()!); return; }
    if (type.IsGenericType) foreach (var argument in type.GetGenericArguments()) Visible(argument);
    if (type.Assembly.GetName().Name?.StartsWith("PiSharp.", StringComparison.Ordinal) == true)
        Check(type.IsVisible, "internal public-signature type: " + type.FullName);
}
// Reflection includes private implementation contracts in GetInterfaces(). They are not callable public API.
// Public method/property/field/event signatures below still pass through Visible without this filter.
static IEnumerable<Type> PublicInterfaces(Type type) => type.GetInterfaces().Where(implemented => implemented.IsVisible);
var storeImplementation = typeof(ChordStore).GetInterfaces().Single(type => type.FullName == "PiSharp.Chord.Store.IChordStore");
Check(!storeImplementation.IsVisible, "store implementation interface remains internal");
Check(!PublicInterfaces(typeof(ChordStore)).Contains(storeImplementation), "internal implemented interface is excluded from public-interface enumeration");
Check(PublicInterfaces(typeof(ChordStore)).Contains(typeof(IStore)), "public store interface remains checked");
// A hidden type in an actual exposed signature must still fail, including array and generic positions.
foreach (var hiddenSignature in new[] { storeImplementation, storeImplementation.MakeArrayType(), typeof(IReadOnlyList<>).MakeGenericType(storeImplementation) })
{
    var refused = false;
    try { Visible(hiddenSignature); } catch (InvalidOperationException) { refused = true; }
    Check(refused, "hidden public-signature control was accepted: " + hiddenSignature);
}
foreach (var assembly in assemblies)
foreach (var type in assembly.GetExportedTypes())
{
    if (type.BaseType is { } parent) Visible(parent);
    foreach (var implemented in PublicInterfaces(type)) Visible(implemented);
    foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
    {
        if (member is MethodInfo method) { Visible(method.ReturnType); foreach (var p in method.GetParameters()) Visible(p.ParameterType); }
        if (member is ConstructorInfo constructor) foreach (var p in constructor.GetParameters()) Visible(p.ParameterType);
        if (member is PropertyInfo property) Visible(property.PropertyType);
        if (member is FieldInfo field) Visible(field.FieldType);
        if (member is EventInfo @event && @event.EventHandlerType is { } handler) Visible(handler);
    }
}
Console.WriteLine("PASS package-only public consumer: five assemblies; protocol, environment, hosting, byte-schema host call, SQLite adapter and visible signatures.");
