// highlight.js 10.7.3 (BSD-3-Clause): lib/core.js (compileLanguage, compileKeywords, MultiRegex, ResumableMultiRegex, expandOrCloneMode) — ported for Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/utils/syntax-highlight.ts.
using System.Text.RegularExpressions;

namespace PiSharp.Cli.Interactive.Mode;

internal enum HljsRuleType { Begin, End, Illegal }

/// <summary>A JS pattern compiled with the language's case sensitivity (hljs <c>langRe</c>), materialized lazily.</summary>
internal sealed class HljsPattern(string source, bool ignoreCase)
{
    public string Source { get; } = source;
    public bool IgnoreCase { get; } = ignoreCase;
    Regex? search, anchored;
    public Regex Search => search ??= HljsRegex.Get(Source, IgnoreCase);
    public Regex Anchored => anchored ??= HljsRegex.Get(Source, IgnoreCase, anchored: true);
}

/// <summary>hljs MultiRegex: one joined <c>(r1)|(r2)|…</c> regex; the first defined wrapper group names the rule.</summary>
internal sealed class HljsMultiRegex
{
    const int TierUpThreshold = 512;
    readonly HljsRule[] rules;
    readonly int[] wrapperGroups;
    readonly string? pattern;
    readonly bool ignoreCase;
    Regex? regex;
    int uses;

    public HljsMultiRegex(HljsRule[] rules, bool ignoreCase)
    {
        this.rules = rules;
        wrapperGroups = new int[rules.Length];
        var matchAt = 1;
        for (var i = 0; i < rules.Length; i++)
        {
            wrapperGroups[i] = matchAt;
            matchAt += HljsRegex.CountMatchGroups(rules[i].Source) + 1;
        }
        this.ignoreCase = ignoreCase;
        // An empty rule set never matches (hljs replaces exec with `() => null`).
        if (rules.Length == 0) return;
        pattern = HljsRegex.Join([.. rules.Select(r => r.Source)]);
        regex = HljsRegex.Get(pattern, ignoreCase);
    }

    public HljsMatch? Exec(string s, int lastIndex)
    {
        var re = regex;
        if (re == null || lastIndex > s.Length) return null;
        // Hot matchers tier up to RegexOptions.Compiled in the background (as V8 tiers up irregexp code): same pattern,
        // same semantics, no compilation pause on the highlighting thread.
        if (Interlocked.Increment(ref uses) == TierUpThreshold)
            ThreadPool.QueueUserWorkItem(static self => self.regex = HljsRegex.Get(self.pattern!, self.ignoreCase, compiled: true), this, false);
        var m = re.Match(s, lastIndex);
        if (!m.Success) return null;
        for (var i = 0; i < wrapperGroups.Length; i++)
        {
            if (!m.Groups[wrapperGroups[i]].Success) continue;
            var rule = rules[i];
            return new HljsMatch(m, wrapperGroups[i], s, rule.Type, rule.Mode, rule.Position);
        }
        throw new InvalidOperationException("hljs: no rule group matched");
    }
}

/// <summary>A matcher rule. <see cref="Position"/> is shared and rewritten whenever a resumed matcher is built, exactly
/// like the shared <c>opts</c> objects of hljs (the value observed by <c>exec</c> depends on build history).</summary>
internal sealed class HljsRule(string source, HljsRuleType type, HljsCompiledMode? mode)
{
    public string Source { get; } = source;
    public HljsRuleType Type { get; } = type;
    public HljsCompiledMode? Mode { get; internal set; } = mode;
    public volatile int Position;
}

/// <summary>hljs ResumableMultiRegex. Per-scan state (<c>regexIndex</c>, <c>lastIndex</c>) is owned by the caller.</summary>
internal sealed class HljsResumableMatcher
{
    readonly HljsRule[] rules;
    readonly int count;
    readonly bool ignoreCase;
    readonly HljsMultiRegex?[] multiRegexes;
    readonly Lock buildLock = new();

    public HljsResumableMatcher(HljsRule[] rules, bool ignoreCase)
    {
        this.rules = rules;
        this.ignoreCase = ignoreCase;
        count = rules.Count(r => r.Type == HljsRuleType.Begin);
        multiRegexes = new HljsMultiRegex?[rules.Length + 1];
    }

    public IReadOnlyList<HljsRule> Rules => rules;

    public HljsMultiRegex GetMatcher(int index)
    {
        if (index < multiRegexes.Length && Volatile.Read(ref multiRegexes[index]) is { } existing) return existing;
        lock (buildLock)
        {
            if (index < multiRegexes.Length && multiRegexes[index] is { } built) return built;
            var slice = index < rules.Length ? rules[index..] : [];
            for (var i = 0; i < slice.Length; i++) slice[i].Position = i;
            var matcher = new HljsMultiRegex(slice, ignoreCase);
            if (index < multiRegexes.Length) Volatile.Write(ref multiRegexes[index], matcher);
            return matcher;
        }
    }

    public HljsMatch? Exec(string s, int lastIndex, ref int regexIndex)
    {
        var result = GetMatcher(regexIndex).Exec(s, lastIndex);
        if (regexIndex != 0 && !(result != null && result.Index == lastIndex))
            result = GetMatcher(0).Exec(s, lastIndex + 1);
        if (result != null)
        {
            regexIndex += result.Position + 1;
            if (regexIndex == count) regexIndex = 0;
        }
        return result;
    }
}

/// <summary>A match as seen by the parser and callbacks after <c>match.splice(0, i)</c>: index 0 is the rule's text.</summary>
internal sealed class HljsMatch(Match match, int offset, string input, HljsRuleType type, HljsCompiledMode? rule, int position)
{
    public int Index { get; } = match.Index;
    public string Input { get; } = input;
    public HljsRuleType Type { get; } = type;
    public HljsCompiledMode? Rule { get; } = rule;
    public int Position { get; } = position;
    public string Lexeme { get; } = match.Groups[offset].Value;

    /// <summary>JS <c>match[i]</c> (null for undefined).</summary>
    public string? this[int i]
    {
        get
        {
            var g = offset + i;
            return g < match.Groups.Count && match.Groups[g].Success ? match.Groups[g].Value : null;
        }
    }
}

internal sealed class HljsResponse(Dictionary<string, object?> data)
{
    public Dictionary<string, object?> Data { get; } = data;
    public bool IsMatchIgnored { get; private set; }
    public void IgnoreMatch() => IsMatchIgnored = true;
}

internal delegate void HljsModeCallback(HljsMatch match, HljsResponse response);

/// <summary>Runtime view of a compiled mode object (one per distinct JS mode object).</summary>
internal sealed class HljsCompiledMode
{
    public string? ClassName;        // raw className when truthy
    public string? OpenName;         // classNameAliases[className] || className
    public double Relevance;
    public bool Skip, ExcludeBegin, ExcludeEnd, ReturnBegin, ReturnEnd, EndsWithParent, EndsParent, EndSameAsBegin;
    public bool HasSubLanguage, SubLanguageTruthy;
    public string? SubLanguageName;  // string subLanguage
    public string[]? SubLanguageList; // array subLanguage
    public Dictionary<string, (string Kind, double Relevance)>? Keywords;
    public HljsPattern? KeywordPattern;
    public HljsPattern? EndRe;
    public HljsCompiledMode? Starts;
    public HljsResumableMatcher? Matcher;
    public HljsModeCallback? BeforeBegin, OnBegin, OnEnd;
}

/// <summary>A registered language (JS: the object stored in <c>languages[name]</c>) plus its lazily compiled form.</summary>
internal sealed class HljsLanguage
{
    static readonly string[] CommonKeywords = ["of", "and", "for", "in", "not", "or", "if", "then", "parent", "list", "value"];

    readonly Lock compileLock = new();
    volatile HljsCompiledMode? compiled;
    readonly HljsCallbacks callbacks;

    public HljsLanguage(HljsObject raw, HljsCallbacks callbacks)
    {
        Raw = raw;
        this.callbacks = callbacks;
        // Read once: compilation mutates Raw in place (under compileLock), concurrent readers must not touch it.
        CaseInsensitive = HljsJs.Truthy(raw["case_insensitive"]);
        DisableAutodetect = HljsJs.Truthy(raw["disableAutodetect"]);
        SupersetOf = raw["supersetOf"];
    }

    public HljsObject Raw { get; }
    public bool CaseInsensitive { get; }
    public bool DisableAutodetect { get; }
    public object? SupersetOf { get; }
    public IReadOnlyDictionary<string, string> ClassNameAliases { get; private set; } = new Dictionary<string, string>();

    public HljsCompiledMode Compile()
    {
        if (compiled is { } c) return c;
        lock (compileLock)
        {
            if (compiled is { } c2) return c2;
            var root = new Compiler(this).CompileLanguage();
            compiled = root;
            return root;
        }
    }

    public string AliasOf(string kind) => ClassNameAliases.TryGetValue(kind, out var a) ? a : kind;

    /// <summary>The body of hljs <c>compileLanguage</c>, operating on the JS object graph.</summary>
    sealed class Compiler(HljsLanguage owner)
    {
        readonly HljsObject language = owner.Raw;
        readonly bool ci = owner.CaseInsensitive;
        readonly Dictionary<HljsObject, HljsCompiledMode> typed = new(ReferenceEqualityComparer.Instance);
        readonly Dictionary<MatcherSpec, HljsResumableMatcher> matchers = new(ReferenceEqualityComparer.Instance);

        sealed record MatcherSpec((string Source, HljsRuleType Type, HljsObject? Rule)[] Rules);

        // new RegExp(source(value), flags): source() is null for falsy values (-> /null/) and undefined for other non-strings (-> /(?:)/).
        HljsPattern LangRe(object? value) => new(!HljsJs.Truthy(value) ? "null" : HljsJs.Source(value) ?? "(?:)", ci);

        public HljsCompiledMode CompileLanguage()
        {
            if (!HljsJs.Truthy(language["compilerExtensions"])) language["compilerExtensions"] = new List<object?>();
            if (language["contains"] is List<object?> top && top.Contains("self"))
                throw new InvalidOperationException("ERR: contains `self` is not supported at the top-level of a language.  See documentation.");
            language["classNameAliases"] = Inherit(HljsJs.Truthy(language["classNameAliases"]) ? (HljsObject)language["classNameAliases"]! : new HljsObject());
            var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
            var cna = (HljsObject)language["classNameAliases"]!;
            foreach (var k in cna.Keys) if (cna[k] is string v && v.Length > 0) aliases[k] = v;
            owner.ClassNameAliases = aliases;
            CompileMode(language, null);
            return Typed(language);
        }

        static HljsObject Inherit(HljsObject original, params HljsObject[] objects)
        {
            var result = new HljsObject();
            original.CopyTo(result);
            foreach (var o in objects) o.CopyTo(result);
            return result;
        }

        static List<object?> AsList(object? v) => v as List<object?> ?? throw new InvalidOperationException("hljs: expected array");

        void CompileMode(HljsObject mode, HljsObject? parent)
        {
            if (HljsJs.Truthy(mode["isCompiled"])) return;

            // compileMatch
            if (HljsJs.Truthy(mode["match"]))
            {
                if (HljsJs.Truthy(mode["begin"]) || HljsJs.Truthy(mode["end"])) throw new InvalidOperationException("begin & end are not supported with match");
                mode["begin"] = mode["match"];
                mode.Delete("match");
            }
            if (language["compilerExtensions"] is List<object?> exts)
                foreach (var ext in exts) RunExtension(ext, mode);
            mode["__beforeBegin"] = null;

            // beginKeywords
            if (parent != null && HljsJs.Truthy(mode["beginKeywords"]))
            {
                mode["begin"] = "\\b(" + string.Join("|", ((string)mode["beginKeywords"]!).Split(' ')) + ")(?!\\.)(?=\\b|\\s)";
                mode["__beforeBegin"] = new HljsFunctionRef("skipIfhasPrecedingDot");
                if (!HljsJs.Truthy(mode["keywords"])) mode["keywords"] = mode["beginKeywords"];
                mode.Delete("beginKeywords");
                if (HljsJs.IsUndefined(mode["relevance"])) mode["relevance"] = 0d;
            }
            // compileIllegal
            if (mode["illegal"] is List<object?> illegal) mode["illegal"] = "(" + string.Join("|", illegal.Select(HljsJs.Source)) + ")";
            // compileRelevance
            if (HljsJs.IsUndefined(mode["relevance"])) mode["relevance"] = 1d;

            mode["isCompiled"] = true;

            object? keywordPattern = null;
            var kw = mode["keywords"];
            if (kw is HljsObject kwObj)
            {
                keywordPattern = kwObj["$pattern"];
                kwObj.Delete("$pattern");
            }
            else if (kw is null) throw new InvalidOperationException("hljs: keywords is null");
            if (HljsJs.Truthy(mode["keywords"])) mode["keywords"] = CompileKeywords(mode["keywords"], ci);
            if (HljsJs.Truthy(mode["lexemes"]) && HljsJs.Truthy(keywordPattern))
                throw new InvalidOperationException("ERR: Prefer `keywords.$pattern` to `mode.lexemes`, BOTH are not allowed. (see mode reference) ");
            if (!HljsJs.Truthy(keywordPattern)) keywordPattern = HljsJs.Truthy(mode["lexemes"]) ? mode["lexemes"] : new HljsRegexLiteral("\\w+");
            mode["keywordPatternRe"] = LangRe(keywordPattern);

            if (parent != null)
            {
                if (!HljsJs.Truthy(mode["begin"])) mode["begin"] = new HljsRegexLiteral("\\B|\\b");
                if (HljsJs.Truthy(mode["endSameAsBegin"])) mode["end"] = mode["begin"];
                if (!HljsJs.Truthy(mode["end"]) && !HljsJs.Truthy(mode["endsWithParent"])) mode["end"] = new HljsRegexLiteral("\\B|\\b");
                if (HljsJs.Truthy(mode["end"])) mode["endRe"] = LangRe(mode["end"]);
                var terminatorEnd = HljsJs.Source(mode["end"]) ?? "";
                if (HljsJs.Truthy(mode["endsWithParent"]) && parent["terminatorEnd"] is string pte && pte.Length > 0)
                    terminatorEnd += (HljsJs.Truthy(mode["end"]) ? "|" : "") + pte;
                mode["terminatorEnd"] = terminatorEnd;
            }
            if (!HljsJs.Truthy(mode["contains"])) mode["contains"] = new List<object?>();

            var expanded = new List<object?>();
            foreach (var c in AsList(mode["contains"]))
            {
                var e = ExpandOrCloneMode(c is "self" ? mode : c as HljsObject ?? throw new InvalidOperationException("hljs: bad contains entry"));
                if (e is List<object?> many) expanded.AddRange(many);
                else expanded.Add(e);
            }
            mode["contains"] = expanded;
            foreach (var c in expanded) CompileMode((HljsObject)c!, mode);

            if (HljsJs.Truthy(mode["starts"])) CompileMode((HljsObject)mode["starts"]!, parent);

            // buildModeRegex
            var rules = new List<(string, HljsRuleType, HljsObject?)>();
            foreach (var term in expanded.Cast<HljsObject>())
                rules.Add((HljsJs.Source(term["begin"]) ?? throw new InvalidOperationException("hljs: rule without begin"), HljsRuleType.Begin, term));
            if (mode["terminatorEnd"] is string te && te.Length > 0) rules.Add((te, HljsRuleType.End, null));
            if (HljsJs.Truthy(mode["illegal"])) rules.Add((HljsJs.Source(mode["illegal"]) ?? "(?:)", HljsRuleType.Illegal, null));
            mode["matcher"] = new MatcherSpec([.. rules]);
        }

        object ExpandOrCloneMode(HljsObject mode)
        {
            if (HljsJs.Truthy(mode["variants"]) && !HljsJs.Truthy(mode["cachedVariants"]))
            {
                var variantsNull = new HljsObject { ["variants"] = null };
                mode["cachedVariants"] = AsList(mode["variants"]).Select(v => (object?)Inherit(mode, variantsNull, (HljsObject)v!)).ToList();
            }
            if (HljsJs.Truthy(mode["cachedVariants"])) return mode["cachedVariants"]!;
            if (DependencyOnParent(mode))
                return Inherit(mode, new HljsObject { ["starts"] = HljsJs.Truthy(mode["starts"]) ? Inherit((HljsObject)mode["starts"]!) : null });
            if (mode.Frozen) return Inherit(mode);
            return mode;
        }

        static bool DependencyOnParent(HljsObject? mode) =>
            mode != null && (HljsJs.Truthy(mode["endsWithParent"]) || DependencyOnParent(mode["starts"] as HljsObject));

        void RunExtension(object? ext, HljsObject mode)
        {
            if (ext is not HljsFunctionRef { Id: "r.beforeMatch" }) throw new InvalidOperationException("hljs: unknown compiler extension");
            if (!HljsJs.Truthy(mode["beforeMatch"])) return;
            if (HljsJs.Truthy(mode["starts"])) throw new InvalidOperationException("beforeMatch cannot be used with starts");
            var originalMode = new HljsObject();
            mode.CopyTo(originalMode);
            foreach (var key in mode.Keys.ToArray()) mode.Delete(key);
            mode["begin"] = (HljsJs.Source(originalMode["beforeMatch"]) ?? "") + "(?=" + (HljsJs.Source(originalMode["begin"]) ?? "") + ")";
            originalMode["endsParent"] = true;
            mode["starts"] = new HljsObject { ["relevance"] = 0d, ["contains"] = new List<object?> { originalMode } };
            mode["relevance"] = 0d;
            originalMode.Delete("beforeMatch");
        }

        static Dictionary<string, (string, double)> CompileKeywords(object? raw, bool caseInsensitive, string className = "keyword")
        {
            var compiled = new Dictionary<string, (string, double)>(StringComparer.Ordinal);
            switch (raw)
            {
                case string s: CompileList(className, s.Split(' ')); break;
                case List<object?> list: CompileList(className, list.Select(x => (string)x!)); break;
                case HljsObject obj:
                    foreach (var key in obj.Keys.ToArray())
                        foreach (var (k, v) in CompileKeywords(obj[key], caseInsensitive, key)) compiled[k] = v;
                    break;
                default: throw new InvalidOperationException("hljs: unsupported keywords value");
            }
            return compiled;

            void CompileList(string cls, IEnumerable<string> keywordList)
            {
                foreach (var kwRaw in keywordList)
                {
                    var keyword = caseInsensitive ? kwRaw.ToLowerInvariant() : kwRaw;
                    var pair = keyword.Split('|');
                    if (pair[0] == "__proto__") continue; // JS: assigning __proto__ sets the prototype, never an own key
                    compiled[pair[0]] = (cls, ScoreForKeyword(pair[0], pair.Length > 1 ? pair[1] : null));
                }
            }
        }

        static double ScoreForKeyword(string keyword, string? providedScore) =>
            !string.IsNullOrEmpty(providedScore) ? HljsJs.ToNumber(providedScore) : CommonKeywords.Contains(keyword.ToLowerInvariant()) ? 0 : 1;

        // ---- typed runtime graph ---------------------------------------------------------------------------------

        HljsCompiledMode Typed(HljsObject mode)
        {
            if (typed.TryGetValue(mode, out var t)) return t;
            t = new HljsCompiledMode();
            typed[mode] = t;
            var className = mode["className"];
            if (HljsJs.Truthy(className))
            {
                t.ClassName = className as string ?? throw new InvalidOperationException("hljs: non-string className");
                t.OpenName = owner.AliasOf(t.ClassName);
            }
            t.Relevance = HljsJs.ToNumber(mode["relevance"]);
            t.Skip = HljsJs.Truthy(mode["skip"]);
            t.ExcludeBegin = HljsJs.Truthy(mode["excludeBegin"]);
            t.ExcludeEnd = HljsJs.Truthy(mode["excludeEnd"]);
            t.ReturnBegin = HljsJs.Truthy(mode["returnBegin"]);
            t.ReturnEnd = HljsJs.Truthy(mode["returnEnd"]);
            t.EndsWithParent = HljsJs.Truthy(mode["endsWithParent"]);
            t.EndsParent = HljsJs.Truthy(mode["endsParent"]);
            t.EndSameAsBegin = HljsJs.Truthy(mode["endSameAsBegin"]);
            var sub = mode["subLanguage"];
            t.HasSubLanguage = sub is not (null or HljsUndefined);
            t.SubLanguageTruthy = HljsJs.Truthy(sub);
            if (sub is string ss) t.SubLanguageName = ss;
            else if (sub is List<object?> sl) t.SubLanguageList = [.. sl.Select(x => x as string ?? throw new InvalidOperationException("hljs: bad subLanguage"))];
            else if (t.HasSubLanguage) throw new InvalidOperationException("hljs: unsupported subLanguage");
            if (mode["keywords"] is Dictionary<string, (string, double)> kws && HljsJs.Truthy(mode["keywords"])) t.Keywords = kws;
            else if (HljsJs.Truthy(mode["keywords"])) throw new InvalidOperationException("hljs: uncompiled keywords");
            t.KeywordPattern = mode["keywordPatternRe"] as HljsPattern;
            t.EndRe = mode["endRe"] as HljsPattern;
            t.BeforeBegin = owner.callbacks.Resolve(mode["__beforeBegin"]);
            t.OnBegin = owner.callbacks.Resolve(mode["on:begin"]);
            t.OnEnd = owner.callbacks.Resolve(mode["on:end"]);
            if (mode["starts"] is HljsObject starts) t.Starts = Typed(starts);
            if (mode["matcher"] is MatcherSpec spec)
            {
                if (!matchers.TryGetValue(spec, out var matcher))
                {
                    var rules = spec.Rules.Select(r => new HljsRule(r.Source, r.Type, null)).ToArray();
                    matcher = new HljsResumableMatcher(rules, ci);
                    matchers[spec] = matcher;
                    for (var i = 0; i < rules.Length; i++)
                        if (spec.Rules[i].Rule is { } ruleMode) rules[i].Mode = Typed(ruleMode);
                }
                t.Matcher = matcher;
            }
            return t;
        }
    }
}
