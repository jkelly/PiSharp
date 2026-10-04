using System.Text;
using PiSharp.Contracts;

namespace PiSharp.Rpc;

/// <summary>The existing strict JSONL encoder's bounded, token-preserving LF record for borrowed text output.</summary>
public static class JsonlRecordFormatter
{
    public static string Format(JsonData record, JsonlTransportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        var configured = options ?? new(); configured.Validate(JsonlStreamOwnership.Borrowed);
        return Encoding.UTF8.GetString(JsonlRecordCodec.Encode(record, configured));
    }
}
