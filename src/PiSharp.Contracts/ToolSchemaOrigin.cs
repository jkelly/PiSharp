// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/utils/validation.ts.
namespace PiSharp.Contracts;

/// <summary>How a tool's parameter schema was produced upstream, which decides how validateToolArguments converts and coerces
/// arguments (see PiSharp.Agent.ToolArgumentValidation).</summary>
public enum ToolSchemaOrigin
{
    /// <summary>Plain JSON schema (MCP tools, extensions passing raw objects). "~kind" annotations, if present, are still honoured.</summary>
    JsonSchema,

    /// <summary>Built with TypeBox 1.x (Pi built-in tools, Type.* extension tools). Kinds are inferred when the schema carries no "~kind" annotations.</summary>
    TypeBox,

    /// <summary>Schema object carrying Symbol.for("TypeBox.Kind") (TypeBox 0.x): coerceWithJsonSchema is skipped.</summary>
    LegacyTypeBoxKindSymbol,
}
