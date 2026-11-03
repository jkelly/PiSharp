// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/ai/src/utils/validation.ts.
namespace PiSharp.Contracts;

/// <summary>How a tool's parameter schema was produced upstream. Up to Pi v1.2.0 it decided how validateToolArguments converted and
/// coerced arguments; since Pi v1.5.0 every schema is validated and coerced the same way (pi-schema, see
/// PiSharp.Agent.ToolArgumentValidation), so the origin no longer changes a result.</summary>
public enum ToolSchemaOrigin
{
    /// <summary>Plain JSON schema (MCP tools, extensions passing raw objects).</summary>
    JsonSchema,

    /// <summary>Built with TypeBox 1.x (Pi built-in tools, Type.* extension tools). Its "~kind" annotations are ignored.</summary>
    TypeBox,

    /// <summary>Schema object carrying Symbol.for("TypeBox.Kind") (TypeBox 0.x).</summary>
    LegacyTypeBoxKindSymbol,
}
