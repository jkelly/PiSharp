// highlight.js 10.7.3 (BSD-3-Clause): lib/core.js, lib/index.js — ported for Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/utils/syntax-highlight.ts.
namespace PiSharp.Cli.Interactive.Mode;

/// <summary>
/// Pi's shared highlight.js instance: the 21 eagerly registered languages (python, java, go, javascript, json, cpp,
/// typescript, php, ruby, c, csharp, nix, bash, rust, scala, kotlin, swift, dart, groovy, perl, lua) on first use, and
/// the full <c>lib/index.js</c> set after <see cref="LoadAllLanguages"/>. Thread-safe.
/// </summary>
internal static class Hljs
{
    static readonly Lazy<HljsEngine> Instance = new(() => HljsEngine.CreateWithEagerLanguages(), LazyThreadSafetyMode.ExecutionAndPublication);

    internal static HljsEngine Engine => Instance.Value;

    /// <summary>syntax-highlight.ts <c>supportsLanguage</c>: <c>hljs.getLanguage(name) !== undefined</c>.</summary>
    public static bool SupportsLanguage(string name) => Engine.GetLanguage(name) != null;

    /// <summary><c>hljs.highlight(code, { language, ignoreIllegals }).value</c>. Throws <see cref="ArgumentException"/> for an
    /// unknown language (like hljs).</summary>
    public static string HighlightHtml(string code, string language, bool ignoreIllegals) =>
        Engine.Highlight(code, language, ignoreIllegals).Value;

    /// <summary><c>hljs.highlightAuto(code, languageSubset).value</c> (Pi's <c>highlight()</c> when no language is given).</summary>
    public static string HighlightAutoHtml(string code, IReadOnlyList<string>? languageSubset = null) =>
        Engine.HighlightAuto(code, languageSubset).Value;

    /// <summary>syntax-highlight.ts <c>loadAllHighlightLanguages</c>: registers every language of <c>lib/index.js</c> on the
    /// same instance (idempotent).</summary>
    public static void LoadAllLanguages() => Engine.LoadAllLanguages();
}
