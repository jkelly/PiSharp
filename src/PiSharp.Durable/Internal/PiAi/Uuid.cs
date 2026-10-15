// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/ai/src/utils/uuid.ts (uuidv7). Upstream generates RFC 9562 version 7 UUIDs: a
// 48-bit millisecond timestamp, version 7, variant 10, and random bits seeded once and then incremented as a 42-bit sequence, so IDs are
// strictly increasing within a process. Guid.CreateVersion7 writes the same layout with fresh random bits per call; nothing in pi-durable
// depends on monotonicity within one millisecond (the provider session ID is created once per conversation).
namespace PiSharp.Durable.Internal.PiAi;

/// <summary>UUIDv7 generation (upstream <c>utils/uuid.ts</c>).</summary>
internal static class PiAiUuid
{
    /// <summary>A UUIDv7 in lowercase hyphenated form (upstream <c>uuidv7()</c>).</summary>
    public static string UuidV7() => Guid.CreateVersion7().ToString("D");
}
