using PiSharp.CustomAgents.Scripting;
using PiSharp.CustomAgents.Teams;
using PiSharp.CustomAgents.Workflows;
using PiSharp.Durable;
using PiSharp.Durable.Storage;

/// <summary>Arguments of a script tool (script types must be public).</summary>
public sealed record CsGreetArgs([property: System.ComponentModel.Description("Who to greet")] string Name);

/// <summary>Globals of plain compiled scripts.</summary>
public sealed class CsGlobals
{
    public int N { get; set; }
}

/// <summary>Data of a workflow with C# script actions.</summary>
public sealed class ScriptOrder
{
    public List<string> Trail { get; set; } = [];
    public int Count { get; set; }
    public bool Vip { get; set; }
}

// Decision 0012, P11: C# scripts: tools, diagnostics, the cache, refused directives, trust, the CSharp action, and a hot-reloaded folder.
internal static partial class Program
{
    internal static IEnumerable<(string, Func<Task>)> CsCases()
    {
        yield return Case("agents.cs.script-tool-runs", async () =>
        {
            var host = new CSharpScriptHost();
            var greet = await ScriptTool.CreateAsync<CsGreetArgs>("cs_greet", "Greets", "return $\"Hello {Args.Name}\";", host);
            Check(greet.Parameters.ToJson().Contains("\"name\"", StringComparison.Ordinal), "the schema comes from the record: " + greet.Parameters.ToJson());
            var sum = await ScriptTool.CreateAsync("cs_sum", "Adds", "return (Args.GetProperty(\"a\").GetInt32() + Args.GetProperty(\"b\").GetInt32()).ToString();",
                Schema("""{"type":"object","required":["a","b"],"properties":{"a":{"type":"integer"},"b":{"type":"integer"}}}"""), host);
            var boom = await ScriptTool.CreateAsync("cs_boom", "Fails", "throw new InvalidOperationException(\"boom\");", Schema("""{"type":"object"}"""), host);
            var (team, _) = CallingTeam(new AgentDefinition { Name = "lead", Description = "Calls tools", Tools = [greet, sum, boom] });
            await using var session = await team.OpenAsync(new MemoryStorage());
            Equal("Hello Ada", (await session.AskAsync("call cs_greet {\"name\":\"Ada\"}")).Text, "the typed script tool ran");
            Equal("5", (await session.AskAsync("call cs_sum {\"a\":2,\"b\":3}")).Text, "the JSON script tool ran");
            Equal("error: Tool 'cs_boom' failed: boom", (await session.AskAsync("call cs_boom")).Text, "a throwing script is an error result");
            var script = host.Compile<CsGlobals, int>("var doubled = N * 2;\nthrow new InvalidOperationException(\"at line 2\");", "lines.csx");
            var thrown = await ThrowsAsync<InvalidOperationException>(() => script.RunAsync(new CsGlobals { N = 1 }), "the script's exception propagates");
            Check(thrown.StackTrace!.Contains("lines.csx:line 2", StringComparison.Ordinal), "stack frames name the script's own lines: " + thrown.StackTrace);
        });

        yield return Case("agents.cs.diagnostics-mapped-to-source", () =>
        {
            var host = new CSharpScriptHost();
            var missing = Throws<ScriptCompilationException>(() => host.Compile<CsGlobals, int>("using System.Text;\nvar x = N;\nreturn missing + x;", "calc.csx"), "an error");
            var error = missing.Diagnostics.Single();
            Equal("CS0103", error.Id, "the compiler's ID");
            Equal("calc.csx", error.Path, "the script's path");
            Equal(3, error.Line, "the script's line");
            Equal(8, error.Column, "the script's column");
            var badUsing = Throws<ScriptCompilationException>(() => host.Compile<CsGlobals, int>("using Nope.Missing;\nreturn 1;", "using.csx"), "a bad using");
            Check(badUsing.Diagnostics.Any(diagnostic => diagnostic is { Id: "CS0246", Line: 1, Path: "using.csx" }), "a hoisted using keeps its line: " + badUsing.Message);
            Equal(3, host.Compile<CsGlobals, int>("using System.Text;\nvar text = new StringBuilder().Append(N).ToString();\nreturn text.Length + 2;").RunAsync(new CsGlobals { N = 5 }).Result,
                "a script with a using runs");
        });

        yield return Case("agents.cs.cache-by-hash", async () =>
        {
            var directory = Temp("cs-cache");
            var first = new CSharpScriptHost(new CSharpScriptOptions { CacheDirectory = directory });
            var a = first.Compile<CsGlobals, int>("return N + 1;");
            var b = first.Compile<CsGlobals, int>("return N + 1;");
            Check(!a.FromCache && b.FromCache, "the second compile of the same code is a cache hit");
            Equal(1, first.Compilations, "compiled once");
            Equal(4, await b.RunAsync(new CsGlobals { N = 3 }), "the cached image runs");
            Check(!ReferenceEquals(a.Context.Target, b.Context.Target), "each compiled script has its own load context");
            var second = new CSharpScriptHost(new CSharpScriptOptions { CacheDirectory = directory });
            var c = second.Compile<CsGlobals, int>("return N + 1;");
            Check(c.FromCache, "another host finds the image on disk");
            Equal(0, second.Compilations, "without compiling");
            second.Compile<CsGlobals, int>("return N + 2;");
            Equal(1, second.Compilations, "other code compiles");
            Equal(4, Directory.GetFiles(directory).Length, "two images, each a dll and a pdb");
            await a.UnloadAsync();
            await ThrowsAsync<ObjectDisposedException>(() => a.RunAsync(new CsGlobals()), "an unloaded script does not run");
        });

        yield return Case("agents.cs.directives-rejected", () =>
        {
            var host = new CSharpScriptHost();
            string Refused(string code) => string.Join(",", Throws<ScriptCompilationException>(() => host.Compile<CsGlobals, int>(code), code).Diagnostics
                .Select(diagnostic => diagnostic.Id + "@" + diagnostic.Line));
            Equal("PSA0001@1", Refused("#r \"System.dll\"\nreturn 1;"), "#r");
            Equal("PSA0002@1", Refused("#load \"other.csx\"\nreturn 1;"), "#load");
            Equal("CS8097@2", Refused("using System;\n#load \"other.csx\"\nreturn 1;"), "a misplaced #load is the compiler's error");
            Equal("PSA0003@1", Refused("class Helper { }\nreturn 1;"), "a class");
            Equal("PSA0003@2", Refused("return 1;\nrecord Point(int X);"), "a record");
            Equal(6, host.Compile<CsGlobals, int>("int Twice(int n) => n * 2;\nreturn Twice(N);").RunAsync(new CsGlobals { N = 3 }).Result, "local functions are fine");
        });

        yield return Case("agents.cs.untrusted-hash-refused", async () =>
        {
            var trusted = new CSharpScriptHost(new CSharpScriptOptions { TrustedSourceHashes = [CSharpScriptHost.HashOf("return N;")] });
            var refused = Throws<ScriptTrustException>(() => trusted.Compile<CsGlobals, int>("return N * 100;"), "an unlisted script");
            Check(refused.Message.Contains(CSharpScriptHost.HashOf("return N * 100;"), StringComparison.Ordinal), "the refusal names the hash to pin");
            Equal(0, trusted.Compilations, "nothing was compiled");
            Equal(7, await trusted.Compile<CsGlobals, int>("return N;").RunAsync(new CsGlobals { N = 7 }), "a listed script runs");
        });

        yield return Case("agents.cs.action-mutates-typed-data", async () =>
        {
            var host = new CSharpScriptHost();
            var compile = Throws<ScriptCompilationException>(() => Workflow.For<ScriptOrder>("cs-bad").CSharp("bad", "Data.Nope = 1;", host), "a broken action");
            Check(compile.Diagnostics.Any(diagnostic => diagnostic is { Path: "bad.csx", Line: 1 }), "compiled when the workflow is built: " + compile.Message);
            var workflow = Workflow.For<ScriptOrder>("cs-orders")
                .CSharp("score", """
                    Data.Count = Data.Trail.Count + 1;
                    Data.Trail.Add(Context.Action + "#" + Context.Attempt);
                    if (Data.Vip) return ActionResult.GoTo("vip");
                    """, host)
                .Then("standard", data => data.Trail.Add("standard"), new ActionOptions { Next = Workflow.End })
                .Then("vip", data => data.Trail.Add("vip"))
                .Build();
            await using var test = await TestHost.OpenAsync(catalog => catalog.Add(workflow));
            var standard = (await test.Runner.RunAsync(workflow, new ScriptOrder())).EnsureCompleted();
            Equal("score#1,standard", string.Join(",", standard.Trail), "no return continues with the next action");
            Equal(1, standard.Count, "the change to the typed Data was committed");
            var vip = (await test.Runner.RunAsync(workflow, new ScriptOrder { Vip = true, Trail = ["x"] })).EnsureCompleted();
            Equal("x,score#1,vip", string.Join(",", vip.Trail), "the returned decision");
            Equal(2, vip.Count, "counted");
        });

        yield return Case("agents.cs.file-source-hot-reload-unloads", async () =>
        {
            var directory = Temp("cs-tools");
            var file = Path.Combine(directory, "greet.tool.csx");
            const string Header = """// @tool: {"description":"Greets","parameters":{"type":"object","required":["who"],"properties":{"who":{"type":"string"}}},"replay":"safe"}""";
            await File.WriteAllTextAsync(file, Header + "\nreturn \"v1 \" + Args.GetProperty(\"who\").GetString();");
            var errors = new List<Exception>();
            await using var source = new ScriptToolSource("scripts", directory, new ScriptToolSourceOptions
            {
                Host = new CSharpScriptHost(),
                Debounce = TimeSpan.FromMilliseconds(100),
                OnError = error => { lock (errors) errors.Add(error); },
            });
            await source.LoadAsync();
            Equal("greet", string.Join(",", source.Tools.Select(tool => tool.Name)), "named after the file");
            Equal(ToolReplay.Safe, source.Tools[0].Replay, "the header's replay");
            var (team, _) = CallingTeam(new AgentDefinition { Name = "lead", Description = "Calls tools", ToolSources = ["scripts"] });
            team.Catalog.AddSource(source);
            await using var session = await team.OpenAsync(new MemoryStorage());
            Equal("v1 Ada", (await session.AskAsync("call greet {\"who\":\"Ada\"}")).Text, "the file's tool runs");
            var old = source.ContextOf("greet")!;
            await File.WriteAllTextAsync(file, Header + "\nreturn \"v2 \" + Args.GetProperty(\"who\").GetString();");
            await WaitUntilAsync(() => Task.FromResult(source.ContextOf("greet") != old), TimeSpan.FromSeconds(20));
            Equal("v2 Ada", (await session.AskAsync("call greet {\"who\":\"Ada\"}")).Text, "the edit replaced the tool in place");
            await File.WriteAllTextAsync(file, Header + "\nreturn (;");
            await WaitUntilAsync(() => Task.FromResult(source.Errors.ContainsKey(file)), TimeSpan.FromSeconds(20));
            Check(errors.Any(error => error is ScriptCompilationException or ScriptToolLoadException), "the failed reload was reported");
            Equal("v2 Ada", (await session.AskAsync("call greet {\"who\":\"Ada\"}")).Text, "a broken edit keeps the last good tool");
            await source.WhenRetiredAsync();
            for (var attempt = 0; attempt < 40 && old.IsAlive; attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                await Task.Delay(50);
            }
            Check(!old.IsAlive, "the replaced script's load context was collected");
            File.Delete(file);
            await WaitUntilAsync(() => Task.FromResult(source.Tools.Count == 0), TimeSpan.FromSeconds(20));
            Equal("tools: ", (await session.AskAsync("list")).Text, "a deleted file's tool is gone");
        });
    }
}
