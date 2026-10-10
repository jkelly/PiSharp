// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/session-manager.ts.
using PiSharp.Contracts.Compatibility;

namespace PiSharp.Sessions.Serialization;

/// <summary>session-manager.ts writes every record as <c>JSON.stringify(entry)</c>: raw non-ASCII, only control characters and lone
/// surrogates escaped, JavaScript number text. A record re-read through the codec keeps exactly that text.</summary>
internal static class SessionJavaScriptJson
{
    private static readonly EcmaScriptJsonProjectionOptions Unbounded = new(MaximumInputCharacters: int.MaxValue, MaximumInputBytes: int.MaxValue,
        MaximumOutputCharacters: int.MaxValue, MaximumOutputBytes: int.MaxValue, MaximumDepth: 64, MaximumNodes: int.MaxValue,
        MaximumPropertiesPerObject: int.MaxValue, MaximumNumbers: int.MaxValue, MaximumNumberCharacters: 16_384,
        MaximumTotalNumberCharacters: int.MaxValue, MaximumStringCharacters: int.MaxValue);

    /// <summary>JSON.stringify(JSON.parse(record)), parsed back under the codec's own bounds.</summary>
    /// <exception cref="EcmaScriptJsonProjectionException">The record cannot be projected.</exception>
    /// <exception cref="SessionEntryCodecException">The projected record exceeds the codec's bounds.</exception>
    internal static SessionEntry Stringify(SessionEntryCodec codec, SessionEntry entry) =>
        codec.Parse(EcmaScriptJsonProjection.Project(entry.WireBody, Unbounded));
}
