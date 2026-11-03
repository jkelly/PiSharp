// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/coding-agent/src/extensions/tool-search/tool.ts (wordSegmenter, an
// Intl.Segmenter with word granularity, and its isWordLike segments).
using System.Runtime.InteropServices;

namespace PiSharp.Extensions.Mcp.Discovery;

/// <summary>
/// Word segmentation through ICU's word break iterator, the engine behind V8's <c>Intl.Segmenter</c> word granularity, so text
/// written without spaces (Chinese, Japanese, Thai) splits into dictionary words as upstream does. ICU comes from the operating
/// system: <c>icu.dll</c> (or <c>icuuc.dll</c>) in the Windows system directory, <c>libicucore</c> on macOS, the newest
/// <c>libicuuc.so.N</c> elsewhere. Without ICU each run of word characters stays one word. The rules are ICU's root locale; the ICU
/// version is the system's, not the one Node bundles.
/// </summary>
internal static class UnicodeWordSegmenter
{
    private const int WordBreak = 1; // UBRK_WORD
    private const int Done = -1; // UBRK_DONE
    private const int WordNoneLimit = 100; // UBRK_WORD_NONE_LIMIT: statuses below it are spaces and punctuation

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint OpenIterator(int type, [MarshalAs(UnmanagedType.LPStr)] string locale, nint text, int length, ref int status);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Step(nint iterator);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void CloseIterator(nint iterator);

    private sealed record Icu(OpenIterator Open, Step First, Step Next, Step RuleStatus, CloseIterator Close);

    private static readonly Lazy<Icu?> Library = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Whether ICU was found, so text without spaces is split as upstream splits it.</summary>
    internal static bool IsAvailable => Library.Value is not null;

    /// <summary>The word-like segments of the text (letters, marks and digits; no spaces), or its space-separated runs without ICU.</summary>
    internal static IEnumerable<string> WordLikeSegments(string text)
    {
        if (text.Length == 0) return [];
        if (Library.Value is not { } icu || Segment(icu, text) is not { } words) return text.Split(' ');
        return words;
    }

    private static List<string>? Segment(Icu icu, string text)
    {
        var buffer = Marshal.StringToHGlobalUni(text);
        try
        {
            var status = 0;
            var iterator = icu.Open(WordBreak, "", buffer, text.Length, ref status);
            if (status > 0 || iterator == 0) return null; // U_FAILURE
            try
            {
                var words = new List<string>();
                var start = icu.First(iterator);
                for (var end = icu.Next(iterator); end != Done; start = end, end = icu.Next(iterator))
                    if (icu.RuleStatus(iterator) >= WordNoneLimit) words.Add(text[start..end]);
                return words;
            }
            finally { icu.Close(iterator); }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static Icu? Load()
    {
        try
        {
            foreach (var (path, suffix) in Candidates())
            {
                if (!NativeLibrary.TryLoad(path, out var handle)) continue;
                var exportSuffix = suffix is null || NativeLibrary.TryGetExport(handle, "ubrk_open", out _) ? "" : suffix;
                if (Bind(handle, exportSuffix) is { } icu) return icu;
            }
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or MarshalDirectiveException)
        {
        }
        return null;
    }

    /// <summary>Library paths with the version suffix their exports may carry (ICU renames symbols to <c>ubrk_open_74</c>).</summary>
    private static IEnumerable<(string Path, string? Suffix)> Candidates()
    {
        if (OperatingSystem.IsWindows())
        {
            // The system directory only: a DLL of the same name next to the app or in the working directory must not load.
            yield return (Path.Combine(Environment.SystemDirectory, "icu.dll"), null);
            yield return (Path.Combine(Environment.SystemDirectory, "icuuc.dll"), null);
        }
        else if (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst())
            yield return ("/usr/lib/libicucore.dylib", null);
        else
            for (var version = 99; version >= 50; version--) yield return ($"libicuuc.so.{version}", "_" + version);
    }

    private static Icu? Bind(nint handle, string suffix)
    {
        T? Export<T>(string name) where T : Delegate =>
            NativeLibrary.TryGetExport(handle, name + suffix, out var address) ? Marshal.GetDelegateForFunctionPointer<T>(address) : null;
        return Export<OpenIterator>("ubrk_open") is { } open && Export<Step>("ubrk_first") is { } first && Export<Step>("ubrk_next") is { } next &&
            Export<Step>("ubrk_getRuleStatus") is { } ruleStatus && Export<CloseIterator>("ubrk_close") is { } close
            ? new(open, first, next, ruleStatus, close) : null;
    }
}
