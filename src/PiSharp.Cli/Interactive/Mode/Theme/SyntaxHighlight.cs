// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/utils/syntax-highlight.ts (highlight, supportsLanguage,
// renderHighlightedHtml's per-line formatting) and theme.ts buildCliHighlightTheme (scope to theme token mapping).
// highlight.js is not available to .NET: this is a compact native lexer over highlight.js's scopes (keyword, built_in, literal,
// number, string, comment, title, type, meta, variable, attr, tag, addition, deletion) for the languages pi registers eagerly
// and the common ones it loads later. Scopes are colored exactly as buildCliHighlightTheme colors them; token boundaries are an
// approximation of highlight.js's grammars (recorded deviation).
using System.Text;

namespace PiSharp.Cli.Interactive.Mode;

internal static class SyntaxHighlight
{
    private sealed record Language(
        string[] Keywords, string[] BuiltIns, string[] Literals, string[] LineComments, (string Open, string Close)[] BlockComments,
        string[] StringDelimiters, bool CaseInsensitive = false, bool DollarVariables = false, bool AtVariables = false, bool HashMeta = false,
        bool TitleAfterKeywords = true, bool Markup = false, bool Diff = false, bool Yaml = false, bool Css = false, bool Ini = false,
        bool TypeCase = true);

    private static readonly string[] CFamilyKeywords = ["if", "else", "for", "while", "do", "switch", "case", "default", "break", "continue", "return", "goto", "sizeof", "typedef", "struct", "union", "enum", "static", "const", "extern", "volatile", "register", "inline", "auto", "restrict"];
    private static readonly string[] CTypes = ["int", "char", "float", "double", "void", "long", "short", "signed", "unsigned", "bool", "size_t", "int8_t", "int16_t", "int32_t", "int64_t", "uint8_t", "uint16_t", "uint32_t", "uint64_t", "wchar_t"];

    private static readonly Dictionary<string, Language> Languages = Build();
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["js"] = "javascript", ["jsx"] = "javascript", ["mjs"] = "javascript", ["cjs"] = "javascript",
        ["ts"] = "typescript", ["tsx"] = "typescript", ["mts"] = "typescript", ["cts"] = "typescript",
        ["py"] = "python", ["gyp"] = "python", ["ipython"] = "python", ["python3"] = "python",
        ["sh"] = "bash", ["zsh"] = "bash", ["shell"] = "bash", ["console"] = "bash", ["shellsession"] = "bash",
        ["cs"] = "csharp", ["c#"] = "csharp", ["cc"] = "cpp", ["c++"] = "cpp", ["h++"] = "cpp", ["hpp"] = "cpp", ["hh"] = "cpp", ["hxx"] = "cpp", ["cxx"] = "cpp",
        ["h"] = "c", ["rb"] = "ruby", ["gemspec"] = "ruby", ["podspec"] = "ruby", ["thor"] = "ruby", ["irb"] = "ruby",
        ["rs"] = "rust", ["kt"] = "kotlin", ["kts"] = "kotlin", ["golang"] = "go", ["pl"] = "perl", ["pm"] = "perl",
        ["jsonc"] = "json", ["json5"] = "json", ["html"] = "xml", ["xhtml"] = "xml", ["rss"] = "xml", ["atom"] = "xml", ["xsl"] = "xml",
        ["plist"] = "xml", ["svg"] = "xml", ["vue"] = "xml", ["yml"] = "yaml", ["md"] = "markdown", ["mkdown"] = "markdown", ["mkd"] = "markdown",
        ["ps"] = "powershell", ["ps1"] = "powershell", ["pwsh"] = "powershell", ["docker"] = "dockerfile", ["toml"] = "ini",
        ["patch"] = "diff", ["text"] = "plaintext", ["txt"] = "plaintext", ["mk"] = "makefile", ["mak"] = "makefile", ["make"] = "makefile",
        ["gradle"] = "groovy", ["sc"] = "scala", ["scss"] = "css", ["less"] = "css", ["postgres"] = "sql", ["mysql"] = "sql"
    };

    private static Dictionary<string, Language> Build()
    {
        string[] js = ["as", "in", "of", "if", "for", "while", "finally", "var", "new", "function", "do", "return", "void", "else", "break", "catch", "instanceof", "with", "throw", "case", "default", "try", "switch", "continue", "typeof", "delete", "let", "yield", "const", "class", "debugger", "async", "await", "static", "import", "from", "export", "extends", "get", "set"];
        string[] jsBuiltIns = ["Object", "Function", "Boolean", "Symbol", "Math", "Date", "Number", "BigInt", "String", "RegExp", "Array", "Float32Array", "Float64Array", "Int8Array", "Uint8Array", "Int16Array", "Int32Array", "Uint16Array", "Uint32Array", "Map", "Set", "WeakMap", "WeakSet", "ArrayBuffer", "JSON", "Promise", "Proxy", "Reflect", "Intl", "Error", "TypeError", "RangeError", "SyntaxError", "console", "window", "document", "globalThis", "process", "require", "module", "exports", "setTimeout", "setInterval", "clearTimeout", "clearInterval", "parseInt", "parseFloat", "isNaN", "isFinite", "super", "this"];
        string[] jsLiterals = ["true", "false", "null", "undefined", "NaN", "Infinity"];
        var cStrings = new[] { "\"", "'" };
        var cBlock = new[] { ("/*", "*/") };
        var languages = new Dictionary<string, Language>(StringComparer.OrdinalIgnoreCase)
        {
            ["javascript"] = new(js, jsBuiltIns, jsLiterals, ["//"], cBlock, ["\"", "'", "`"]),
            ["typescript"] = new([.. js, "type", "interface", "enum", "namespace", "declare", "abstract", "implements", "private", "public", "protected", "readonly", "keyof", "infer", "is", "asserts", "satisfies", "unique", "module", "override", "accessor"],
                [.. jsBuiltIns, "any", "number", "boolean", "string", "unknown", "never", "object", "bigint", "symbol"], jsLiterals, ["//"], cBlock, ["\"", "'", "`"]),
            ["python"] = new(["and", "as", "assert", "async", "await", "break", "case", "class", "continue", "def", "del", "elif", "else", "except", "finally", "for", "from", "global", "if", "import", "in", "is", "lambda", "match", "nonlocal", "not", "or", "pass", "raise", "return", "try", "while", "with", "yield"],
                ["__import__", "abs", "all", "any", "ascii", "bin", "bool", "breakpoint", "bytearray", "bytes", "callable", "chr", "classmethod", "compile", "complex", "delattr", "dict", "dir", "divmod", "enumerate", "eval", "exec", "filter", "float", "format", "frozenset", "getattr", "globals", "hasattr", "hash", "help", "hex", "id", "input", "int", "isinstance", "issubclass", "iter", "len", "list", "locals", "map", "max", "memoryview", "min", "next", "object", "oct", "open", "ord", "pow", "print", "property", "range", "repr", "reversed", "round", "set", "setattr", "slice", "sorted", "staticmethod", "str", "sum", "super", "tuple", "type", "vars", "zip", "self", "cls"],
                ["True", "False", "None", "Ellipsis", "NotImplemented"], ["#"], [], ["\"\"\"", "'''", "\"", "'"], AtVariables: true),
            ["java"] = new(["synchronized", "abstract", "private", "var", "static", "if", "const", "for", "while", "strictfp", "finally", "protected", "import", "native", "final", "void", "enum", "else", "break", "transient", "catch", "instanceof", "volatile", "case", "assert", "package", "default", "public", "try", "switch", "continue", "throws", "protected", "public", "private", "module", "requires", "exports", "do", "sealed", "yield", "permits", "goto", "when", "class", "interface", "extends", "implements", "new", "throw", "return", "record"],
                ["super", "this", "char", "boolean", "long", "float", "int", "byte", "short", "double", "String", "Object", "Integer", "List", "Map", "System"], ["false", "true", "null"], ["//"], cBlock, cStrings, AtVariables: true),
            ["go"] = new(["break", "case", "chan", "const", "continue", "default", "defer", "else", "fallthrough", "for", "func", "go", "goto", "if", "import", "interface", "map", "package", "range", "return", "select", "struct", "switch", "type", "var"],
                ["append", "cap", "close", "complex", "copy", "imag", "len", "make", "new", "panic", "print", "println", "real", "recover", "delete", "bool", "byte", "complex64", "complex128", "error", "float32", "float64", "int8", "int16", "int32", "int64", "string", "uint8", "uint16", "uint32", "uint64", "int", "uint", "uintptr", "rune", "any"],
                ["true", "false", "iota", "nil"], ["//"], cBlock, ["\"", "'", "`"]),
            ["json"] = new([], [], ["true", "false", "null"], ["//"], cBlock, ["\""], TitleAfterKeywords: false),
            ["c"] = new(CFamilyKeywords, [.. CTypes, "printf", "malloc", "free", "memcpy", "strlen", "NULL"], ["true", "false", "NULL"], ["//"], cBlock, cStrings, HashMeta: true),
            ["cpp"] = new([.. CFamilyKeywords, "class", "namespace", "template", "typename", "public", "private", "protected", "virtual", "override", "final", "new", "delete", "operator", "using", "try", "catch", "throw", "constexpr", "consteval", "noexcept", "friend", "explicit", "mutable", "this", "co_await", "co_return", "co_yield", "concept", "requires", "decltype", "static_cast", "dynamic_cast", "reinterpret_cast", "const_cast"],
                [.. CTypes, "std", "string", "vector", "map", "set", "unique_ptr", "shared_ptr", "cout", "cin", "endl"], ["true", "false", "nullptr", "NULL"], ["//"], cBlock, cStrings, HashMeta: true),
            ["csharp"] = new(["abstract", "as", "base", "break", "case", "catch", "checked", "class", "const", "continue", "default", "delegate", "do", "else", "enum", "event", "explicit", "extern", "finally", "fixed", "for", "foreach", "goto", "if", "implicit", "in", "interface", "internal", "is", "lock", "namespace", "new", "operator", "out", "override", "params", "private", "protected", "public", "readonly", "record", "ref", "return", "sealed", "sizeof", "stackalloc", "static", "struct", "switch", "this", "throw", "try", "typeof", "unchecked", "unsafe", "using", "virtual", "void", "volatile", "while", "async", "await", "var", "get", "set", "init", "yield", "where", "when", "with", "required", "global", "partial", "dynamic", "nameof", "file", "scoped", "not", "and", "or"],
                ["bool", "byte", "char", "decimal", "double", "float", "int", "long", "object", "sbyte", "short", "string", "uint", "ulong", "ushort", "nint", "nuint"], ["true", "false", "null", "default"], ["//"], cBlock, ["\"\"\"", "\"", "'"], HashMeta: true),
            ["php"] = new(["abstract", "and", "array", "as", "break", "callable", "case", "catch", "class", "clone", "const", "continue", "declare", "default", "do", "echo", "else", "elseif", "empty", "enddeclare", "endfor", "endforeach", "endif", "endswitch", "endwhile", "enum", "eval", "exit", "extends", "final", "finally", "fn", "for", "foreach", "function", "global", "goto", "if", "implements", "include", "include_once", "instanceof", "insteadof", "interface", "isset", "list", "match", "namespace", "new", "or", "print", "private", "protected", "public", "readonly", "require", "require_once", "return", "static", "switch", "throw", "trait", "try", "unset", "use", "var", "while", "xor", "yield"],
                ["Error", "Exception", "ArrayObject", "Closure", "Generator", "stdClass"], ["true", "false", "null"], ["//", "#"], cBlock, cStrings, CaseInsensitive: true, DollarVariables: true),
            ["ruby"] = new(["and", "then", "defined", "module", "in", "return", "redo", "if", "BEGIN", "retry", "end", "for", "self", "when", "next", "until", "do", "begin", "unless", "END", "rescue", "else", "break", "undef", "not", "super", "class", "case", "require", "yield", "alias", "while", "ensure", "elsif", "or", "include", "attr_reader", "attr_writer", "attr_accessor", "def", "lambda", "proc", "raise", "private", "protected", "public"],
                ["puts", "print", "p", "Array", "Hash", "String", "Integer", "Float", "Kernel", "Object"], ["true", "false", "nil"], ["#"], [("=begin", "=end")], ["\"", "'", "`"], DollarVariables: true, AtVariables: true),
            ["bash"] = new(["if", "then", "else", "elif", "fi", "for", "while", "until", "in", "do", "done", "case", "esac", "function", "select", "return", "local", "export", "declare", "readonly", "unset", "shift", "break", "continue"],
                ["echo", "cd", "pwd", "ls", "cat", "grep", "sed", "awk", "find", "printf", "read", "source", "exit", "eval", "exec", "set", "test", "trap", "alias", "type", "kill", "wait", "true", "false", "mkdir", "rm", "cp", "mv", "chmod", "chown", "touch", "git", "npm", "node", "curl", "sudo"],
                [], ["#"], [], ["\"", "'"], DollarVariables: true, HashMeta: false, TitleAfterKeywords: true),
            ["rust"] = new(["abstract", "as", "async", "await", "become", "box", "break", "const", "continue", "crate", "do", "dyn", "else", "enum", "extern", "final", "fn", "for", "if", "impl", "in", "let", "loop", "macro", "match", "mod", "move", "mut", "override", "priv", "pub", "ref", "return", "self", "Self", "static", "struct", "super", "trait", "try", "type", "typeof", "union", "unsafe", "unsized", "use", "virtual", "where", "while", "yield"],
                ["i8", "i16", "i32", "i64", "i128", "isize", "u8", "u16", "u32", "u64", "u128", "usize", "f32", "f64", "str", "char", "bool", "Box", "Option", "Result", "String", "Vec", "Some", "None", "Ok", "Err", "println", "print", "format", "vec", "panic", "assert", "assert_eq"],
                ["true", "false"], ["//"], cBlock, ["\""], HashMeta: true),
            ["scala"] = new(["type", "yield", "lazy", "override", "def", "with", "val", "var", "sealed", "abstract", "private", "trait", "object", "if", "then", "forSome", "for", "while", "do", "throw", "finally", "protected", "extends", "import", "final", "return", "else", "break", "new", "catch", "super", "class", "case", "package", "default", "try", "this", "match", "continue", "throws", "implicit", "export", "enum", "given", "transparent", "inline", "using"],
                ["Int", "Long", "String", "Boolean", "Double", "Float", "Unit", "Any", "AnyRef", "Nothing", "List", "Map", "Option", "Some", "Seq"], ["true", "false", "null"], ["//"], cBlock, ["\"\"\"", "\"", "'"]),
            ["kotlin"] = new(["abstract", "as", "val", "var", "vararg", "get", "set", "class", "object", "open", "private", "protected", "public", "noinline", "crossinline", "dynamic", "final", "enum", "if", "else", "do", "while", "for", "when", "throw", "try", "catch", "finally", "import", "package", "is", "in", "fun", "override", "companion", "reified", "inline", "lateinit", "init", "interface", "annotation", "data", "sealed", "internal", "infix", "operator", "out", "by", "constructor", "super", "tailrec", "where", "const", "inner", "suspend", "typealias", "external", "expect", "actual", "return", "break", "continue", "this"],
                ["Byte", "Short", "Char", "Int", "Long", "Boolean", "Float", "Double", "Void", "Unit", "Nothing", "String", "Any", "List", "Map", "println"], ["true", "false", "null"], ["//"], cBlock, ["\"\"\"", "\"", "'"], AtVariables: true),
            ["swift"] = new(["actor", "any", "associatedtype", "async", "await", "as", "break", "case", "catch", "class", "continue", "convenience", "default", "defer", "deinit", "didSet", "distributed", "do", "dynamic", "else", "enum", "extension", "fallthrough", "fileprivate", "final", "for", "func", "get", "guard", "if", "import", "indirect", "infix", "init", "inout", "internal", "in", "is", "isolated", "lazy", "let", "mutating", "nonisolated", "nonmutating", "open", "operator", "optional", "override", "postfix", "precedencegroup", "prefix", "private", "protocol", "public", "repeat", "required", "rethrows", "return", "set", "some", "static", "struct", "subscript", "super", "switch", "throws", "throw", "try", "typealias", "unowned", "var", "weak", "where", "while", "willSet", "self", "Self"],
                ["Int", "Double", "Float", "String", "Bool", "Character", "Array", "Dictionary", "Set", "Optional", "print"], ["true", "false", "nil"], ["//"], cBlock, ["\"\"\"", "\""], AtVariables: true, HashMeta: true),
            ["dart"] = new(["abstract", "as", "assert", "async", "await", "base", "break", "case", "catch", "class", "const", "continue", "covariant", "default", "deferred", "do", "dynamic", "else", "enum", "export", "extends", "extension", "external", "factory", "final", "finally", "for", "Function", "get", "hide", "if", "implements", "import", "in", "interface", "is", "late", "library", "mixin", "new", "on", "operator", "part", "required", "rethrow", "return", "sealed", "set", "show", "static", "super", "switch", "sync", "this", "throw", "try", "typedef", "var", "void", "when", "while", "with", "yield"],
                ["int", "double", "num", "String", "bool", "List", "Map", "Set", "Future", "Stream", "print", "Object"], ["true", "false", "null"], ["//"], cBlock, ["\"\"\"", "'''", "\"", "'"], AtVariables: true),
            ["groovy"] = new(["byte", "short", "char", "int", "long", "boolean", "float", "double", "void", "def", "as", "in", "assert", "trait", "abstract", "static", "volatile", "transient", "public", "private", "protected", "synchronized", "final", "class", "interface", "enum", "if", "else", "for", "while", "switch", "case", "break", "default", "continue", "throw", "throws", "try", "catch", "finally", "implements", "extends", "new", "import", "package", "return", "instanceof", "var"],
                ["println", "print", "String", "Object", "List", "Map"], ["true", "false", "null"], ["//"], cBlock, ["\"\"\"", "'''", "\"", "'"], DollarVariables: true, AtVariables: true),
            ["perl"] = new(["abs", "accept", "alarm", "and", "atan2", "bind", "binmode", "bless", "break", "caller", "chdir", "chmod", "chomp", "chop", "chown", "chr", "close", "closedir", "continue", "defined", "delete", "die", "do", "each", "else", "elsif", "eof", "eval", "exec", "exists", "exit", "for", "foreach", "if", "join", "keys", "last", "local", "map", "my", "next", "no", "not", "open", "or", "our", "package", "pop", "print", "printf", "push", "redo", "ref", "require", "return", "reverse", "say", "scalar", "shift", "sort", "splice", "split", "sprintf", "sub", "undef", "unless", "unshift", "until", "use", "values", "wantarray", "warn", "while"],
                [], [], ["#"], [("=pod", "=cut")], ["\"", "'"], DollarVariables: true, AtVariables: true),
            ["lua"] = new(["and", "break", "do", "else", "elseif", "end", "for", "goto", "if", "in", "local", "not", "or", "repeat", "return", "then", "until", "while", "function"],
                ["_G", "_ENV", "_VERSION", "assert", "collectgarbage", "dofile", "error", "getmetatable", "ipairs", "load", "loadfile", "next", "pairs", "pcall", "print", "rawequal", "rawget", "rawlen", "rawset", "require", "select", "setmetatable", "tonumber", "tostring", "type", "xpcall", "coroutine", "debug", "io", "math", "os", "package", "string", "table", "utf8"],
                ["true", "false", "nil"], ["--"], [("--[[", "]]")], ["\"", "'"]),
            ["nix"] = new(["rec", "with", "let", "in", "inherit", "assert", "if", "else", "then"], ["import", "abort", "baseNameOf", "dirOf", "isNull", "builtins", "map", "removeAttrs", "throw", "toString", "derivation"],
                ["true", "false", "or", "and", "null"], ["#"], cBlock, ["\"", "''"]),
            ["powershell"] = new(["if", "else", "elseif", "switch", "foreach", "for", "while", "do", "until", "break", "continue", "return", "function", "filter", "param", "begin", "process", "end", "try", "catch", "finally", "throw", "trap", "class", "enum", "using", "in"],
                ["Write-Host", "Write-Output", "Get-ChildItem", "Get-Content", "Set-Content", "Get-Item", "New-Item", "Remove-Item", "Copy-Item", "Move-Item", "Select-Object", "Where-Object", "ForEach-Object", "Invoke-WebRequest", "Invoke-RestMethod"],
                ["$true", "$false", "$null"], ["#"], [("<#", "#>")], ["\"", "'"], CaseInsensitive: true, DollarVariables: true),
            ["sql"] = new(["select", "from", "where", "and", "or", "not", "insert", "into", "values", "update", "set", "delete", "create", "table", "drop", "alter", "add", "column", "index", "primary", "key", "foreign", "references", "join", "inner", "left", "right", "outer", "full", "on", "group", "by", "order", "having", "limit", "offset", "as", "distinct", "union", "all", "case", "when", "then", "else", "end", "in", "is", "like", "between", "exists", "view", "with", "returning", "default", "unique", "constraint", "begin", "commit", "rollback", "transaction", "asc", "desc"],
                ["count", "sum", "avg", "min", "max", "coalesce", "cast", "int", "integer", "varchar", "text", "boolean", "date", "timestamp", "serial", "bigint", "numeric", "char", "float", "real"],
                ["true", "false", "null"], ["--"], cBlock, ["'", "\""], CaseInsensitive: true, TitleAfterKeywords: false),
            ["dockerfile"] = new(["from", "maintainer", "expose", "env", "arg", "user", "onbuild", "stopsignal", "run", "cmd", "entrypoint", "volume", "workdir", "copy", "add", "label", "healthcheck", "shell", "as"],
                [], [], ["#"], [], ["\"", "'"], CaseInsensitive: true, DollarVariables: true, TitleAfterKeywords: false),
            ["makefile"] = new(["define", "endef", "undefine", "ifdef", "ifndef", "ifeq", "ifneq", "else", "endif", "include", "-include", "sinclude", "override", "export", "unexport", "private", "vpath"],
                [], [], ["#"], [], ["\"", "'"], DollarVariables: true, TitleAfterKeywords: false),
            ["xml"] = new([], [], [], [], [("<!--", "-->")], ["\"", "'"], Markup: true),
            ["css"] = new([], [], [], [], cBlock, ["\"", "'"], Css: true),
            ["yaml"] = new([], [], ["true", "false", "yes", "no", "null", "on", "off"], ["#"], [], ["\"", "'"], Yaml: true, TitleAfterKeywords: false),
            ["ini"] = new([], [], ["true", "false", "yes", "no", "on", "off"], ["#", ";"], [], ["\"\"\"", "'''", "\"", "'"], Ini: true, TitleAfterKeywords: false),
            ["diff"] = new([], [], [], [], [], [], Diff: true),
            ["markdown"] = new([], [], [], [], [], [], TitleAfterKeywords: false),
            ["plaintext"] = new([], [], [], [], [], [], TitleAfterKeywords: false)
        };
        return languages;
    }

    private static Language? Resolve(string name)
    {
        if (Languages.TryGetValue(name, out var language)) return language;
        return Aliases.TryGetValue(name, out var target) && Languages.TryGetValue(target, out var aliased) ? aliased : null;
    }

    public static bool SupportsLanguage(string name) => Resolve(name) is not null;

    /// <summary>Highlights <paramref name="code"/>; every scope color opens and closes within a line.</summary>
    public static string Highlight(string code, string language, Theme theme)
    {
        var definition = Resolve(language) ?? throw new ArgumentException($"Unknown language: \"{language}\"");
        var output = new StringBuilder(code.Length * 2);
        void Emit(string text, string? scope)
        {
            var formatter = scope is null ? null : Formatter(scope, theme);
            if (formatter is null) { output.Append(text); return; }
            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (i > 0) output.Append('\n');
                if (lines[i].Length > 0) output.Append(formatter(lines[i]));
            }
        }
        if (definition.Diff) { HighlightDiff(code, Emit); return output.ToString(); }
        if (definition.Markup) { HighlightMarkup(code, definition, Emit); return output.ToString(); }
        HighlightCode(code, definition, Emit, language);
        return output.ToString();
    }

    private static Func<string, string>? Formatter(string scope, Theme t) => scope switch
    {
        "keyword" => s => t.Fg("syntaxKeyword", s),
        "built_in" => s => t.Fg("syntaxType", s),
        "literal" or "number" => s => t.Fg("syntaxNumber", s),
        "regexp" or "string" => s => t.Fg("syntaxString", s),
        "subst" => s => t.Fg("text", s),
        "comment" or "doctag" => s => t.Fg("syntaxComment", s),
        "meta" => s => t.Fg("muted", s),
        "function" or "title" => s => t.Fg("syntaxFunction", s),
        "class" or "type" => s => t.Fg("syntaxType", s),
        "tag" => s => t.Fg("syntaxPunctuation", s),
        "name" => s => t.Fg("syntaxKeyword", s),
        "attr" or "variable" or "params" => s => t.Fg("syntaxVariable", s),
        "operator" => s => t.Fg("syntaxOperator", s),
        "punctuation" => s => t.Fg("syntaxPunctuation", s),
        "emphasis" => s => t.Italic(s),
        "strong" => s => t.Bold(s),
        "link" => s => t.Underline(s),
        "addition" => s => t.Fg("toolDiffAdded", s),
        "deletion" => s => t.Fg("toolDiffRemoved", s),
        "section" => s => t.Fg("syntaxFunction", s),
        _ => null
    };

    private static void HighlightDiff(string code, Action<string, string?> emit)
    {
        var lines = code.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0) emit("\n", null);
            var line = lines[i];
            var scope = line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal) || line.StartsWith("@@", StringComparison.Ordinal) ? "meta"
                : line.StartsWith('+') ? "addition" : line.StartsWith('-') ? "deletion" : line.StartsWith("diff ", StringComparison.Ordinal) || line.StartsWith("index ", StringComparison.Ordinal) ? "meta" : null;
            emit(line, scope);
        }
    }

    private static void HighlightMarkup(string code, Language language, Action<string, string?> emit)
    {
        var i = 0;
        while (i < code.Length)
        {
            if (code.AsSpan(i).StartsWith("<!--"))
            {
                var end = code.IndexOf("-->", i + 4, StringComparison.Ordinal);
                end = end < 0 ? code.Length : end + 3;
                emit(code[i..end], "comment"); i = end; continue;
            }
            if (code[i] == '<' && i + 1 < code.Length && (char.IsLetter(code[i + 1]) || code[i + 1] is '/' or '!' or '?'))
            {
                var end = code.IndexOf('>', i);
                if (end < 0) end = code.Length - 1;
                var tag = code[i..(end + 1)];
                if (tag.StartsWith("<?", StringComparison.Ordinal) || tag.StartsWith("<!", StringComparison.Ordinal)) { emit(tag, "meta"); i = end + 1; continue; }
                var j = 1; if (j < tag.Length && tag[j] == '/') j++;
                emit(tag[..j], "tag");
                var nameStart = j;
                while (j < tag.Length && !char.IsWhiteSpace(tag[j]) && tag[j] is not '>' and not '/') j++;
                emit(tag[nameStart..j], "name");
                while (j < tag.Length)
                {
                    var c = tag[j];
                    if (c is '"' or '\'')
                    {
                        var close = tag.IndexOf(c, j + 1); close = close < 0 ? tag.Length - 1 : close;
                        emit(tag[j..(close + 1)], "string"); j = close + 1;
                    }
                    else if (char.IsLetter(c) || c is '-' or ':' or '_')
                    {
                        var start = j; while (j < tag.Length && (char.IsLetterOrDigit(tag[j]) || tag[j] is '-' or ':' or '_' or '.')) j++;
                        emit(tag[start..j], "attr");
                    }
                    else if (c is '>' or '/') { emit(tag[j..], "tag"); j = tag.Length; }
                    else { emit(c.ToString(), null); j++; }
                }
                i = end + 1; continue;
            }
            if (code[i] == '&')
            {
                var end = code.IndexOf(';', i);
                if (end > i && end - i < 10) { emit(code[i..(end + 1)], "symbol"); i = end + 1; continue; }
            }
            var next = code.IndexOfAny(['<', '&'], i + 1);
            if (next < 0) next = code.Length;
            emit(code[i..next], null); i = next;
            _ = language;
        }
    }

    private static bool IsIdentStart(char c) => char.IsLetter(c) || c is '_';
    private static bool IsIdentPart(char c) => char.IsLetterOrDigit(c) || c is '_';

    private static void HighlightCode(string code, Language lang, Action<string, string?> emit, string languageName)
    {
        var comparer = lang.CaseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var keywords = new HashSet<string>(lang.Keywords, comparer);
        var builtIns = new HashSet<string>(lang.BuiltIns, comparer);
        var literals = new HashSet<string>(lang.Literals, comparer);
        string[] titleKeywords = ["function", "def", "fn", "func", "fun", "class", "struct", "enum", "interface", "trait", "type", "sub", "module", "impl", "record", "object", "protocol", "extension", "namespace"];
        var expectTitle = false;
        var lineStart = true;
        var i = 0;
        var plain = new StringBuilder();
        void Flush() { if (plain.Length > 0) { emit(plain.ToString(), null); plain.Clear(); } }
        void Token(string text, string? scope) { Flush(); emit(text, scope); }
        while (i < code.Length)
        {
            var c = code[i];
            if (c == '\n') { plain.Append(c); i++; lineStart = true; continue; }
            if (lineStart && char.IsWhiteSpace(c)) { plain.Append(c); i++; continue; }
            var atLineStart = lineStart; lineStart = false;
            // Block comments.
            var matchedBlock = false;
            foreach (var (open, close) in lang.BlockComments)
            {
                if (!code.AsSpan(i).StartsWith(open, StringComparison.Ordinal)) continue;
                if ((open is "=begin" or "=pod") && !atLineStart) continue;
                var end = code.IndexOf(close, i + open.Length, StringComparison.Ordinal);
                end = end < 0 ? code.Length : end + close.Length;
                Token(code[i..end], "comment"); i = end; matchedBlock = true; break;
            }
            if (matchedBlock) continue;
            // Line comments.
            var matchedLine = false;
            foreach (var marker in lang.LineComments)
            {
                if (!code.AsSpan(i).StartsWith(marker, StringComparison.Ordinal)) continue;
                if (marker == "#" && lang.HashMeta) break;
                if (marker == "#" && languageName is "bash" or "sh" or "zsh" or "shell" && i > 0 && !char.IsWhiteSpace(code[i - 1]) && code[i - 1] != ';') break;
                var end = code.IndexOf('\n', i); if (end < 0) end = code.Length;
                Token(code[i..end], "comment"); i = end; matchedLine = true; break;
            }
            if (matchedLine) continue;
            // Preprocessor/attributes.
            if (lang.HashMeta && c == '#' && (atLineStart || languageName is "rust" or "rs"))
            {
                var end = code.IndexOf('\n', i); if (end < 0) end = code.Length;
                if (languageName is "rust" or "rs" && i + 1 < code.Length && code[i + 1] is '[' or '!')
                { var close = code.IndexOf(']', i); end = close < 0 ? end : Math.Min(end, close + 1); }
                Token(code[i..end], "meta"); i = end; continue;
            }
            // Strings.
            var matchedString = false;
            foreach (var delimiter in lang.StringDelimiters)
            {
                if (!code.AsSpan(i).StartsWith(delimiter, StringComparison.Ordinal)) continue;
                if (delimiter == "'" && languageName is "rust" or "rs" && IsRustLifetime(code, i)) break;
                var j = i + delimiter.Length;
                var multiline = delimiter.Length > 1 || delimiter == "`";
                while (j < code.Length)
                {
                    if (code[j] == '\\' && delimiter != "''" && j + 1 < code.Length) { j += 2; continue; }
                    if (code.AsSpan(j).StartsWith(delimiter, StringComparison.Ordinal)) { j += delimiter.Length; break; }
                    if (code[j] == '\n' && !multiline && languageName is not "bash" and not "powershell") break;
                    j++;
                }
                Token(code[i..Math.Min(j, code.Length)], "string"); i = Math.Min(j, code.Length); matchedString = true; break;
            }
            if (matchedString) continue;
            // Variables.
            if (lang.DollarVariables && c == '$' && i + 1 < code.Length && (IsIdentStart(code[i + 1]) || code[i + 1] is '{' or '(' || char.IsDigit(code[i + 1]) || code[i + 1] is '@' or '?' or '#' or '!' or '*' or '$'))
            {
                var j = i + 1;
                if (code[j] is '{' or '(') { var close = code[j] == '{' ? '}' : ')'; var end = code.IndexOf(close, j); j = end < 0 ? code.Length : end + 1; }
                else if (!IsIdentStart(code[j])) j++;
                else while (j < code.Length && (IsIdentPart(code[j]) || languageName == "powershell" && code[j] == ':')) j++;
                var text = code[i..j];
                Token(text, literals.Contains(text) ? "literal" : "variable"); i = j; continue;
            }
            if (lang.AtVariables && c == '@' && i + 1 < code.Length && IsIdentStart(code[i + 1]))
            {
                var j = i + 1; while (j < code.Length && (IsIdentPart(code[j]) || code[j] == '.')) j++;
                Token(code[i..j], languageName is "ruby" or "rb" or "perl" or "pl" ? "variable" : "meta"); i = j; continue;
            }
            // Numbers.
            if (char.IsDigit(c) || c == '.' && i + 1 < code.Length && char.IsDigit(code[i + 1]))
            {
                if (i > 0 && IsIdentPart(code[i - 1])) { plain.Append(c); i++; continue; }
                var j = i;
                if (c == '0' && i + 1 < code.Length && code[i + 1] is 'x' or 'X' or 'b' or 'B' or 'o' or 'O') j += 2;
                while (j < code.Length && (char.IsLetterOrDigit(code[j]) || code[j] is '_' or '.' && j + 1 < code.Length && char.IsDigit(code[j + 1]))) j++;
                Token(code[i..j], "number"); i = j; continue;
            }
            // Identifiers.
            if (IsIdentStart(c) || c == '$' && !lang.DollarVariables && languageName is "javascript" or "typescript" or "js" or "ts")
            {
                var j = i + 1;
                while (j < code.Length && (IsIdentPart(code[j]) || code[j] == '$' && languageName is "javascript" or "typescript" or "js" or "ts" || code[j] == '-' && lang.Css)) j++;
                if (languageName == "powershell") while (j < code.Length && (IsIdentPart(code[j]) || code[j] == '-')) j++;
                if (j < code.Length && code[j] is '!' or '?' && languageName is "ruby" or "rb" or "rust" or "rs") j++;
                var word = code[i..j];
                if (lang.Yaml || lang.Ini) { HighlightKeyValue(code, ref i, j, word, lang, emit, Token, plain); continue; }
                string? scope;
                if (expectTitle) { scope = "title"; expectTitle = false; }
                else if (keywords.Contains(word)) { scope = "keyword"; if (lang.TitleAfterKeywords && titleKeywords.Contains(word, comparer)) expectTitle = true; }
                else if (literals.Contains(word)) scope = "literal";
                else if (builtIns.Contains(word)) scope = "built_in";
                else if (languageName is "rust" or "rs" && j < code.Length && code[j] == '!') scope = "built_in";
                else if (lang.TypeCase && char.IsUpper(word[0]) && word.Length > 1 && languageName is not "bash" and not "sql" && word.Any(char.IsLower)) scope = "title";
                else if (NextNonSpace(code, j) == '(' && languageName is "javascript" or "typescript" or "js" or "ts") scope = "title";
                else scope = null;
                if (scope is null) plain.Append(word); else Token(word, scope);
                i = j; continue;
            }
            if (expectTitle && !char.IsWhiteSpace(c) && c != '*') expectTitle = false;
            plain.Append(c); i++;
        }
        Flush();
    }

    private static void HighlightKeyValue(string code, ref int i, int j, string word, Language lang, Action<string, string?> emit,
        Action<string, string?> token, StringBuilder plain)
    {
        var next = NextNonSpace(code, j);
        var keyLike = lang.Yaml ? next == ':' : next == '=';
        if (keyLike && IsFirstOnLine(code, i)) token(word, "attr");
        else if (lang.Literals.Contains(word, StringComparer.OrdinalIgnoreCase)) token(word, "literal");
        else plain.Append(word);
        i = j; _ = emit;
    }

    private static bool IsFirstOnLine(string code, int index)
    {
        for (var k = index - 1; k >= 0 && code[k] != '\n'; k--)
            if (!char.IsWhiteSpace(code[k]) && code[k] is not '-' and not '[' and not '.') return false;
        return true;
    }

    private static char NextNonSpace(string code, int index)
    {
        while (index < code.Length && code[index] is ' ' or '\t') index++;
        return index < code.Length ? code[index] : '\0';
    }

    private static bool IsRustLifetime(string code, int index) =>
        index + 2 < code.Length && IsIdentStart(code[index + 1]) && code[index + 2] != '\'' && !(index + 3 < code.Length && code[index + 3] == '\'' && code[index + 1] == '\\');
}
