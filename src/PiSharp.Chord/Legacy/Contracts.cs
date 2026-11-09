// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/contract.ts and schema.ts.
using System.Collections.ObjectModel;
using System.Text.Json.Nodes;

namespace PiSharp.Chord.Legacy;

public abstract record LegacyPlacement
{
    public abstract string Resolve(string typeName, JsonNode? input);
    public abstract bool Allows(string host);
    internal abstract JsonObject Describe();
    public static LegacyPlacement Named(string name) => new LegacyNamedHost(name);
    public static LegacyPlacement Family(string prefix, string key) => new LegacyHostFamily(prefix, key);
}
public sealed record LegacyNamedHost : LegacyPlacement
{
    public LegacyNamedHost(string name) { ArgumentException.ThrowIfNullOrEmpty(name); Name = name; }
    public string Name { get; }
    public override string Resolve(string typeName, JsonNode? input) => Name;
    public override bool Allows(string host) => host == Name;
    internal override JsonObject Describe() => new() { ["name"] = Name };
}
public sealed record LegacyHostFamily : LegacyPlacement
{
    public LegacyHostFamily(string prefix, string key) { ArgumentException.ThrowIfNullOrEmpty(prefix); ArgumentException.ThrowIfNullOrEmpty(key); Prefix = prefix; Key = key; }
    public string Prefix { get; }
    public string Key { get; }
    public override string Resolve(string typeName, JsonNode? input) => input is JsonObject obj && obj[Key] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0
        ? Prefix + text : throw ChordException.Invalid("placement", $"{typeName}: input field {Key} names no host");
    public override bool Allows(string host) => host.StartsWith(Prefix, StringComparison.Ordinal) && host.Length > Prefix.Length;
    internal override JsonObject Describe() => new() { ["prefix"] = Prefix, ["key"] = Key };
}

public abstract class LegacyMember
{
    private protected LegacyMember() { }
    internal abstract JsonObject Describe();
    public static LegacyMember State(JsonNode schema) => new LegacyStateMember(schema);
    public static LegacyMember Method(JsonNode? args = null, JsonNode? result = null) => new LegacyMethodMember(args, result);
    public static LegacyMember Opaque(string id) => new LegacyOpaqueMember(id);
}
public sealed class LegacyStateMember : LegacyMember
{
    private readonly JsonNode _schema;
    public LegacyStateMember(JsonNode schema) => _schema = schema.DeepClone();
    public JsonNode Schema => _schema.DeepClone();
    internal JsonNode RawSchema => _schema;
    internal override JsonObject Describe() => new() { ["kind"] = "state", ["schema"] = Schema };
}
public sealed class LegacyMethodMember : LegacyMember
{
    private readonly JsonNode? _args, _result;
    public LegacyMethodMember(JsonNode? args = null, JsonNode? result = null) { _args = args?.DeepClone(); _result = result?.DeepClone(); }
    public JsonNode? Args => _args?.DeepClone();
    public JsonNode? Result => _result?.DeepClone();
    internal JsonNode? RawArgs => _args;
    internal JsonNode? RawResult => _result;
    internal override JsonObject Describe()
    {
        var value = new JsonObject { ["kind"] = "method" };
        if (_args is not null) value["args"] = Args;
        if (_result is not null) value["result"] = Result;
        return value;
    }
}
public sealed class LegacyOpaqueMember : LegacyMember
{
    public LegacyOpaqueMember(string id) { if (!System.Text.RegularExpressions.Regex.IsMatch(id, @"^[^@\s]+@\d+$")) throw new ArgumentException("opaque id must be name@version", nameof(id)); Id = id; }
    public string Id { get; }
    internal override JsonObject Describe() => new() { ["kind"] = "opaque", ["id"] = Id };
}

/// <summary>The independent legacy contract token. Its description and hash intentionally differ from Chord/next.</summary>
public sealed class LegacyContract
{
    private static readonly HashSet<string> Reserved = ["variant", "status", "ready", "shape", "error", "onShape", "onStatus", "release", "then", "contract"];
    private readonly JsonNode _input;
    private JsonObject _description;
    private LegacyContract(string name, int version, JsonNode input, LegacyPlacement? placement, IReadOnlyDictionary<string, IReadOnlyDictionary<string, LegacyMember>> variants)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (version < 1) throw new ArgumentException("contract version must be a positive integer", nameof(version));
        Name = name; Version = version; _input = input.DeepClone(); Placement = placement;
        var variantCopy = new Dictionary<string, IReadOnlyDictionary<string, LegacyMember>>(StringComparer.Ordinal);
        var described = new JsonObject();
        foreach (var (variant, members) in variants)
        {
            var copy = new Dictionary<string, LegacyMember>(StringComparer.Ordinal); var description = new JsonObject();
            foreach (var (key, member) in members)
            {
                if (Reserved.Contains(key)) throw new ArgumentException("member name is reserved: " + key);
                if (placement is not null && member is LegacyOpaqueMember) throw new ArgumentException("remote contracts cannot expose opaque members: " + key);
                copy.Add(key, member); description[key] = member.Describe();
            }
            variantCopy.Add(variant, new ReadOnlyDictionary<string, LegacyMember>(copy)); described[variant] = description;
        }
        if (placement is not null && variantCopy.Count == 0) throw new ArgumentException("remote contract has no variants");
        Variants = new ReadOnlyDictionary<string, IReadOnlyDictionary<string, LegacyMember>>(variantCopy);
        _description = new JsonObject { ["name"] = Name, ["scope"] = Scope, ["version"] = Version, ["input"] = Input };
        if (placement is not null) _description["host"] = placement.Describe();
        _description["variants"] = described;
        Fingerprint = LegacySchema.Hash64(LegacySchema.CanonicalJson(_description));
    }
    public string Name { get; }
    public int Version { get; }
    public string Scope => Placement is null ? "local" : "remote";
    public JsonNode Input => _input.DeepClone();
    internal JsonNode RawInput => _input;
    public LegacyPlacement? Placement { get; }
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, LegacyMember>> Variants { get; }
    public JsonObject Description => (JsonObject)_description.DeepClone();
    public string Fingerprint { get; private set; }
    public static LegacyContract Local(string name, int version, JsonNode input, IReadOnlyDictionary<string, LegacyMember>? members = null) =>
        new(name, version, input, null, new Dictionary<string, IReadOnlyDictionary<string, LegacyMember>> { [""] = members ?? new Dictionary<string, LegacyMember>() });
    public static LegacyContract Remote(string name, int version, JsonNode input, LegacyPlacement host, IReadOnlyDictionary<string, IReadOnlyDictionary<string, LegacyMember>> variants) =>
        new(name, version, input, host, variants);
    public static LegacyContract FromDescription(JsonObject description)
    {
        try
        {
            LegacySchema.Assert(LegacyProtocol.DescriptionSchema, description, "token", "contract token");
            var scope = description["scope"]!.GetValue<string>();
            LegacyPlacement? placement = description["host"] is JsonObject host ? host["name"] is not null ? LegacyPlacement.Named(host["name"]!.GetValue<string>()) : LegacyPlacement.Family(host["prefix"]!.GetValue<string>(), host["key"]!.GetValue<string>()) : null;
            if ((scope == "remote") != (placement is not null) || scope is not ("local" or "remote")) throw new ArgumentException();
            var variants = new Dictionary<string, IReadOnlyDictionary<string, LegacyMember>>();
            foreach (var (name, value) in description["variants"]!.AsObject())
            {
                var members = new Dictionary<string, LegacyMember>();
                foreach (var (key, member) in value!.AsObject()) members[key] = member!["kind"]!.GetValue<string>() switch
                {
                    "state" => LegacyMember.State(member["schema"]!),
                    "method" => LegacyMember.Method(member["args"], member["result"]),
                    "opaque" => LegacyMember.Opaque(member["id"]!.GetValue<string>()),
                    _ => throw new ArgumentException(),
                };
                variants[name] = members;
            }
            var contract = new LegacyContract(description["name"]!.GetValue<string>(), description["version"]!.GetValue<int>(), description["input"]!, placement, variants);
            // The wire description is open; extension fields participate in the original token's fingerprint.
            contract._description = (JsonObject)description.DeepClone(); contract.Fingerprint = LegacySchema.Hash64(LegacySchema.CanonicalJson(description));
            return contract;
        }
        catch (Exception error) when (error is not ChordException) { throw new ChordException("invalid", "malformed contract token", "token", cause: error); }
    }
    public static string? Coverage(LegacyContract provider, LegacyContract consumer)
    {
        if (provider.Name != consumer.Name || provider.Scope != consumer.Scope) return "different type";
        if (!LegacySchema.Equal(provider.Placement?.Describe(), consumer.Placement?.Describe())) return "host";
        if (!LegacySchema.Fits(consumer._input, provider._input)) return "input";
        foreach (var (variant, wantedMembers) in consumer.Variants)
        {
            if (!provider.Variants.TryGetValue(variant, out var offeredMembers)) return $"variant {variant} missing";
            foreach (var (name, wanted) in wantedMembers)
            {
                if (!offeredMembers.TryGetValue(name, out var offered) || offered.GetType() != wanted.GetType()) return $"{variant}.{name} missing or of another kind";
                if (offered is LegacyMethodMember om && wanted is LegacyMethodMember wm)
                {
                    if ((wm.RawArgs is null) != (om.RawArgs is null) || wm.RawArgs is not null && !LegacySchema.Fits(wm.RawArgs, om.RawArgs)) return $"{variant}.{name} arguments";
                    if (wm.RawResult is not null && (om.RawResult is null || !LegacySchema.Fits(om.RawResult, wm.RawResult))) return $"{variant}.{name} result";
                }
                else if (offered is LegacyStateMember os && wanted is LegacyStateMember ws && !LegacySchema.Fits(os.RawSchema, ws.RawSchema)) return $"{variant}.{name} state";
                else if (offered is LegacyOpaqueMember oo && wanted is LegacyOpaqueMember wo && oo.Id != wo.Id) return $"{variant}.{name} opaque {oo.Id} != {wo.Id}";
            }
        }
        return null;
    }
    internal static LegacyAdapter? Bind(LegacyContract provider, IReadOnlyDictionary<int, LegacyAdapter>? accepts, LegacyContract consumer)
    {
        if (provider.Fingerprint == consumer.Fingerprint) return null;
        if (!LegacySchema.Equal(provider.Placement?.Describe(), consumer.Placement?.Describe())) throw new ChordException("contract_mismatch", $"{provider.Name} changed host placement");
        if (provider.Version == consumer.Version)
        {
            var reason = Coverage(provider, consumer);
            if (reason is null) return null;
            throw new ChordException("contract_mismatch", $"{provider.Name}@{provider.Version} does not cover the consumer ({reason}); forgotten version bump?");
        }
        if (accepts?.TryGetValue(consumer.Version, out var adapter) == true) return adapter;
        throw new ChordException("contract_mismatch", $"{provider.Name}: provider version {provider.Version}, consumer version {consumer.Version}, no accepts adapter");
    }
}
public delegate LegacyImplementation LegacyAdapter(LegacyImplementation current);
