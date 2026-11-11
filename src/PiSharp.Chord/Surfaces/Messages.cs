// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/surfaces/messages.ts.
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json.Nodes;
using PiSharp.Chord.Contracts;

namespace PiSharp.Chord.Surfaces;

/// <summary>A catalog value with a runtime-checked signature, replacing TypeScript's inferred catalog shape.</summary>
public sealed class MessageValue
{
    private readonly string? text;
    private readonly Delegate? function;
    private readonly Type[] parameters;
    private MessageValue(string text) { ArgumentNullException.ThrowIfNull(text); this.text = text; parameters = []; }
    private MessageValue(Delegate function)
    {
        if (function.Method.ReturnType != typeof(string)) throw new ArgumentException("A message function must return string", nameof(function));
        parameters = function.Method.GetParameters().Select(p => p.ParameterType).ToArray();
        if (parameters.Any(type => type.IsByRef || type.IsPointer)) throw new ArgumentException("A message cannot take by-reference parameters", nameof(function));
        this.function = function;
    }
    public static MessageValue Function(Delegate function) => new(function);
    public static implicit operator MessageValue(string text) => new(text);
    internal bool SameSignature(MessageValue other) => (function is null) == (other.function is null) && parameters.SequenceEqual(other.parameters);
    internal string Call(IReadOnlyList<object?> arguments)
    {
        var converted = ConvertArguments(arguments);
        if (function is null) return text!;
        try { return function.DynamicInvoke(converted) as string ?? throw new InvalidOperationException("A message function returned no string"); }
        catch (TargetInvocationException error) when (error.InnerException is { } inner) { ExceptionDispatchInfo.Capture(inner).Throw(); throw; }
    }
    internal void Validate(IReadOnlyList<object?> arguments) => ConvertArguments(arguments);
    private object?[] ConvertArguments(IReadOnlyList<object?> arguments)
    {
        if (arguments.Count != parameters.Length) throw new ArgumentException($"Message expects {parameters.Length} arguments, got {arguments.Count}");
        var converted = new object?[arguments.Count];
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index]; var type = parameters[index];
            if (argument is null)
            {
                if (type.IsValueType && Nullable.GetUnderlyingType(type) is null) throw new ArgumentException($"Message argument {index} cannot be null");
            }
            else if (!type.IsInstanceOfType(argument))
            {
                var target = Nullable.GetUnderlyingType(type) ?? type;
                if (IsNumber(argument.GetType()) && IsNumber(target))
                {
                    var numeric = Convert.ToDouble(argument, System.Globalization.CultureInfo.InvariantCulture);
                    if (target != typeof(float) && target != typeof(double) && target != typeof(decimal) && numeric != Math.Truncate(numeric))
                        throw new ArgumentException($"Message argument {index} must be an integer");
                    argument = Convert.ChangeType(argument, target, System.Globalization.CultureInfo.InvariantCulture);
                }
                else throw new ArgumentException($"Message argument {index} must be {type.Name}");
            }
            converted[index] = argument;
        }
        return converted;
    }
    private static bool IsNumber(Type type) => type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort) ||
        type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong) || type == typeof(float) || type == typeof(double) || type == typeof(decimal);
}

public sealed class Catalog : Dictionary<string, MessageValue>
{
    public Catalog() : base(StringComparer.Ordinal) { }
    public Catalog(IEnumerable<KeyValuePair<string, MessageValue>> values) : base(values, StringComparer.Ordinal) { }
}

public sealed record MessageData(string Namespace, string Key, IReadOnlyList<object?> Params)
{
    public static JsonObject Schema { get; } = Schemas.Obj(("namespace", Schemas.Str()), ("key", Schemas.Str()),
        ("params", Schemas.Arr(Schemas.Union(Schemas.Str(), Schemas.Num(), Schemas.Bool(), Schemas.Nul()))));
    public JsonObject ToJson() => new() { ["namespace"] = Namespace, ["key"] = Key,
        ["params"] = new JsonArray(Params.Select(Primitive).ToArray()) };
    public static MessageData FromJson(JsonNode? value)
    {
        if (value is not JsonObject obj || obj["namespace"] is not JsonValue n || !n.TryGetValue<string>(out var ns) ||
            obj["key"] is not JsonValue k || !k.TryGetValue<string>(out var key) || obj["params"] is not JsonArray parameters)
            throw new ArgumentException("Invalid message data", nameof(value));
        return new(ns, key, parameters.Select(ReadPrimitive).ToArray());
    }
    internal static JsonNode? Primitive(object? value) => value switch
    {
        null => null, string text => JsonValue.Create(text), bool boolean => JsonValue.Create(boolean),
        byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal =>
            double.IsFinite(Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture)) ? System.Text.Json.JsonSerializer.SerializeToNode(value) :
                throw new ArgumentException("Message numbers must be finite"),
        _ => throw new ArgumentException("Message parameters must be JSON strings, numbers, booleans or null"),
    };
    private static object? ReadPrimitive(JsonNode? node) => node?.GetValueKind() switch
    {
        null or System.Text.Json.JsonValueKind.Null => null,
        System.Text.Json.JsonValueKind.String => node.GetValue<string>(),
        System.Text.Json.JsonValueKind.True => true, System.Text.Json.JsonValueKind.False => false,
        System.Text.Json.JsonValueKind.Number => double.Parse(node.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture),
        _ => throw new ArgumentException("Message parameter is not a JSON primitive"),
    };
}

public sealed class Translator
{
    private readonly Messages source;
    private readonly Func<Language?> language;
    internal Translator(Messages source, Func<Language?> language) { this.source = source; this.language = language; }
    public string Text(string key, params object?[] arguments) => source.RenderKey(language(), key, arguments);
    public string Render(MessageData message) => source.RenderData(language(), message);
}

public sealed class Messages
{
    private readonly Dictionary<string, Catalog> catalogs;
    private readonly Catalog first;
    public string Namespace { get; }
    public IReadOnlyList<Translation> Translations { get; }
    private Messages(string ns, Dictionary<string, Catalog> catalogs)
    {
        Namespace = ns; this.catalogs = catalogs; first = catalogs.First().Value;
        Translations = catalogs.Select(entry => new Translation(ns, entry.Key, entry.Value)).ToArray();
    }
    public static Messages Define(string ns, IReadOnlyDictionary<string, Catalog> catalogs)
    {
        ContractsInternal.CheckName(ns, "A messages namespace");
        if (catalogs.Count == 0) throw new ArgumentException($"{ns}: Messages.Define needs a catalog", nameof(catalogs));
        var own = new Dictionary<string, Catalog>(StringComparer.Ordinal);
        Catalog? first = null;
        foreach (var (locale, given) in catalogs)
        {
            var canonical = SurfaceTranslations.CanonicalLocale(locale) ?? throw new ArgumentException($"{ns}: '{locale}' is no language tag");
            var catalog = new Catalog(given);
            if (catalog.Any(entry => entry.Value is null)) throw new ArgumentException($"{ns}: catalog contains null values");
            if (first is not null && (first.Count != catalog.Count || first.Any(entry => !catalog.TryGetValue(entry.Key, out var next) || !entry.Value.SameSignature(next))))
                throw new ArgumentException($"{ns}: every catalog must have identical keys and message signatures");
            first ??= catalog;
            if (!own.TryAdd(canonical, catalog)) throw new ArgumentException($"{ns}: duplicate canonical locale {canonical}");
        }
        return new(ns, own);
    }
    public Translator In(Language? language) => new(this, () => language);
    public string Text(string key, params object?[] arguments) => RenderKey(Languages.Current, key, arguments);
    public string Render(MessageData message) => RenderData(Languages.Current, message);
    public MessageData Message(string key, params object?[] arguments)
    {
        if (!first.TryGetValue(key, out var value)) throw new ArgumentException($"Unknown message '{key}'");
        foreach (var argument in arguments) MessageData.Primitive(argument);
        // Runtime parity for the compile-time argument contract, without invoking a user callback.
        ValidateArguments(value, arguments);
        return new(Namespace, key, arguments.ToArray());
    }
    private static void ValidateArguments(MessageValue value, object?[] arguments)
    {
        // MessageValue validates both count and type before invoking. A signature-only helper avoids side effects here.
        value.Validate(arguments);
    }
    internal string RenderKey(Language? language, string key, IReadOnlyList<object?> arguments)
    {
        if (!first.TryGetValue(key, out var expected))
        { language?.Report(new("unknown", Namespace, key, Expected: Namespace)); return key; }
        if (language is null) return expected.Call(arguments);
        return Languages.In(language, () =>
        {
            for (var index = 0; index < language.Chain.Count; index++)
            {
                var locale = language.Chain[index];
                var contributed = language.Translations.Catalogs.GetValueOrDefault(Namespace)?.GetValueOrDefault(locale);
                if (contributed?.Value.Messages.TryGetValue(key, out var message) == true)
                {
                    try
                    {
                        if (!expected.SameSignature(message)) throw new ArgumentException("Contributed message has an incompatible signature");
                        return Found(language, key, index, message.Call(arguments));
                    }
                    catch (Exception error) { language.Report(new("failed", Namespace, key, locale, Plugin: contributed.Plugin, Error: error)); }
                }
                if (catalogs.TryGetValue(locale, out var own) && !ReferenceEquals(own, contributed?.Value.Messages) && own.TryGetValue(key, out var fallback))
                    return Found(language, key, index, fallback.Call(arguments));
            }
            return Found(language, key, language.Chain.Count, expected.Call(arguments));
        });
    }
    private string Found(Language language, string key, int index, string text)
    {
        if (index > 0) language.Report(new("fallback", Namespace, key, language.Locale,
            Used: index < language.Chain.Count ? language.Chain[index] : null));
        return text;
    }
    internal string RenderData(Language? language, MessageData message)
    {
        if (message.Namespace != Namespace || !first.ContainsKey(message.Key) || message.Params is null)
        { language?.Report(new("unknown", message.Namespace, message.Key, Expected: Namespace)); return message.Key; }
        try
        {
            foreach (var argument in message.Params) MessageData.Primitive(argument);
            return RenderKey(language, message.Key, message.Params);
        }
        catch (Exception error) { language?.Report(new("failed", Namespace, message.Key, language.Locale, Error: error)); return message.Key; }
    }
}
