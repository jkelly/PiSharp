// highlight.js 10.7.3 (BSD-3-Clause): lib/core.js (HLJS: registerLanguage, getLanguage, highlight, _highlight, highlightAuto, TokenTreeEmitter, HTMLRenderer) — ported for Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/utils/syntax-highlight.ts.
using System.Text;

namespace PiSharp.Cli.Interactive.Mode;

/// <summary>The grammar callbacks hand-ported from hljs core and the language files (ids assigned by the extractor).</summary>
internal sealed class HljsCallbacks(HljsGrammarData data)
{
    public HljsModeCallback? Resolve(object? value) => value switch
    {
        null or HljsUndefined => null,
        HljsFunctionRef c => c.Id switch
        {
            "skipIfhasPrecedingDot" => SkipIfHasPrecedingDot,
            "shebang" => Shebang,
            "endSameAsBegin.begin" => EndSameAsBeginBegin,
            "endSameAsBegin.end" => EndSameAsBeginEnd,
            "javascript.isTrulyOpeningTag" => IsTrulyOpeningTag,
            "mathematica.systemSymbol" => MathematicaSystemSymbol,
            _ => throw new InvalidOperationException($"hljs: unknown callback '{c.Id}'"),
        },
        _ => throw new InvalidOperationException("hljs: callback expected"),
    };

    // core.js skipIfhasPrecedingDot (beginKeywords)
    static void SkipIfHasPrecedingDot(HljsMatch m, HljsResponse r)
    {
        if (m.Index > 0 && m.Input[m.Index - 1] == '.') r.IgnoreMatch();
    }

    // core.js SHEBANG "on:begin"
    static void Shebang(HljsMatch m, HljsResponse r)
    {
        if (m.Index != 0) r.IgnoreMatch();
    }

    // core.js END_SAME_AS_BEGIN
    static void EndSameAsBeginBegin(HljsMatch m, HljsResponse r) => r.Data["_beginMatch"] = m[1];

    static void EndSameAsBeginEnd(HljsMatch m, HljsResponse r)
    {
        r.Data.TryGetValue("_beginMatch", out var begin);
        if (!Equals(begin, m[1])) r.IgnoreMatch();
    }

    // languages/javascript.js + typescript.js XML_TAG.isTrulyOpeningTag / hasClosingTag
    static void IsTrulyOpeningTag(HljsMatch m, HljsResponse r)
    {
        var afterMatchIndex = m.Lexeme.Length + m.Index;
        var nextChar = afterMatchIndex < m.Input.Length ? m.Input[afterMatchIndex] : '\uFFFF';
        if (afterMatchIndex < m.Input.Length && nextChar == '<')
        {
            r.IgnoreMatch();
            return;
        }
        if (afterMatchIndex < m.Input.Length && nextChar == '>')
        {
            var tag = "</" + m.Lexeme[1..];
            if (m.Input.IndexOf(tag, afterMatchIndex, StringComparison.Ordinal) == -1) r.IgnoreMatch();
        }
    }

    // languages/mathematica.js: if (!SYSTEM_SYMBOLS_SET.has(match[0])) response.ignoreMatch();
    void MathematicaSystemSymbol(HljsMatch m, HljsResponse r)
    {
        if (!data.MathematicaSystemSymbols.Contains(m.Lexeme)) r.IgnoreMatch();
    }
}

/// <summary>hljs TokenTree node.</summary>
internal sealed class HljsTokenNode
{
    public string? Kind;
    public bool Sublanguage;
    public readonly List<object> Children = [];
}

/// <summary>hljs TokenTreeEmitter + HTMLRenderer.</summary>
internal sealed class HljsEmitter
{
    const string ClassPrefix = "hljs-";
    readonly List<HljsTokenNode> stack;

    public HljsEmitter() => stack = [Root];

    public HljsTokenNode Root { get; } = new();
    HljsTokenNode Top => stack[^1];

    public void AddKeyword(string text, string kind)
    {
        if (text.Length == 0) return;
        OpenNode(kind);
        AddText(text);
        CloseNode();
    }

    public void AddText(string text)
    {
        if (text.Length == 0) return;
        Top.Children.Add(text);
    }

    public void AddSublanguage(HljsEmitter emitter, string? name)
    {
        var node = emitter.Root;
        node.Kind = name;
        node.Sublanguage = true;
        Top.Children.Add(node);
    }

    public void OpenNode(string kind)
    {
        var node = new HljsTokenNode { Kind = kind };
        Top.Children.Add(node);
        stack.Add(node);
    }

    public void CloseNode()
    {
        if (stack.Count > 1) stack.RemoveAt(stack.Count - 1);
    }

    public void CloseAllNodes()
    {
        while (stack.Count > 1) stack.RemoveAt(stack.Count - 1);
    }

    public string ToHtml()
    {
        var sb = new StringBuilder();
        Walk(sb, Root);
        return sb.ToString();
    }

    static void Walk(StringBuilder sb, HljsTokenNode node)
    {
        var wrap = !string.IsNullOrEmpty(node.Kind);
        if (wrap) sb.Append("<span class=\"").Append(node.Sublanguage ? "" : ClassPrefix).Append(node.Kind).Append("\">");
        foreach (var child in node.Children)
        {
            if (child is string text) HljsEngine.EscapeHtml(sb, text);
            else Walk(sb, (HljsTokenNode)child);
        }
        if (wrap) sb.Append("</span>");
    }
}

/// <summary>hljs HighlightResult.</summary>
internal sealed class HljsResult
{
    public double Relevance;
    public required string Value;
    public string? Language;
    public bool Illegal;
    public required HljsEmitter Emitter;
    public HljsEngine.Frame? Top;
    public Exception? ErrorRaised;
}

/// <summary>An hljs instance (the object exported by <c>lib/core.js</c>): language registry plus the parser.</summary>
internal sealed class HljsEngine
{
    sealed class Registry
    {
        public readonly Dictionary<string, HljsLanguage> Languages = new(StringComparer.Ordinal);
        public readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal);
        public readonly List<string> Order = []; // Object.keys(languages): first-registration order
    }

    /// <summary>A mode on the parse stack (JS: <c>Object.create(mode, {parent})</c>); the root frame has no parent.</summary>
    internal sealed class Frame(HljsCompiledMode mode, Frame? parent)
    {
        public readonly HljsCompiledMode Mode = mode;
        public readonly Frame? Parent = parent;
    }

    sealed class IllegalException(string message) : Exception(message);

    /// <summary>Mutable per-highlight state hljs keeps on compiled modes (<c>mode.data</c>, <c>endRe</c> rewrites).</summary>
    sealed class Context
    {
        public readonly Dictionary<HljsCompiledMode, Dictionary<string, object?>> Data = new(ReferenceEqualityComparer.Instance);
        public readonly Dictionary<HljsCompiledMode, HljsPattern?> EndRe = new(ReferenceEqualityComparer.Instance);
        public Dictionary<string, object?> DataOf(HljsCompiledMode mode) => Data.TryGetValue(mode, out var d) ? d : Data[mode] = [];
        public HljsPattern? EndReOf(HljsCompiledMode mode) => EndRe.TryGetValue(mode, out var r) ? r : mode.EndRe;
    }

    readonly HljsGrammarData data;
    readonly HljsCallbacks callbacks;
    readonly Lock registerLock = new();
    volatile Registry registry = new();
    bool allLoaded;

    public HljsEngine(HljsGrammarData data)
    {
        this.data = data;
        callbacks = new HljsCallbacks(data);
    }

    /// <summary>A new instance with Pi's 21 eager languages registered.</summary>
    public static HljsEngine CreateWithEagerLanguages(HljsGrammarData? data = null)
    {
        var engine = new HljsEngine(data ?? HljsGrammarData.Default);
        engine.RegisterLanguages(engine.data.EagerOrder);
        return engine;
    }

    /// <summary>Pi's <c>import("highlight.js/lib/index.js")</c>: registers every language in index.js order (once).</summary>
    public void LoadAllLanguages()
    {
        lock (registerLock)
        {
            if (allLoaded) return;
            RegisterLanguages(data.IndexOrder);
            allLoaded = true;
        }
    }

    public void RegisterLanguages(IEnumerable<string> names)
    {
        lock (registerLock)
        {
            var old = registry;
            var next = new Registry();
            foreach (var (k, v) in old.Languages) next.Languages[k] = v;
            foreach (var (k, v) in old.Aliases) next.Aliases[k] = v;
            next.Order.AddRange(old.Order);
            foreach (var name in names) Register(next, name);
            registry = next;
        }
    }

    void Register(Registry reg, string languageName)
    {
        var raw = data.Instantiate(languageName);
        if (!HljsJs.Truthy(raw["name"])) raw["name"] = languageName;
        if (!reg.Languages.ContainsKey(languageName)) reg.Order.Add(languageName);
        reg.Languages[languageName] = new HljsLanguage(raw, callbacks);
        var aliases = raw["aliases"];
        if (!HljsJs.Truthy(aliases)) return;
        IEnumerable<object?> list = aliases is string one ? [one] : (List<object?>)aliases!;
        foreach (var alias in list) reg.Aliases[((string)alias!).ToLowerInvariant()] = languageName;
    }

    public IReadOnlyList<string> ListLanguages() => registry.Order;

    public HljsLanguage? GetLanguage(string? name) => GetLanguage(registry, name);

    static HljsLanguage? GetLanguage(Registry reg, string? name)
    {
        name = (name ?? "").ToLowerInvariant();
        if (reg.Languages.TryGetValue(name, out var lang)) return lang;
        return reg.Aliases.TryGetValue(name, out var target) && reg.Languages.TryGetValue(target, out lang) ? lang : null;
    }

    /// <summary>hljs <c>highlight(code, { language, ignoreIllegals })</c>. Throws for an unknown language like hljs.</summary>
    public HljsResult Highlight(string code, string languageName, bool ignoreIllegals) =>
        new Run(this, registry, new Context(), languageName, code, ignoreIllegals, null).Execute();

    /// <summary>hljs <c>highlightAuto(code, languageSubset)</c>.</summary>
    public HljsResult HighlightAuto(string code, IReadOnlyList<string>? languageSubset = null) =>
        HighlightAuto(registry, new Context(), code, languageSubset);

    HljsResult HighlightAuto(Registry reg, Context ctx, string code, IReadOnlyList<string>? languageSubset)
    {
        var subset = languageSubset ?? reg.Order;
        var plaintext = JustTextHighlightResult(code);
        var results = new List<HljsResult> { plaintext };
        foreach (var name in subset)
        {
            var lang = GetLanguage(reg, name);
            if (lang == null || lang.DisableAutodetect) continue;
            results.Add(new Run(this, reg, ctx, name, code, false, null).Execute());
        }
        var arr = results.ToArray();
        HljsTimSort.Sort(arr, (a, b) =>
        {
            if (a.Relevance != b.Relevance) return b.Relevance - a.Relevance;
            if (a.Language != null && b.Language != null)
            {
                if (GetLanguage(reg, a.Language)!.SupersetOf is string sa && sa == b.Language) return 1;
                if (GetLanguage(reg, b.Language)!.SupersetOf is string sb && sb == a.Language) return -1;
            }
            return 0;
        });
        return arr[0];
    }

    static HljsResult JustTextHighlightResult(string code)
    {
        var result = new HljsResult { Value = EscapeHtml(code), Emitter = new HljsEmitter() };
        result.Emitter.AddText(code);
        return result;
    }

    public static string EscapeHtml(string value)
    {
        var sb = new StringBuilder(value.Length + 16);
        EscapeHtml(sb, value);
        return sb.ToString();
    }

    public static void EscapeHtml(StringBuilder sb, string value)
    {
        foreach (var c in value)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&#x27;"); break;
                default: sb.Append(c); break;
            }
        }
    }

    /// <summary>One <c>_highlight</c> invocation (the closure state of hljs' <c>_highlight</c>).</summary>
    sealed class Run(HljsEngine engine, Registry reg, Context ctx, string languageName, string code, bool ignoreIllegals, Frame? continuation)
    {
        static readonly object NoMatch = new();

        HljsLanguage language = null!;
        Frame top = null!;
        readonly Dictionary<string, Frame?> continuations = new(StringComparer.Ordinal);
        readonly HljsEmitter emitter = new();
        readonly StringBuilder modeBuffer = new();
        double relevance;
        int index;
        int iterations;
        bool resumeScanAtSamePosition;
        int regexIndex;
        HljsMatch? lastMatch;

        public HljsResult Execute()
        {
            language = GetLanguage(reg, languageName) ?? throw new ArgumentException($"Unknown language: \"{languageName}\"");
            var md = language.Compile();
            top = continuation ?? new Frame(md, null);
            ProcessContinuations();
            try
            {
                regexIndex = 0;
                for (; ; )
                {
                    iterations++;
                    if (resumeScanAtSamePosition) resumeScanAtSamePosition = false;
                    else regexIndex = 0;
                    var matcher = top.Mode.Matcher ?? throw new InvalidOperationException("Cannot read properties of undefined (reading 'exec')");
                    var match = matcher.Exec(code, index, ref regexIndex);
                    if (match == null) break;
                    var beforeMatch = JsSubstring(code, index, match.Index);
                    var processedCount = ProcessLexeme(beforeMatch, match);
                    index = match.Index + processedCount;
                }
                ProcessLexeme(JsSubstr(code, index), null);
                emitter.CloseAllNodes();
                return new HljsResult { Relevance = Math.Floor(relevance), Value = emitter.ToHtml(), Language = languageName, Emitter = emitter, Top = top };
            }
            catch (IllegalException)
            {
                return new HljsResult { Illegal = true, Relevance = 0, Value = EscapeHtml(code), Emitter = emitter };
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return new HljsResult { Relevance = 0, Value = EscapeHtml(code), Emitter = emitter, Language = languageName, Top = top, ErrorRaised = ex };
            }
        }

        static string JsSubstring(string s, int start, int end)
        {
            start = Math.Clamp(start, 0, s.Length);
            end = Math.Clamp(end, 0, s.Length);
            if (start > end) (start, end) = (end, start);
            return s.Substring(start, end - start);
        }

        static string JsSubstr(string s, int start) => start >= s.Length ? "" : s[Math.Max(0, start)..];

        void ProcessKeywords()
        {
            var mode = top.Mode;
            var buffer = modeBuffer.ToString();
            if (mode.Keywords == null)
            {
                emitter.AddText(buffer);
                return;
            }
            var re = (mode.KeywordPattern ?? throw new InvalidOperationException("keywordPatternRe undefined")).Search;
            var lastIndex = 0;
            var match = re.Match(buffer, 0);
            var buf = new StringBuilder();
            while (match.Success)
            {
                buf.Append(buffer, lastIndex, Math.Max(0, match.Index - lastIndex));
                var text = match.Value;
                var key = language.CaseInsensitive ? text.ToLowerInvariant() : text;
                if (mode.Keywords.TryGetValue(key, out var kw))
                {
                    emitter.AddText(buf.ToString());
                    buf.Clear();
                    relevance += kw.Relevance;
                    if (kw.Kind.StartsWith('_')) buf.Append(text);
                    else emitter.AddKeyword(text, language.AliasOf(kw.Kind));
                }
                else buf.Append(text);
                // A global regex whose match is empty does not advance lastIndex: hljs would spin forever here.
                if (match.Length == 0) throw new InvalidOperationException("hljs: empty keyword match (infinite loop in hljs)");
                lastIndex = match.Index + match.Length;
                match = re.Match(buffer, lastIndex);
            }
            buf.Append(JsSubstr(buffer, lastIndex));
            emitter.AddText(buf.ToString());
        }

        void ProcessSubLanguage()
        {
            var mode = top.Mode;
            if (modeBuffer.Length == 0) return;
            var buffer = modeBuffer.ToString();
            HljsResult result;
            if (mode.SubLanguageName is { } sub)
            {
                if (!reg.Languages.ContainsKey(sub))
                {
                    emitter.AddText(buffer);
                    return;
                }
                continuations.TryGetValue(sub, out var cont);
                result = new Run(engine, reg, ctx, sub, buffer, true, cont).Execute();
                continuations[sub] = result.Top;
            }
            else
            {
                var list = mode.SubLanguageList!;
                result = engine.HighlightAuto(reg, ctx, buffer, list.Length > 0 ? list : null);
            }
            if (mode.Relevance > 0) relevance += result.Relevance;
            emitter.AddSublanguage(result.Emitter, result.Language);
        }

        void ProcessBuffer()
        {
            if (top.Mode.HasSubLanguage) ProcessSubLanguage();
            else ProcessKeywords();
            modeBuffer.Clear();
        }

        void StartNewMode(HljsCompiledMode mode)
        {
            if (mode.OpenName != null) emitter.OpenNode(mode.OpenName);
            top = new Frame(mode, top);
        }

        Frame? EndOfMode(Frame? frame, HljsMatch match)
        {
            if (frame == null) throw new InvalidOperationException("Cannot read properties of undefined (reading 'endRe')");
            var mode = frame.Mode;
            var matched = ctx.EndReOf(mode) is { } endRe && endRe.Anchored.Match(code, match.Index, code.Length - match.Index).Success;
            if (matched)
            {
                if (mode.OnEnd != null)
                {
                    var resp = new HljsResponse(ctx.DataOf(mode));
                    mode.OnEnd(match, resp);
                    if (resp.IsMatchIgnored) matched = false;
                }
                if (matched)
                {
                    while (frame.Mode.EndsParent && frame.Parent != null) frame = frame.Parent;
                    return frame;
                }
            }
            return mode.EndsWithParent ? EndOfMode(frame.Parent, match) : null;
        }

        int DoIgnore(string lexeme)
        {
            if (regexIndex == 0)
            {
                modeBuffer.Append(lexeme.Length > 0 ? lexeme[0].ToString() : "undefined");
                return 1;
            }
            resumeScanAtSamePosition = true;
            return 0;
        }

        int DoBeginMatch(HljsMatch match)
        {
            var lexeme = match.Lexeme;
            var newMode = match.Rule ?? throw new InvalidOperationException("hljs: begin rule without mode");
            var resp = new HljsResponse(ctx.DataOf(newMode));
            foreach (var cb in (ReadOnlySpan<HljsModeCallback?>)[newMode.BeforeBegin, newMode.OnBegin])
            {
                if (cb == null) continue;
                cb(match, resp);
                if (resp.IsMatchIgnored) return DoIgnore(lexeme);
            }
            if (newMode.EndSameAsBegin) ctx.EndRe[newMode] = new HljsPattern(HljsRegex.EscapeSource(lexeme), false);
            if (newMode.Skip) modeBuffer.Append(lexeme);
            else
            {
                if (newMode.ExcludeBegin) modeBuffer.Append(lexeme);
                ProcessBuffer();
                if (!newMode.ReturnBegin && !newMode.ExcludeBegin) modeBuffer.Clear().Append(lexeme);
            }
            StartNewMode(newMode);
            return newMode.ReturnBegin ? 0 : lexeme.Length;
        }

        object DoEndMatch(HljsMatch match)
        {
            var lexeme = match.Lexeme;
            var endMode = EndOfMode(top, match);
            if (endMode == null) return NoMatch;
            var origin = top.Mode;
            if (origin.Skip) modeBuffer.Append(lexeme);
            else
            {
                if (!(origin.ReturnEnd || origin.ExcludeEnd)) modeBuffer.Append(lexeme);
                ProcessBuffer();
                if (origin.ExcludeEnd) modeBuffer.Clear().Append(lexeme);
            }
            do
            {
                if (top.Mode.ClassName != null) emitter.CloseNode();
                if (!top.Mode.Skip && !top.Mode.SubLanguageTruthy) relevance += top.Mode.Relevance;
                top = top.Parent ?? throw new InvalidOperationException("Cannot read properties of undefined (reading 'className')");
            } while (top != endMode.Parent);
            if (endMode.Mode.Starts is { } starts)
            {
                if (endMode.Mode.EndSameAsBegin) ctx.EndRe[starts] = ctx.EndReOf(endMode.Mode);
                StartNewMode(starts);
            }
            return origin.ReturnEnd ? 0 : lexeme.Length;
        }

        void ProcessContinuations()
        {
            var list = new List<string>();
            for (var current = top; current.Parent != null; current = current.Parent)
                if (current.Mode.ClassName != null) list.Insert(0, current.Mode.ClassName);
            foreach (var item in list) emitter.OpenNode(item);
        }

        int ProcessLexeme(string textBeforeMatch, HljsMatch? match)
        {
            modeBuffer.Append(textBeforeMatch);
            if (match == null)
            {
                ProcessBuffer();
                return 0;
            }
            var lexeme = match.Lexeme;
            if (lastMatch is { Type: HljsRuleType.Begin } && match.Type == HljsRuleType.End && lastMatch.Index == match.Index && lexeme.Length == 0)
            {
                modeBuffer.Append(JsSubstring(code, match.Index, match.Index + 1));
                return 1;
            }
            lastMatch = match;
            if (match.Type == HljsRuleType.Begin) return DoBeginMatch(match);
            if (match.Type == HljsRuleType.Illegal && !ignoreIllegals)
                throw new IllegalException($"Illegal lexeme \"{lexeme}\" for mode \"{top.Mode.ClassName ?? "<unnamed>"}\"");
            if (match.Type == HljsRuleType.End && DoEndMatch(match) is int processed) return processed;
            if (match.Type == HljsRuleType.Illegal && lexeme.Length == 0) return 1;
            if (iterations > 100000 && iterations > match.Index * 3)
                throw new InvalidOperationException("potential infinite loop, way more iterations than matches");
            modeBuffer.Append(lexeme);
            return lexeme.Length;
        }
    }
}
