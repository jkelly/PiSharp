using System.Collections.Immutable;
using PiSharp.Codemode;
using PiSharp.Contracts;

// Upstream: packages/codemode/test/sandbox.test.ts (script execution, tools, store and load, globals, escape hatches).
internal static partial class Program
{
    private const string Png = "iVBORw0KGgo=", Jpeg = "/9j/4A==", Gif = "R0lGODlh", Webp = "UklGRgAAAABXRUJQ";
    private static readonly CodemodeTool Echo = new("echo", (args, _) => ValueTask.FromResult(args));

    private static async Task<CodemodeResult> Run(string code, IEnumerable<CodemodeTool>? tools = null, double? timeoutMs = 10_000,
        IReadOnlyList<KeyValuePair<string, JsonData>>? store = null, IEnumerable<CodemodeTool>? globals = null, CancellationToken token = default)
    {
        await using var sandbox = new CodemodeSandbox(new() { Tools = [.. tools ?? []], Globals = [.. globals ?? []], TimeoutMs = timeoutMs });
        return await sandbox.ExecuteAsync(code, new() { Store = store }, token);
    }

    private static string Output(CodemodeResult result) => string.Join(" | ", result.Output.Select(item => item switch
    {
        CodemodeTextOutput text => (text.Console ? "console:" : "text:") + text.Text,
        CodemodeImageOutput image => "image:" + image.MimeType + ":" + image.Data,
        _ => "?"
    }));

    private static void Ok(CodemodeResult result, string value, string what)
    {
        Check(result.Ok, $"{what}: failed with {result.Error}");
        Equal(value, Json(result.Value), what);
    }
    private static void Failed(CodemodeResult result, CodemodeErrorKind kind, string what, string? name = null, string? message = null)
    {
        Check(!result.Ok, what + ": expected failure");
        Equal(kind, result.Error!.Kind, what + " kind");
        if (name is not null) Equal(name, result.Error.Name, what + " name");
        if (message is not null) Check(result.Error.Message.Contains(message, StringComparison.Ordinal), $"{what}: message {result.Error.Message}");
    }

    private static IEnumerable<(string, Func<Task>)> SandboxCases() =>
    [
        Case("sandbox.return-value-json-round-trip", async () =>
        {
            Ok(await Run("return { a: 1, b: [true, 'x'] }"), """{"a":1,"b":[true,"x"]}""", "object");
            Ok(await Run("return 'plain'"), "\"plain\"", "string");
            Ok(await Run(""), "undefined", "empty");
            Ok(await Run("const x = await Promise.resolve(41); return x + 1"), "42", "top-level await");
        }),
        Case("sandbox.output-in-order", async () =>
        {
            var result = await Run($$"""
                console.log("hello", 1, { a: 1 });
                text({ json: true });
                text(undefined);
                text(7);
                image("data:image/png;base64,{{Png}}");
                image({ image_url: "data:image/jpeg;base64,{{Jpeg}}" });
                image({ type: "image", data: "{{Gif}}", mimeType: "image/gif" });
                image("data:image/png;base64,{{Webp}}");
                image({ type: "image", data: "{{Png}}" });
                console.error(new Error("bad"));
                return null;
                """);
            Check(result.Ok, "ok");
            Equal($"console:hello 1 {{\"a\":1}} | text:{{\"json\":true}} | text:undefined | text:7 | image:image/png:{Png} | image:image/jpeg:{Jpeg} | image:image/gif:{Gif} | image:image/webp:{Webp} | image:image/png:{Png}",
                string.Join(" | ", Output(result).Split(" | ")[..^1]), "items");
            Check(result.Output[^1] is CodemodeTextOutput { Console: true } last && last.Text.StartsWith("Error: bad", StringComparison.Ordinal), "console error " + Output(result));
        }),
        Case("sandbox.image-validation", async () =>
        {
            var result = await Run("""
                const errors = [];
                const circular = {};
                circular.self = circular;
                for (const run of [
                  () => text(circular),
                  () => image(""),
                  () => image("https://example.com/a.png"),
                  () => image("data:image/png,raw"),
                  () => image({ type: "text", text: "x" }),
                  () => image({ type: "image", data: "" }),
                  () => image(42),
                  () => image("data:image/png;base64,AAAA!"),
                  () => image("data:image/png;base64,AAAAA"),
                  () => image("data:image/png;base64,AA=A"),
                  () => image("data:image/png;base64,"),
                  () => image("data:image/png;base64,AAAA\n[Output truncated]"),
                  () => image({ type: "image", data: "AAAA!", mimeType: "image/png" }),
                  () => image("data:image/png;base64,AAAA"),
                  () => image("data:image/png;base64,QUJD"),
                  () => image("data:image/jpeg;base64,/9j/9w=="),
                ]) {
                  try { run(); errors.push("no error"); } catch (error) { errors.push(error.name + ": " + error.message); }
                }
                return errors;
                """);
            Check(result.Ok && result.Output.IsEmpty, "no output");
            var errors = result.Value!.Value.EnumerateArray().Select(item => item.GetString()!).ToArray();
            // Engine message: V8 says "Converting circular structure to JSON", Jint "Cyclic reference detected." (codemode-engine.md).
            Equal("TypeError: Cyclic reference detected.", errors[0], "circular");
            Names([
                "TypeError: image expects a non-empty image URL string, an object with image_url, or a raw MCP image block",
                "TypeError: remote image URLs are not supported in tool outputs. Pass a base64 data URI instead",
                "TypeError: invalid image output. Pass a base64 data URI instead",
                "TypeError: image only accepts MCP image blocks, got \"text\"",
                "TypeError: image expected MCP image data",
                "TypeError: image expects a non-empty image URL string, an object with image_url, or a raw MCP image block",
                .. Enumerable.Repeat("TypeError: invalid image output. The image data is not valid base64 (truncated or corrupted?)", 6),
                .. Enumerable.Repeat("TypeError: invalid image output. The image data is not a PNG, JPEG, GIF, or WebP image", 3)], errors[1..], "image errors");
        }),
        Case("sandbox.wrapped-and-large-image-data", async () =>
        {
            var large = "iVBORw0KGgoA" + string.Concat(Enumerable.Repeat("QUJD", 256 * 1024));
            var result = await Run("image(\"data:image/png;base64,iVBORw0K\\r\\nGgo=\\n\");\nimage(\"data:image/png;base64," + large + "\");");
            Check(result.Ok, "ok");
            Equal($"image:image/png:{Png} | image:image/png:{large}", Output(result), "images");
        }),
        Case("sandbox.exit-keeps-output-and-store-writes", async () =>
        {
            var result = await Run("text(\"before\");\nstore(\"k\", 1);\nawait tools.echo(1);\ntry { exit(); } catch {}\ntext(\"after\");\nreturn \"unreachable\";", [Echo]);
            Ok(result, "undefined", "exit");
            Equal("text:before", Output(result), "output");
            Equal("k=1", string.Join(",", result.StoreWrites.Set.Select(pair => pair.Key + "=" + pair.Value)), "writes");
        }),
        Case("sandbox.errors-keep-output-and-line-numbers", async () =>
        {
            var partial = await Run("text(\"partial\");\nthrow new Error(\"boom\")");
            Failed(partial, CodemodeErrorKind.Script, "partial"); Equal("text:partial", Output(partial), "partial output");
            var syntax = await Run("const a = 1;\nconst b = ;\nreturn a");
            Failed(syntax, CodemodeErrorKind.Script, "syntax", "SyntaxError");
            Check(syntax.Error!.Stack!.Contains("codemode.js:2", StringComparison.Ordinal), "syntax line: " + syntax.Error.Stack);
            var thrown = await Run("const a = 1;\nthrow new TypeError('boom ' + a)");
            Failed(thrown, CodemodeErrorKind.Script, "thrown", "TypeError", "boom 1");
            Check(thrown.Error!.Stack!.Contains("codemode.js:2", StringComparison.Ordinal), "thrown line: " + thrown.Error.Stack);
            var stack = await Run("console.log(new Error('inner'));\nthrow new RangeError('outer')");
            Check(System.Text.RegularExpressions.Regex.IsMatch(stack.Error!.Stack!, "^RangeError: outer\n {4}at .*codemode\\.js:2"), "V8-like stack: " + stack.Error.Stack);
            Check(!stack.Error.Stack!.Contains("codemode-prelude.js", StringComparison.Ordinal) && !stack.Error.Stack.Contains('\r'), "no prelude frames, LF only");
            Check(System.Text.RegularExpressions.Regex.IsMatch(((CodemodeTextOutput)stack.Output[0]).Text, "^Error: inner\n {4}at .*codemode\\.js:1"), "console stack: " + Output(stack));
            Failed(await Run("throw { code: 7 }"), CodemodeErrorKind.Script, "non-Error", message: "{\"code\":7}");
            Failed(await Run("return 10n"), CodemodeErrorKind.Script, "BigInt return", "TypeError");
            Failed(await Run("const error = new Error('x'); error.message = 42; throw error;"), CodemodeErrorKind.Script, "non-string message", "Error", "42");
        }),
        Case("sandbox.tools-record-calls-and-run-concurrently", async () =>
        {
            var seen = new List<string>();
            var add = new CodemodeTool("add", (args, _) =>
            {
                lock (seen) seen.Add(Json(args));
                var value = args!.Value;
                return ValueTask.FromResult<JsonData?>(Parse($"{{\"sum\":{value.GetProperty("a").GetDouble() + value.GetProperty("b").GetDouble()}}}"));
            });
            var result = await Run("const first = await tools.add({ a: 1, b: 2 });\nconst second = await tools.add({ a: first.sum, b: 10 });\nreturn second.sum;", [add]);
            Ok(result, "13", "chained");
            Names(["{\"a\":1,\"b\":2}", "{\"a\":3,\"b\":10}"], seen, "arguments");
            Names(["add:Ok", "add:Ok"], result.Calls.Select(call => call.Name + ":" + call.Status), "calls");
            var delay = new CodemodeTool("delay", async (args, token) => { await Task.Delay(20, token); return args; });
            Ok(await Run("const [a, b, c] = await Promise.all([tools.delay(1), tools.delay(2), tools.echo(3)]);\nreturn { values: [a, b, c], names: Object.keys(tools) };", [Echo, delay]),
                """{"values":[1,2,3],"names":["echo","delay"]}""", "concurrent");
        }),
        Case("sandbox.normalized-identifiers-and-all-tools", async () =>
        {
            CodemodeTool Fixed(string name, string value, string? description = null) => new(name, (_, _) => ValueTask.FromResult<JsonData?>(Parse(value))) { Description = description };
            Ok(await Run("try { ALL_TOOLS.push({}); } catch {}\nreturn { all: ALL_TOOLS, calls: [await tools.my_tool(), await tools[\"my-tool\"](), await tools.mcp__docs__search()] };",
                [Fixed("my-tool", "\"dash\"", "Dashes"), Fixed("my_tool", "\"underscore\"", "Shadowed"), Fixed("mcp__docs__search", "\"mcp\"")]),
                """{"all":[{"name":"my_tool","description":"Dashes"},{"name":"mcp__docs__search","description":""}],"calls":["dash","dash","mcp"]}""", "identifiers");
            Ok(await Run("return [await tools.noop(), await tools.noop(null)]", [new("noop", (args, _) => ValueTask.FromResult(args))]), "[null,null]", "undefined through");
        }),
        Case("sandbox.tool-errors-catchable-and-unknown-tools", async () =>
        {
            var fail = new CodemodeTool("fail", (_, _) => throw new InvalidOperationException("tool exploded"));
            var result = await Run("try {\n await tools.fail();\n return 'no error';\n} catch (error) {\n return { isError: error instanceof Error, message: error.message };\n}", [fail]);
            Ok(result, """{"isError":true,"message":"tool exploded"}""", "caught");
            Names(["fail:Error"], result.Calls.Select(call => call.Name + ":" + call.Status), "error call");
            Failed(await Run("return await tools.missing()"), CodemodeErrorKind.Script, "unknown tool", "TypeError");
        }),
        Case("sandbox.unawaited-calls-cancelled-at-return", async () =>
        {
            var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var slow = new CodemodeTool("slow", async (_, token) =>
            {
                try { await Task.Delay(Timeout.Infinite, token); } catch (OperationCanceledException) { aborted.TrySetResult(); throw; }
                return null;
            });
            var result = await Run("tools.slow(); return 'early'", [slow]);
            Ok(result, "\"early\"", "early");
            Names(["slow:Cancelled"], result.Calls.Select(call => call.Name + ":" + call.Status), "cancelled call");
            await aborted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }),
        Case("sandbox.missing-members-name-close-matches", async () =>
        {
            CodemodeTool[] tools = [Echo, new("web-search", (_, _) => ValueTask.FromResult<JsonData?>(Parse("\"\"")))];
            async Task<string> Attempt(string expression) { var result = await Run($"return {expression};", tools); return result.Ok ? Json(result.Value) : result.Error!.Message; }
            Equal("tools.Echo does not exist. Did you mean tools.echo? ALL_TOOLS lists every tool; searchTools(query) finds tools by topic. Check for a member with \"Echo\" in tools.",
                await Attempt("tools.Echo"), "close match");
            Check((await Attempt("tools.websearch")).Contains("Did you mean tools.web_search?", StringComparison.Ordinal), "identifier match");
            Check((await Attempt("tools.nothing")).Contains("Available: echo, web_search.", StringComparison.Ordinal), "available");
            Equal("[true,false,\"undefined\",\"{}\"]", await Attempt("['echo' in tools, 'nothing' in tools, String(tools.toString), JSON.stringify(tools)]"), "membership");
            var result = await Run("await models.generateImage();", globals: [new("models.classify"), new("models.generateImages")]);
            Equal("models.generateImage does not exist. Did you mean models.generateImages? Check for a member with \"generateImage\" in models.", result.Error!.Message, "namespace member");
        }),
        Case("sandbox.register-unregister-and-close", async () =>
        {
            var sandbox = new CodemodeSandbox();
            sandbox.RegisterTool(Echo);
            Throws<ArgumentException>(() => sandbox.RegisterTool(Echo), "duplicate");
            Names(["echo"], sandbox.Tools.Select(tool => tool.Name), "tools");
            Ok(await sandbox.ExecuteAsync("return await tools.echo('a')"), "\"a\"", "registered");
            Check(sandbox.UnregisterTool("echo"), "unregistered");
            Ok(await sandbox.ExecuteAsync("return 'echo' in tools"), "false", "gone");
            await sandbox.CloseAsync();
            var closed = false;
            try { await sandbox.ExecuteAsync("return 1"); } catch (InvalidOperationException error) { closed = error.Message.Contains("closed", StringComparison.Ordinal); }
            Check(closed, "execute after close rejects");
        }),
        Case("sandbox.store-and-load", async () =>
        {
            var result = await Run("const seen = load(\"counter\");\nstore(\"counter\", seen + 1);\nstore(\"list\", [1, { a: null }]);\nstore(\"old\", undefined);\nreturn [seen, load(\"counter\"), load(\"missing\"), load(\"old\")];",
                store: [new("counter", Parse("41")), new("old", Parse("\"x\""))]);
            Ok(result, "[41,42,null,null]", "store");
            Equal("counter=42,list=[1,{\"a\":null}]", string.Join(",", result.StoreWrites.Set.Select(pair => pair.Key + "=" + Json(pair.Value))), "set");
            Names(["old"], result.StoreWrites.Delete, "delete");
            var copies = await Run("const value = load(\"obj\"); value.a = 2; const kept = { b: 1 }; store(\"kept\", kept); kept.b = 2;\nreturn [load(\"obj\").a, load(\"kept\").b];",
                store: [new("obj", Parse("{\"a\":1}"))]);
            Ok(copies, "[1,1]", "copies");
            Ok(await Run("const attempt = (fn) => { try { fn(); return \"ok\"; } catch (error) { return error.name; } };\nreturn [attempt(() => store(1, \"x\")), attempt(() => load({})), attempt(() => store(\"fn\", () => 1)), attempt(() => store(\"big\", \"x\".repeat(300 * 1024))), attempt(() => { for (let i = 0; i < 8; i++) store(\"k\" + i, \"x\".repeat(200 * 1024)); })];"),
                "[\"TypeError\",\"TypeError\",\"TypeError\",\"RangeError\",\"RangeError\"]", "invalid writes");
            var oversized = await Run("store(\"img\", \"x\".repeat(300 * 1024));");
            Check(oversized.Error!.Message.Contains("store(\"img\") value has 307202 characters of JSON", StringComparison.Ordinal) &&
                oversized.Error.Message.Contains("Show images with image()", StringComparison.Ordinal), "explains: " + oversized.Error.Message);
        }),
        Case("sandbox.store-limits-at-boundary", async () =>
        {
            // MAX_STORE_VALUE_CHARS counts JSON characters: a string of n characters is n + 2 with its quotes.
            Ok(await Run($"store(\"v\", \"x\".repeat({CodemodeLimits.MaxStoreValueChars - 2})); return 1"), "1", "value at limit");
            Failed(await Run($"store(\"v\", \"x\".repeat({CodemodeLimits.MaxStoreValueChars - 1}))"), CodemodeErrorKind.Script, "value one past", "RangeError");
            // MAX_STORE_TOTAL_CHARS counts keys and JSON: four values of 262144 characters with 1-character keys are 4 over.
            var fill = $"for (let i = 0; i < 4; i++) store(String(i), \"x\".repeat({CodemodeLimits.MaxStoreValueChars - 2} - 1));";
            Ok(await Run(fill + " return 1"), "1", "total at limit");
            Failed(await Run(fill + " store(\"z\", 1)"), CodemodeErrorKind.Script, "total one past", "RangeError", "store is full");
            Throws<ArgumentException>(() => new CodemodeSandbox(new() { Globals = [new("store")] }), "store reserved");
            Throws<ArgumentException>(() => new CodemodeSandbox(new() { Globals = [new("load")] }), "load reserved");
        }),
        Case("sandbox.globals-and-namespaces", async () =>
        {
            var seen = new List<string>();
            var attach = new CodemodeTool("attach", (args, _) => { lock (seen) seen.Add(Json(args)); return ValueTask.FromResult<JsonData?>(null); });
            var result = await Run("await attach({ ref: 1 });\nattach(\"not awaited\");\nreturn [typeof attach, typeof globalThis.attach, await tools.echo(2)];", [Echo], globals: [attach]);
            Ok(result, "[\"function\",\"function\",2]", "globals");
            Names(["echo"], result.Calls.Select(call => call.Name), "globals not recorded");
            Names(["{\"ref\":1}", "\"not awaited\""], seen, "unawaited global ran");
            var spread = new List<string>();
            Ok(await Run("await models.list(\"classifier\", undefined, 3);\nawait models.list();\ntry { models.extra = 1; } catch {}\nreturn [Object.keys(models), await models.first(\"a\", \"ignored\"), \"extra\" in models];",
                globals: [new("models.list", (args, _) => { spread.Add(Json(args)); return ValueTask.FromResult<JsonData?>(null); }) { Spread = true },
                    new("models.first", (args, _) => ValueTask.FromResult(args))]), "[[\"list\",\"first\"],\"a\",false]", "namespaces");
            Names(["[\"classifier\",null,3]", "[]"], spread, "spread arguments");
            foreach (var name in new[] { "a.b.c", "a.", ".a", "tools.x", "store.x", "a.not-valid", "not-valid", "tools", "console" })
                Throws<ArgumentException>(() => new CodemodeSandbox(new() { Globals = [new(name)] }), name);
            Throws<ArgumentException>(() => new CodemodeSandbox(new() { Globals = [new("models"), new("models.list")] }), "namespace conflict");
        }),
        Case("sandbox.escape-hatches", async () =>
        {
            Ok(await Run("return [typeof process, typeof require, typeof module, typeof setTimeout, typeof fetch, typeof WebAssembly, typeof std, typeof os, typeof globalThis.constructor, typeof importScripts, typeof System, typeof clr]"),
                "[\"undefined\",\"undefined\",\"undefined\",\"undefined\",\"undefined\",\"undefined\",\"undefined\",\"undefined\",\"function\",\"undefined\",\"undefined\",\"undefined\"]", "no host globals");
            Ok(await Run("return [eval(\"typeof process\"), new Function(\"return typeof process\")(), tools.echo.constructor(\"return typeof require\")(), (async () => {}).constructor(\"return typeof setTimeout\")() instanceof Promise];", [Echo]),
                "[\"undefined\",\"undefined\",\"undefined\",true]", "eval inside the engine");
            var imported = await Run("try { await import(\"node:fs\"); return \"imported\"; } catch (error) { return error.constructor.name; }");
            Check(!imported.Ok || Json(imported.Value) != "\"imported\"", "no dynamic import");
            Ok(await Run("Array.prototype.toJSON = () => null;\nObject.prototype.toJSON = () => 5;\nPromise.prototype.then = () => {};\nMap.prototype.get = () => undefined;\nglobalThis.JSON = { stringify: () => \"x\", parse: () => \"x\" };\nstore(\"k\", [1]);\nreturn [await tools.echo([2]), JSON.stringify({ a: 1 })];", [Echo]),
                "[[2],\"{\\\"a\\\":1}\"]", "patches ignored");
            Ok(await Run("return [Object.getPrototypeOf(function* () {}).prototype, Object.getPrototypeOf(async function () {}), Object.getPrototypeOf(Int8Array).prototype, Object.getPrototypeOf([][Symbol.iterator]()), Object.getPrototypeOf(Object.getPrototypeOf([][Symbol.iterator]())), Object.getPrototypeOf(new Map()[Symbol.iterator]()), Object.getPrototypeOf(/a/[Symbol.matchAll](\"\"))].every((object) => Object.isFrozen(object));"),
                "true", "instance intrinsics frozen");
            Ok(await Run("const object = {};\nobject.toString = () => \"custom\";\nfunction Legacy() {}\nLegacy.prototype = Object.create(Error.prototype);\nLegacy.prototype.constructor = Legacy;\nconst bare = new Error();\nbare.message = \"set later\";\nclass MyError extends Error { constructor(message) { super(message); this.name = \"MyError\"; } }\nlet patched = \"silent\";\ntry { Error.prototype.name = \"Patched\"; } catch (error) { patched = error.constructor.name; }\nreturn [String(object), new Legacy().constructor === Legacy, bare.message, new MyError(\"x\").name, Error.prototype.name, patched];"),
                "[\"custom\",true,\"set later\",\"MyError\",\"Error\",\"TypeError\"]", "override mistake avoided");
            Ok(await Run("try { tools.echo = () => 'nope'; } catch {}\ntry { tools.extra = () => 'nope'; } catch {}\ntry { globalThis.tools = null; } catch {}\nreturn [\"extra\" in tools, await tools.echo('still')];", [Echo]),
                "[false,\"still\"]", "tools frozen");
            // Jint shim: Atomics.wait would block the engine thread past cancellation, so it does not exist.
            Ok(await Run("return [typeof Atomics, typeof Atomics.wait, typeof Atomics.waitAsync, typeof Atomics.add];"), "[\"object\",\"undefined\",\"undefined\",\"function\"]", "no blocking Atomics");
        }),
        Case("sandbox.parallel-executions-share-nothing", async () =>
        {
            await using var sandbox = new CodemodeSandbox();
            var results = await Task.WhenAll(sandbox.ExecuteAsync("globalThis.shared = 'a'; await null; return globalThis.shared"),
                sandbox.ExecuteAsync("globalThis.shared = 'b'; await null; return globalThis.shared"), sandbox.ExecuteAsync("return typeof globalThis.shared"));
            Names(["\"a\"", "\"b\"", "\"undefined\""], results.Select(result => Json(result.Value)), "isolated engines");
        }),
    ];
}
