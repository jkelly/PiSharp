// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/codemode/src/runtime/prelude-source.ts.
using System.Security.Cryptography;
using System.Text;

namespace PiSharp.Codemode;

/// <summary>The upstream prelude, evaluated in the engine before the script runs. The embedded resource is upstream's
/// <c>PRELUDE_SOURCE</c> template evaluated (its limit constants substituted), unchanged below one attribution line.</summary>
internal static class CodemodePrelude
{
    /// <summary>SHA-256 of the evaluated upstream PRELUDE_SOURCE (UTF-8, LF line endings).</summary>
    public const string UpstreamSha256 = "224cd74082a03a57105af78e1fe205bda692f096252ff4202121016dd78f6228";

    private static readonly Lazy<string> Body = new(() =>
    {
        using var stream = typeof(CodemodePrelude).Assembly.GetManifestResourceStream("PiSharp.Codemode.codemode-prelude.js")
            ?? throw new InvalidOperationException("Codemode prelude resource is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var text = reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
        var body = text[(text.IndexOf('\n') + 1)..];
        if (Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body))) != UpstreamSha256)
            throw new InvalidOperationException("Codemode prelude differs from the pinned upstream source.");
        return body;
    });

    /// <summary>The prelude as evaluated: the attribution line is dropped, so the upstream source runs byte for byte.</summary>
    public static string Source => Body.Value;
}
