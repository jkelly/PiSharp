// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/data/validator.ts
//
// Compiled checks: pi-schema validators of Chord-owned schemas, and the `invalid` error of a failed check. pi-schema is
// PiSharp.Contracts.Schema; a value is a JsonNode tree whose byte leaves are JsonValues holding a byte[] (Args.cs), and only pi-schema's
// JavaScript value model holds byte arrays, so a Validator checks a value on that model (upstream checks the value itself). A schema
// owner is any Chord-owned record holding a schema (a snapshot, a contract's params; later layers' definitions): it is the key of the weak
// cache, and its schema is passed beside it, as C# has no structural { schema } type.
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using PiSharp.Chord.Contracts;
using PiSharp.Contracts.Schema;

namespace PiSharp.Chord.Data;

/// <summary>pi-schema's compiled check of a Chord-owned schema (upstream's <c>Validator</c>): <see cref="Check"/> is the boolean test,
/// <see cref="Errors"/> explains a failure.</summary>
internal sealed class Validator
{
    internal Validator(SchemaValidator compiled) => Compiled = compiled;

    /// <summary>The compiled pi-schema check.</summary>
    internal SchemaValidator Compiled { get; }

    /// <summary>Whether <paramref name="value"/> is valid.</summary>
    internal bool Check(JsonNode? value) => Compiled.Check(DataInternal.ToSchemaValue(value));

    /// <summary>Why <paramref name="value"/> is invalid: one error per failing keyword, empty when it is valid.</summary>
    internal IReadOnlyList<JsonSchemaValidationError> Errors(JsonNode? value) => Compiled.Errors(DataInternal.ToSchemaValue(value));
}

internal static partial class DataInternal
{
    private static readonly ConditionalWeakTable<object, Validator> Validators = new();

    /// <summary>The validator of a snapshot's schema (<see cref="ValidatorFor(object, JsonNode, ChordContext)"/>).</summary>
    internal static Validator ValidatorFor(Snapshot owner, ChordContext context) => ValidatorFor(owner, owner.Schema, context);

    /// <summary>The validator of a contract's or host kind's params (<see cref="ValidatorFor(object, JsonNode, ChordContext)"/>).</summary>
    internal static Validator ValidatorFor(ParamsData owner, ChordContext context) => ValidatorFor(owner, owner.Schema, context);

    /// <summary>The validator of <paramref name="owner"/>'s schema <paramref name="schema"/>, compiled on first use (counted) and cached
    /// weakly by the owner (upstream <c>validatorFor</c>).</summary>
    internal static Validator ValidatorFor(object owner, JsonNode schema, ChordContext context)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(context);
        if (Validators.TryGetValue(owner, out var validator)) return validator;
        context.Count("chord.data.compilations");
        // `format` is an annotation (decided for the schema switch): no format checks are compiled in.
        validator = new Validator(SchemaValidator.Compile(Js.FromNode(schema), new SchemaOptions()));
        // Two first uses at once compile twice; both see the one the table keeps.
        return Validators.GetValue(owner, _ => validator);
    }

    /// <summary>Checks <paramref name="value"/> (counted); a failure throws <c>invalid/&lt;reason&gt;</c> with the first error's JSON
    /// Pointer path (upstream <c>check</c>). <paramref name="reason"/> is <c>args</c>, <c>params</c> or <c>host_address</c>.</summary>
    internal static void Check(Validator validator, JsonNode? value, string reason, ChordContext context)
    {
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(context);
        context.Count("chord.data.validations");
        var checkedValue = ToSchemaValue(value);
        if (!validator.Compiled.Check(checkedValue)) throw FailedCheck(validator, checkedValue, reason, context);
    }

    /// <summary>The error of a failed check: the first error of the validator's explanation (counted: it walks the schema). A missing field
    /// is reported at the field ("missing"), an unknown one at its own path ("unknown field"); the value itself at <c>/</c>.</summary>
    private static ChordError FailedCheck(Validator validator, object? value, string reason, ChordContext context)
    {
        context.Count("chord.data.walks");
        // A value that failed the check has at least one error.
        var error = validator.Compiled.Errors(value)[0];
        var path = error.Path is "" ? "/" : error.Path;
        return Invalid(reason, $"{reason}{(path == "/" ? "" : path)}: {error.Message}", new JsonObject { ["path"] = path });
    }

    /// <summary>A value on pi-schema's JavaScript value model: JSON as <c>Js.FromNode</c> reads it, and a JsonValue holding a byte[] as the
    /// byte array (upstream's Uint8Array).</summary>
    internal static object? ToSchemaValue(JsonNode? value)
    {
        switch (value)
        {
            case JsonObject node:
            {
                var result = new JsObject();
                foreach (var (key, item) in node) result.Set(key, ToSchemaValue(item));
                return result;
            }
            case JsonArray node:
                return new JsArray(node.Select(ToSchemaValue));
            case JsonValue node when node.TryGetValue<byte[]>(out var bytes):
                return bytes;
            default:
                return Js.FromNode(value);
        }
    }
}
