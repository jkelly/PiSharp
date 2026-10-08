using System.Collections.Immutable;
using PiSharp.Contracts;

namespace PiSharp.Extensions.Mcp.Authentication;

/// <summary>Callback-lifetime refresh authentication proposal. The admitted custom callback replaces
/// default Basic/Post/none; it sees base headers and refresh/resource form, never default credentials.
/// Header methods represent HTTP token/byte-string validation and comma-space Append. Form methods
/// preserve ordered duplicate pairs, USVString normalization and first-position Set. Snapshot/retirement
/// after the callback original joins refuses saved references. No endpoint/store/credential authority.</summary>
public interface IMcpOAuthRefreshClientAuthentication
{
    Uri Endpoint { get; }
    McpOAuthAdmittedClient Client { get; }
    JsonData? Metadata { get; }
    string? HeaderGet(string name);
    void HeaderSet(string name, string value);
    void HeaderAppend(string name, string value);
    void HeaderDelete(string name);
    string? FormGet(string name);
    ImmutableArray<string> FormGetAll(string name);
    void FormSet(string name, string value);
    void FormAppend(string name, string value);
    void FormDelete(string name);
}

/// <summary>Explicit borrowed callback. Its original ValueTask is materialized once and directly joined.
/// After-await cancellation capable of normal Register callbacks MUST use the adapter's required
/// McpOAuthCancellationAdmission and directly join its actual returned task before returning.</summary>
public delegate ValueTask McpAdmittedOAuthRefreshClientAuthentication(IMcpOAuthRefreshClientAuthentication proposal, CancellationToken token);
