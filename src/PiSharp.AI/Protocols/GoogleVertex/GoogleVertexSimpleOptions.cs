using PiSharp.AI.Protocols.GoogleGenerativeAI;
namespace PiSharp.AI.Protocols.GoogleVertex;

/// <summary>Explicit resolved bearer and exact endpoint; project/location are caller-admitted
/// route evidence, not SDK endpoint-builder or credential-acquisition authority.</summary>
public sealed record GoogleVertexSimpleOptions(GoogleVertexOptions DirectOptions, string Project, string Location,
    string? Reasoning = null)
{
    public PiSharp.Contracts.JsonData? ThinkingBudgets { get; init; }
    public int MaximumContextMessages { get; init; } = PiRequestBudget.RequestMessages;
    public int MaximumContextCharacters { get; init; } = PiRequestBudget.RequestPayloadBytes;
    public override string ToString() => nameof(GoogleVertexSimpleOptions);
}
