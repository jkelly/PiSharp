using System.Collections.Immutable;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Cli.Diagnostics;
using PiSharp.CodingAgent.Diagnostics;
using PiSharp.Contracts;
using PiSharp.Sessions.Serialization;

// Crash log, bug report, bug report upload, zip archive and startup timings: Pi v1.1.0 packages/coding-agent/src/core/
// {crash-log,bug-report,bug-report-upload,timings}.ts and src/utils/zip.ts. The crash attribution and redaction groups port
// test/crash-log.test.ts and the redaction half of test/bug-report.test.ts; the rest is authored from the source. Files go to
// fresh temp directories and HTTP goes to an in-process fake handler.
internal static partial class Program
{
    private static readonly DateTimeOffset DiagnosticsNow = new(2026, 10, 8, 13, 45, 31, TimeSpan.Zero);
    private static ManualTimeProvider DiagnosticsClock() => new(DiagnosticsNow.ToUnixTimeMilliseconds());

    private static DiagnosticExtensionInfo PackageExtension(string source, string baseDir, string entry = "extensions/index.ts")
    {
        var resolvedPath = $"{baseDir.Replace('\\', '/')}/{entry}";
        return new(resolvedPath, resolvedPath, source, "user", "package", baseDir);
    }

    private static IEnumerable<(string, Func<Task>)> CrashLogCases()
    {
        yield return Case("crash-log/matches-frames-beneath-package-roots", () =>
        {
            var memory = PackageExtension("npm:pi-observational-memory", "/home/fedora/.pi/agent/npm/node_modules/pi-observational-memory");
            var unrelated = PackageExtension("npm:unrelated", "/home/fedora/.pi/agent/npm/node_modules/unrelated");
            var stack = "TypeError: Cannot read properties of undefined (reading 'runtime')\n" +
                "    at streamSimple (file:///home/fedora/.local/lib/node_modules/@earendil-works/pi-coding-agent/dist/bundle/chunks/chunk-CMRUVXTE.js:1093:16944)\n" +
                "    at /home/fedora/.pi/agent/npm/node_modules/pi-observational-memory/src/agents/worker-stream.ts:43:45";
            Equal("npm:pi-observational-memory", string.Join("|", CrashLog.FindExtensionStackMatches(stack, [memory, unrelated])), "matches");
        });
        yield return Case("crash-log/windows-paths-and-dedup", () =>
        {
            var root = "C:\\Users\\reporter\\.pi\\agent\\npm\\node_modules\\@scope\\memory";
            var stack = "Error: broken\n    at run (c:\\users\\reporter\\.pi\\agent\\npm\\node_modules\\@scope\\memory\\src\\worker.ts:4:2)";
            Equal("npm:@scope/memory", string.Join("|", CrashLog.FindExtensionStackMatches(stack,
                [PackageExtension("npm:@scope/memory", root, "extensions/first.ts"), PackageExtension("npm:@scope/memory", root, "extensions/second.ts")])), "matches");
        });
        yield return Case("crash-log/sibling-single-file-packages", () =>
        {
            static DiagnosticExtensionInfo Extension(string name) => new($"/plugins/{name}.ts", $"/plugins/{name}.ts", $"/plugins/{name}.ts", "user", "package", "/plugins");
            Equal("/plugins/b.ts", string.Join("|", CrashLog.FindExtensionStackMatches("Error: broken\n    at run (file:///plugins/b.ts:4:2)", [Extension("a"), Extension("b")])), "matches");
        });
        yield return Case("crash-log/ignores-paths-in-the-error-message", () =>
        {
            var extension = PackageExtension("npm:memory", "/tmp/node_modules/memory");
            Equal(0, CrashLog.FindExtensionStackMatches($"Error: Failed to read {extension.ResolvedPath}\n    at run (file:///opt/pi/dist/core.js:4:2)", [extension]).Count, "none");
            Equal(0, CrashLog.FindExtensionStackMatches(null, [extension]).Count, "no stack");
        });
        yield return Case("crash-log/decodes-frames-independently", () =>
        {
            var path = "/Users/reporter/.pi/agent/extensions/local memory/index.ts";
            var extension = new DiagnosticExtensionInfo(path, path, "local", "user", "top-level", "/Users/reporter/.pi/agent/extensions");
            Equal(path, string.Join("|", CrashLog.FindExtensionStackMatches(
                "Error: progress 100%\n    at run (file:///Users/reporter/.pi/agent/extensions/local%20memory/worker.ts:4:2)", [extension])), "matches");
            Equal("a%2Fb c", CrashLog.DecodeUri("a%2Fb%20c"), "reserved escapes stay encoded");
            Throws<FormatException>(() => CrashLog.DecodeUri("100%"), "malformed escape");
            Equal("é", CrashLog.DecodeUri("%C3%A9"), "utf-8");
        });
        yield return Case("crash-log/extension-hint-and-texts", () =>
        {
            Equal(null, CrashLog.FormatCrashExtensionHint([]), "none");
            Equal("A stack frame came from loaded extension `a`, which may be involved. Try disabling it with `pi config`, or run `pi -ne` to confirm.",
                CrashLog.FormatCrashExtensionHint(["a", ""]), "one");
            Equal("A stack frame came from loaded extensions `a` and `b`, which may be involved. Try disabling them with `pi config`, or run `pi -ne` to confirm.",
                CrashLog.FormatCrashExtensionHint(["a", "b"]), "two");
            Equal("A stack frame came from loaded extensions `a`, `b`, and `c`, which may be involved. Try disabling them with `pi config`, or run `pi -ne` to confirm.",
                CrashLog.FormatCrashExtensionHint(["a", "b", "c"]), "three");
            Equal("To report this crash: run `pi -r` to resume the session, then run /bug. The crash details are attached automatically.",
                CrashLog.CrashReportInstructions("/s.jsonl"), "with session");
            Equal("To report this crash: start pi and run /bug. The crash details are attached automatically.", CrashLog.CrashReportInstructions(null), "without session");
        });
        yield return Case("crash-log/record-read-take-clear", () =>
        {
            var path = Path.Combine(Temp("crash"), "agent", CrashLog.FileName);
            var record = CrashLog.RecordCrash(CrashRecord.FatalError, "boom", null, "/work", path, timeProvider: DiagnosticsClock());
            Check(record is not null, "recorded");
            Equal("[\n  {\n    \"timestamp\": \"2026-10-08T13:45:31.000Z\",\n    \"version\": \"1.1.0\",\n    \"kind\": \"fatal_error\",\n    \"message\": \"boom\",\n" +
                "    \"stack\": null,\n    \"sessionFile\": null,\n    \"cwd\": \"/work\"\n  }\n]\n", File.ReadAllText(path), "file text");
            var thrown = CrashLog.RecordCrash(CrashRecord.UncaughtException, new InvalidOperationException("bad"), "/s.jsonl", "/work", path, timeProvider: DiagnosticsClock());
            Equal(("bad", "/s.jsonl", "uncaught_exception"), (thrown!.Message, thrown.SessionFile, thrown.Kind), "exception record");
            Check(thrown.Stack!.StartsWith("System.InvalidOperationException: bad", StringComparison.Ordinal), "stack names the type and message");
            Equal("Exception", CrashLog.RecordCrash(CrashRecord.FatalError, new Exception(""), null, "/w", path)!.Message, "empty message uses the name");
            for (var index = 0; index < 5; index++) CrashLog.RecordCrash(CrashRecord.FatalError, "m" + index, null, "/w", path);
            Equal("m0,m1,m2,m3,m4", string.Join(",", CrashLog.ReadCrashLog(path).Select(item => item.Message)), "newest five kept");
            CrashLog.ClearCrashLog(path); CrashLog.ClearCrashLog(path);
            Check(!File.Exists(path) && CrashLog.ReadCrashLog(path).Count == 0, "cleared");
            Check(CrashLog.RecordCrash(CrashRecord.FatalError, "x", null, "/w", Path.Combine(path, "\0bad")) is null, "best effort");
        });
        yield return Case("crash-log/read-filter-and-take-unnotified", () =>
        {
            var directory = Temp("crash-take"); var path = Path.Combine(directory, CrashLog.FileName);
            var now = DiagnosticsNow.ToUnixTimeMilliseconds();
            string Iso(double days) => DiagnosticsNow.AddDays(-days).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
            File.WriteAllText(path, $$$"""[{"timestamp":"{{{Iso(8)}}}","message":"old","extra":1},{"timestamp":"{{{Iso(2)}}}","message":"recent","future":{"a":1}},{"timestamp":"{{{Iso(1)}}}","message":"seen","notified":true},{"timestamp":1,"message":"x"},"str",null]""");
            Equal("old,recent,seen", string.Join(",", CrashLog.ReadCrashLog(path).Select(item => item.Message)), "valid records");
            var crash = CrashLog.TakeUnnotifiedCrash(path, now);
            Equal("recent", crash?.Message, "newest unannounced crash within seven days");
            Equal("old:True,recent:True,seen:True", string.Join(",", CrashLog.ReadCrashLog(path).Select(item => $"{item.Message}:{item.Notified}")), "all marked");
            Check(File.ReadAllText(path).Contains("\"future\": {\n      \"a\": 1\n    },\n    \"notified\": true", StringComparison.Ordinal), "unknown fields kept, notified appended");
            Equal(null, CrashLog.TakeUnnotifiedCrash(path, now), "announced once");
            Equal("pi crashed on today (recent). Run /bug to report it; the crash details are attached automatically.", CrashLog.CrashNotice(crash!, "today"), "notice");
            File.WriteAllText(path, "{\"timestamp\":\"t\",\"message\":\"m\"}");
            Equal(0, CrashLog.ReadCrashLog(path).Count, "not an array");
            File.WriteAllText(path, "not json");
            Equal(0, CrashLog.ReadCrashLog(path).Count, "not json");
        });
        yield return Case("crash-log/host-default-path-and-uncaught-report", () =>
        {
            var agent = Temp("crash-agent");
            Equal(Path.Combine(agent, "crashes.json"), CrashReporting.DefaultCrashLogPath(new Dictionary<string, string?> { ["PI_CODING_AGENT_DIR"] = agent }, "/unused"), "env agent dir");
            var home = Temp("crash-home");
            Equal(Path.Combine(home, ".pi", "agent", "crashes.json"), CrashReporting.DefaultCrashLogPath(new Dictionary<string, string?>(), home), "home default");
            var path = Path.Combine(agent, "crashes.json"); var error = new StringWriter();
            Exception thrown;
            try { throw new InvalidOperationException("kaput"); } catch (InvalidOperationException caught) { thrown = caught; }
            CrashReporting.ReportUncaughtException(thrown, error, [], null, "/work", path);
            var text = error.ToString();
            Check(text.StartsWith("pi exiting due to uncaughtException:\nSystem.InvalidOperationException: kaput", StringComparison.Ordinal), text);
            Check(text.EndsWith("\n\nTo report this crash: start pi and run /bug. The crash details are attached automatically.\n", StringComparison.Ordinal), text);
            Equal(("kaput", "uncaught_exception"), (CrashLog.ReadCrashLog(path)[0].Message, CrashLog.ReadCrashLog(path)[0].Kind), "recorded");
        });
    }

    private static IEnumerable<(string, Func<Task>)> BugReportRedactionCases()
    {
        yield return Case("bug-report/redacts-url-credentials-and-secret-params", () =>
        {
            Equal("https://proxy.example.com:8080/", BugReportRedaction.RedactUrl("https://user:pass@proxy.example.com:8080/"), "credentials");
            Equal("git:https://github.com/org/repo", BugReportRedaction.RedactUrl("git:https://pat@github.com/org/repo"), "nested scheme");
            Equal("https://api.example/v1?api-key=%3Credacted%3E&model=x", BugReportRedaction.RedactUrl("https://api.example/v1?api-key=abc&model=x"), "query");
        });
        yield return Case("bug-report/redacts-nested-json-secrets", () =>
        {
            var redacted = BugReportRedaction.RedactJsonValue(JsonData.Parse("""{"apiKey":"sk-123","headers":{"Authorization":"Bearer x","X-Trace":"1"},"compaction":{"reserveTokens":16384,"keepRecentTokens":20000},"baseUrl":"https://me:secret@example.com/"}"""));
            Equal("""{"apiKey":"<redacted>","headers":{"Authorization":"<redacted>","X-Trace":"1"},"compaction":{"reserveTokens":16384,"keepRecentTokens":20000},"baseUrl":"https://example.com/"}""",
                redacted.ToString(), "redacted");
        });
        yield return Case("bug-report/sensitive-keys", () =>
        {
            var keys = new[] { "apiKey", "api_key", "x-api-key", "API-KEY", "Authorization", "sessionToken", "token", "clientSecret", "password", "passwd",
                "cookie", "credentials", "reserveTokens", "keepRecentTokens", "tokenizer", "maxTokens", "secretary", "author" };
            Equal("apiKey,api_key,x-api-key,API-KEY,Authorization,sessionToken,token,clientSecret,password,passwd,cookie",
                string.Join(",", keys.Where(BugReportRedaction.IsSensitiveKey)), "sensitive");
        });
        yield return Case("bug-report/url-normalization-when-changed", () =>
        {
            Equal("https://example.com/b?x=1", BugReportRedaction.RedactUrl("HTTPS://u:p@Example.COM:443/a/../b?x=1"), "scheme, host, default port, dot segments");
            Equal("http://example.com/?token=%3Credacted%3E&x=1", BugReportRedaction.RedactUrl("http://example.com?token=a&token=b&x=1"), "duplicates removed, empty path");
            Equal("https://h/p?q=a+b&secret=%3Credacted%3E#frag", BugReportRedaction.RedactUrl("https://h/p?q=a%20b&secret=s#frag"), "form re-serialization");
            Equal("http://192.168.0.1/?token=%3Credacted%3E", BugReportRedaction.RedactUrl("http://0xC0.168.0.1?token=1"), "ipv4 canonicalized");
            Equal("npm:pkg?api_key=%3Credacted%3E", BugReportRedaction.RedactUrl("npm:pkg?api_key=1"), "opaque path");
            Equal("ssh://host/repo", BugReportRedaction.RedactUrl("ssh://git@host/repo"), "non-special authority credentials");
        });
        yield return Case("bug-report/non-urls-and-unchanged-urls-stay-verbatim", () =>
        {
            foreach (var value in new[] { "sk-123", "Bearer x", "", "HTTPS://Example.COM/a/../b", "http://exa mple.com/?token=1", "https://:8080/", "1" })
                Equal(value, BugReportRedaction.RedactUrl(value), "verbatim " + value);
            var redacted = BugReportRedaction.RedactJsonValue(JsonNode.Parse("""{"list":["https://u:p@h/",{"token":null,"Token":1}],"nested":{"cookie":{"a":1}}}"""));
            Equal("""{"list":["https://h/",{"token":null,"Token":"<redacted>"}],"nested":{"cookie":"<redacted>"}}""", redacted!.ToJsonString(Relaxed), "arrays, null and object values");
            Equal("\"https://h/\"", BugReportRedaction.RedactJsonValue(JsonNode.Parse("\"https://a@h/\""))!.ToJsonString(), "root string");
        });
    }

    private static readonly SessionEntryCodec DiagnosticsCodec = new();

    private static BugReportMetadataOptions MetadataOptions() => new("sess-1", "/work", true, false, 4, "high",
        JsonData.Parse("""{"theme":"dark","trackingId":"t","deviceId":"d","apiKey":"sk","compaction":{"reserveTokens":16384}}"""), JsonData.Parse("{}"))
    {
        Id = "id-1", Hint = "  it broke \n",
        Model = JsonData.Parse("""{"id":"m","name":"M","api":"openai-completions","provider":"p","baseUrl":"https://u:pw@api.example/v1?key=1&token=abc","reasoning":false,"input":["text"],"cost":{"input":1,"output":2,"cacheRead":0,"cacheWrite":0},"contextWindow":1000,"maxTokens":100,"headers":{"X-B":"1","X-A":"2"},"compat":{"apiKey":"k","supportsStore":false}}"""),
        Provider = new("p", "Provider P", "https://proxy.example/", ["b", "a"], true, true, JsonData.Parse("""{"configured":true,"source":"stored"}"""), false, true),
        Extensions = [new("/ext/a.ts", "/ext/a.ts", "git:https://pat@github.com/o/r", "user", "package")],
        ExtensionErrors = [("/x.ts", "boom")],
        Environment = JsonData.Parse("""{"version":"1.1.0"}"""),
        TimeProvider = DiagnosticsClock(),
    };

    private const string ExpectedMetadata = """{"schemaVersion":1,"id":"id-1","createdAt":"2026-10-08T13:45:31.000Z","hint":"it broke","environment":{"version":"1.1.0"},"session":{"id":"sess-1","included":true,"summaryIncluded":false,"messageCount":4,"cwd":"/work"},"model":{"provider":"p","id":"m","name":"M","api":"openai-completions","baseUrl":"https://api.example/v1?key=1&token=%3Credacted%3E","reasoning":false,"input":["text"],"contextWindow":1000,"maxTokens":100,"samplingParams":null,"compat":{"apiKey":"<redacted>","supportsStore":false},"thinkingLevelMap":null,"headerNames":["X-A","X-B"]},"provider":{"id":"p","name":"Provider P","baseUrl":"https://proxy.example/","headerNames":["a","b"],"authTypes":["api_key","oauth"],"authStatus":{"configured":true,"source":"stored"},"usingOAuth":false,"registeredByExtension":true},"thinkingLevel":"high","extensions":[{"path":"/ext/a.ts","source":"git:https://github.com/o/r","scope":"user","origin":"package","hidden":false}],"extensionErrors":[{"path":"/x.ts","error":"boom"}],"settings":{"global":{"theme":"dark","apiKey":"<redacted>","compaction":{"reserveTokens":16384}},"project":{}}}""";

    private static BugReportBundle SampleBundle(string? session = "{\"type\":\"session\"}\n", string? summary = "## Summary") =>
        new(BugReport.CollectBugReportMetadata(MetadataOptions()), JsonData.Parse("""{"schemaVersion":1,"crashes":[]}"""), session, summary);

    private static TranscriptEntry Transcript(string json) => new(JsonDocument.Parse(json).RootElement.GetProperty("role").GetString()!, JsonData.Parse(json));

    private static IEnumerable<(string, Func<Task>)> BugReportCases()
    {
        yield return Case("bug-report/metadata", () =>
        {
            Equal(ExpectedMetadata, BugReport.CollectBugReportMetadata(MetadataOptions()).ToString(), "report.json");
            var minimal = BugReport.CollectBugReportMetadata(MetadataOptions() with { IncludeSession = false, Model = null, Hint = "   ", Id = null }).Value;
            Equal((JsonValueKind.Null, JsonValueKind.Null, JsonValueKind.Null, false), (minimal.GetProperty("hint").ValueKind, minimal.GetProperty("model").ValueKind,
                minimal.GetProperty("provider").ValueKind, minimal.GetProperty("session").TryGetProperty("cwd", out _)), "no model, provider, hint or cwd");
            Check(Guid.TryParse(minimal.GetProperty("id").GetString(), out var id) && id.Version == 7, "uuidv7 id");
        });
        yield return Case("bug-report/environment", () =>
        {
            var environment = BugReport.CollectEnvironment(new Dictionary<string, string?>
            {
                ["PI_B"] = "secret", ["PI_A"] = "x", ["PATH"] = "/bin", ["SHELL"] = "/usr/bin/zsh", ["TERM"] = "xterm-256color", ["TMUX"] = "1",
                ["SSH_TTY"] = "/dev/pts/1", ["CI"] = "", ["COLORTERM"] = "",
            }).Value;
            Equal("version,userAgent,runtime,platform,arch,osRelease,osVersion,shell,terminal,piEnvironmentVariables",
                string.Join(",", environment.EnumerateObject().Select(property => property.Name)), "keys");
            Equal(("1.1.0", BugReport.PiUserAgent("1.1.0"), "zsh"), (environment.GetProperty("version").GetString(), environment.GetProperty("userAgent").GetString(),
                environment.GetProperty("shell").GetString()), "version, user agent and shell");
            Equal("""{"term":"xterm-256color","program":null,"programVersion":null,"colorterm":null,"tmux":true,"ssh":true,"ci":false}""",
                environment.GetProperty("terminal").GetRawText(), "terminal");
            Equal("""["PI_A","PI_B"]""", environment.GetProperty("piEnvironmentVariables").GetRawText(), "names only, sorted");
            Check(environment.GetProperty("runtime").GetString()!.StartsWith("dotnet/", StringComparison.Ordinal), "runtime");
            Check(BugReport.PiUserAgent("1.1.0").StartsWith($"pi/1.1.0 ({BugReport.Platform}; dotnet/", StringComparison.Ordinal), "user agent shape");
            Equal(JsonValueKind.Null, BugReport.CollectEnvironment(new Dictionary<string, string?>()).Value.GetProperty("shell").ValueKind, "no shell");
        });
        yield return Case("bug-report/diagnostics", () =>
        {
            SessionEntry Parse(string json) => DiagnosticsCodec.Parse(json);
            var crashPath = Path.Combine(Temp("diag"), CrashLog.FileName);
            File.WriteAllText(crashPath, """[{"timestamp":"t","message":"m","notified":true,"cwd":"/w"}]""");
            var entries = new[]
            {
                Parse("""{"type":"session","version":3,"id":"h","timestamp":"T","cwd":"/w"}"""),
                Parse("""{"type":"message","id":"u","parentId":null,"timestamp":"T","message":{"role":"user","content":"hi","timestamp":1}}"""),
                AssistantEntry("e1", UsageJson(), entryTimestamp: "T1"),
                AssistantEntry("e2", UsageJson(), stopReason: "error", extra: ",\"rawStopReason\":\"overloaded\",\"errorMessage\":\"rate limited\"", entryTimestamp: "T2"),
                AssistantEntry("e3", UsageJson(), extra: ",\"diagnostics\":[{\"type\":\"x\"}]", entryTimestamp: "T3"),
            };
            Equal("""{"schemaVersion":1,"sessionId":"s","entryCount":4,"assistantMessageCount":3,"assistant":[{"entryId":"e2","timestamp":"T2","provider":"test","model":"test-model","api":"anthropic-messages","stopReason":"error","rawStopReason":"overloaded","errorMessage":"rate limited","diagnostics":[]},{"entryId":"e3","timestamp":"T3","provider":"test","model":"test-model","api":"anthropic-messages","stopReason":"stop","diagnostics":[{"type":"x"}]}],"crashes":[{"timestamp":"t","message":"m","cwd":"/w"}]}""",
                BugReport.CollectBugReportDiagnostics("s", entries, CrashLog.ReadCrashLog(crashPath)).ToString(), "diagnostics.json");
        });
        yield return Case("bug-report/files-archive-and-entry-data", async () =>
        {
            var bundle = SampleBundle();
            var files = BugReport.BugReportFiles(bundle);
            Equal("report.json:application/json,diagnostics.json:application/json,session.jsonl:application/x-ndjson,summary.md:text/markdown",
                string.Join(",", files.Select(file => file.Name + ":" + file.ContentType)), "files");
            Check(files[0].Data.StartsWith("{\n  \"schemaVersion\": 1,\n  \"id\": \"id-1\",", StringComparison.Ordinal) && files[0].Data.EndsWith("}\n", StringComparison.Ordinal), files[0].Data);
            Equal("{\n  \"schemaVersion\": 1,\n  \"crashes\": []\n}\n", files[1].Data, "pretty diagnostics");
            Equal("## Summary\n", files[3].Data, "summary newline");
            Equal(2, BugReport.BugReportFiles(SampleBundle(null, null)).Count, "optional files");
            Equal("pi-bug-report-id-1.zip", BugReport.BugReportArchiveFileName("id-1"), "archive name");
            Equal("""{"id":"id-1","createdAt":"2026-10-08T13:45:31.000Z","hint":"it broke","sessionIncluded":true,"summaryIncluded":false,"delivery":"zip","path":"/r.zip"}""",
                BugReport.BugReportSessionEntryData(bundle, "zip", "/r.zip").ToString(), "entry data");
            Equal("""{"id":"id-1","createdAt":"2026-10-08T13:45:31.000Z","hint":"it broke","sessionIncluded":true,"summaryIncluded":false,"delivery":"upload"}""",
                BugReport.BugReportSessionEntryData(bundle, "upload").ToString(), "upload entry data");
            Equal("pi.bug-report", BugReport.CustomEntryType, "custom type");
            var archive = Path.Combine(Temp("bug-zip"), BugReport.BugReportArchiveFileName(bundle.Id));
            await BugReport.WriteBugReportArchiveAsync(bundle, archive, DiagnosticsClock());
            using var zip = ZipFile.OpenRead(archive);
            Equal(string.Join(",", files.Select(file => file.Name)), string.Join(",", zip.Entries.Select(entry => entry.FullName)), "entry order");
            foreach (var (entry, file) in zip.Entries.Zip(files))
            {
                using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                Equal(file.Data, reader.ReadToEnd(), "content of " + file.Name);
            }
        });
        yield return Case("bug-report/zip-layout", () =>
        {
            var bytes = ZipArchiveWriter.CreateZipArchive([new("a.txt", "hello"), new("é.txt", new byte[] { 1, 2 })], DiagnosticsClock());
            ushort U16(int at) => BitConverter.ToUInt16(bytes, at);
            uint U32(int at) => BitConverter.ToUInt32(bytes, at);
            Equal((0x04034b50u, (ushort)20, (ushort)0x0800, (ushort)8), (U32(0), U16(4), U16(6), U16(8)), "local header");
            // 13:45:31 -> (13 << 11) | (45 << 5) | (31 >> 1); 2026-10-08 -> ((2026 - 1980) << 9) | (10 << 5) | 8.
            Equal(((ushort)28079, (ushort)23880, 0x3610A686u, 5u, (ushort)5), (U16(10), U16(12), U32(14), U32(22), U16(26)), "time, date, crc, size, name length");
            var end = bytes.Length - 22;
            Equal((0x06054b50u, (ushort)2, (ushort)2), (U32(end), U16(end + 8), U16(end + 10)), "end record");
            var central = (int)U32(end + 16);
            Equal((0x02014b50u, (ushort)20, (ushort)20, 0u), (U32(central), U16(central + 4), U16(central + 6), U32(central + 42)), "first central record");
            Equal((ushort)6, U16(central + 46 + 5 + 28), "utf-8 name length of the second central record");
            Equal((ushort)33, ZipArchiveWriter.DosDateTime(new DateTime(1970, 1, 1)).Day, "years before 1980 clamp");
        });
        yield return Case("bug-report/summary-request", () =>
        {
            var messages = ImmutableArray.Create(
                Transcript("""{"role":"user","content":"hello","timestamp":1}"""),
                Transcript("""{"role":"assistant","content":[{"type":"text","text":"world!"}],"api":"a","provider":"p","model":"m","usage":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0,"totalTokens":0,"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0,"total":0}},"stopReason":"stop","timestamp":2}"""),
                Transcript($$"""{"role":"user","content":"{{new string('x', 400)}}","timestamp":3}"""));
            var request = BugReport.CreateBugReportSummaryRequest(new(messages, 170, 0, true) { Hint = " it broke ", ThinkingLevel = "high", SessionId = "sess" });
            Equal("Note: only the last 2 of 3 messages are shown.\n\n<conversation>\n[Assistant]: world!\n\n[User]: " + new string('x', 400) +
                "\n</conversation>\n\n<user-report>\nit broke\n</user-report>\n\n" + BugReport.SummaryInstructions, request.Prompt, "prompt");
            Equal((4096d, "high", "sess", "none"), (request.MaxTokens, request.Reasoning, request.SessionId, request.CacheRetention), "options");
            Check(request.SystemPrompt.StartsWith("You are helping a user file a bug report about pi", StringComparison.Ordinal) &&
                request.SystemPrompt.EndsWith("ONLY output the report.", StringComparison.Ordinal), "system prompt");
            var all = BugReport.CreateBugReportSummaryRequest(new(messages, 0, 1000, false) { ThinkingLevel = "high" });
            Check(all.Prompt.StartsWith("<conversation>\n[User]: hello\n\n[Assistant]: world!", StringComparison.Ordinal) && !all.Prompt.Contains("<user-report>"), all.Prompt);
            Equal((1000d, (string?)null), (all.MaxTokens, all.Reasoning), "model cap, no reasoning");
            Check(Guid.TryParse(all.SessionId, out var session) && session.Version == 7, "fresh routing id");
            Equal(1, BugReport.CreateBugReportSummaryRequest(new([Transcript($$"""{"role":"user","content":"{{new string('y', 4000)}}","timestamp":1}""")], 100, 0, false))
                .Prompt.Split("[User]").Length - 1, "the newest message is always kept");
        });
        yield return Case("bug-report/summary-generation", async () =>
        {
            var options = new BugReportSummaryOptions([Transcript("""{"role":"user","content":"hello","timestamp":1}""")], 1000, 0, false);
            static AssistantMessage Response(StopReason stopReason, JsonFields? extras = null, params AssistantContent[] content) =>
                new("a", "p", "m", 0, [.. content], TokenUsage.Zero, stopReason, extras);
            Task<string> Run(AssistantMessage response) => BugReport.GenerateBugReportSummaryAsync(options, (_, _) => Task.FromResult(response));
            Equal("## Report\nbody", await Run(Response(StopReason.Stop, null, new TextContent("  ## Report"), new ThinkingContent("t"), new TextContent("body\n"))), "text joined and trimmed");
            async Task Fails(AssistantMessage response, string message) =>
                Equal(message, (await ThrowsAsync<InvalidOperationException>(() => Run(response), message)).Message, message);
            await Fails(Response(StopReason.Aborted), "Bug report summary was cancelled");
            await Fails(Response(StopReason.Error, JsonFields.Empty.Set("errorMessage", JsonData.Parse("\"overloaded\""))), "Bug report summary failed: overloaded");
            await Fails(Response(StopReason.Error), "Bug report summary failed: Unknown error");
            await Fails(Response(StopReason.Length), "Bug report summary failed: generation hit the token cap and the summary is incomplete");
            await Fails(Response(StopReason.ToolUse, null, new ToolCallContent("c", "read", JsonData.Parse("{}"))), "Bug report summary attempted to call a tool");
            await Fails(Response(StopReason.Stop, null, new TextContent(" \n ")), "Bug report summary was empty");
        });
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        internal readonly List<(HttpRequestMessage Request, string Body)> Requests = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request, body));
            return respond(request, body);
        }
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string body, string? reason = null) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json"), ReasonPhrase = reason };

    private static IEnumerable<(string, Func<Task>)> BugReportUploadCases()
    {
        yield return Case("bug-report-upload/multipart-request-and-id", async () =>
        {
            var handler = new FakeHandler((_, _) => JsonResponse(HttpStatusCode.OK, """{"ok":true,"bug_report":{"id":"br_1"}}"""));
            using var client = new HttpClient(handler);
            var bundle = SampleBundle();
            Equal("br_1", await BugReportUpload.UploadBugReportAsync(bundle, new("tok", "https://gw.example/base/"), client), "id");
            var (request, body) = handler.Requests[0];
            Equal(("POST", "https://gw.example/v1/bug-reports", "Bearer tok"), (request.Method.Method, request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString()), "request");
            var contentType = request.Content!.Headers.ContentType!.ToString();
            Check(contentType.StartsWith("multipart/form-data; boundary=----formdata-undici-0", StringComparison.Ordinal), contentType);
            var boundary = contentType[(contentType.IndexOf('=') + 1)..];
            var files = BugReport.BugReportFiles(bundle);
            var expected = string.Concat(files.Select(file => $"--{boundary}\r\nContent-Disposition: form-data; name=\"{file.Name}\"; filename=\"{file.Name}\"\r\nContent-Type: {file.ContentType}\r\n\r\n{file.Data}\r\n")) + $"--{boundary}--\r\n";
            Equal(expected, body, "multipart body");
        });
        yield return Case("bug-report-upload/anonymous-default-gateway", async () =>
        {
            var handler = new FakeHandler((_, _) => JsonResponse(HttpStatusCode.Created, """{"ok":true,"bug_report":{"id":"x"}}"""));
            using var client = new HttpClient(handler);
            Equal("x", await BugReportUpload.UploadBugReportAsync(SampleBundle(), null, client, _ => null), "id");
            Equal(("https://radius.pi.dev/v1/bug-reports", false), (handler.Requests[0].Request.RequestUri!.AbsoluteUri, handler.Requests[0].Request.Headers.Contains("Authorization")), "anonymous");
            Equal("https://gw.local", BugReportUpload.GetRadiusGatewayUrl(name => name == "PI_RADIUS_GATEWAY" ? "gw.local//" : null), "env override normalized");
            Equal("http://127.0.0.1:9", BugReportUpload.GetRadiusGatewayUrl(_ => "http://127.0.0.1:9/"), "explicit scheme kept");
        });
        yield return Case("bug-report-upload/failure-texts", async () =>
        {
            async Task<string> Failure(HttpResponseMessage response)
            {
                using var client = new HttpClient(new FakeHandler((_, _) => response));
                return (await ThrowsAsync<BugReportUploadException>(() => BugReportUpload.UploadBugReportAsync(SampleBundle(), new(GatewayUrl: "https://gw"), client), "failure")).Message;
            }
            Equal("Bug report upload failed: too large", await Failure(JsonResponse(HttpStatusCode.BadRequest, """{"ok":false,"error":"invalid","description":"too large"}""", "Bad Request")), "description");
            Equal("Bug report upload failed: invalid", await Failure(JsonResponse(HttpStatusCode.BadRequest, """{"ok":false,"error":"invalid","description":""}""", "Bad Request")), "error");
            Equal("Bug report upload failed: Bad Gateway", await Failure(new(HttpStatusCode.BadGateway) { Content = new StringContent("<html>"), ReasonPhrase = "Bad Gateway" }), "status text");
            Equal("Bug report upload failed: 503", await Failure(new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent(""), ReasonPhrase = "" }), "status code");
            Equal("Bug report upload failed: OK", await Failure(JsonResponse(HttpStatusCode.OK, """{"ok":true}""", "OK")), "ok without id");
            Equal("Bug report upload failed: Unauthorized", await Failure(JsonResponse(HttpStatusCode.Unauthorized, """{"ok":true,"description":"ignored"}""", "Unauthorized")), "ok flag hides detail");
            Equal("Bug report upload failed: 42", await Failure(JsonResponse(HttpStatusCode.BadRequest, """{"description":42}""", "Bad Request")), "non-string detail");
        });
    }

    private static IEnumerable<(string, Func<Task>)> StartupTimingsCases()
    {
        yield return Case("timings/prints-namespaces-to-stderr", () =>
        {
            var clock = new ManualTimeProvider(1000); var error = new StringWriter();
            var timings = new StartupTimings(true, clock, error);
            timings.ResetTimings();
            clock.SetUtcNow(1005); timings.Time("a");
            clock.SetUtcNow(1012); timings.Time("b");
            clock.SetUtcNow(1020); timings.Time("ext", StartupTimings.Extensions);
            timings.PrintTimings();
            Equal("\n--- Startup Timings: main ---\n  a: 5ms\n  b: 7ms\n  TOTAL: 12ms\n" + new string('-', 29) + "\n\n" +
                "\n--- Startup Timings: extensions ---\n  ext: 0ms\n  TOTAL: 0ms\n" + new string('-', 35) + "\n\n", error.ToString(), "output");
            var disabled = new StringWriter(); var off = new StartupTimings(false, clock, disabled);
            off.Time("x"); off.PrintTimings();
            Equal("", disabled.ToString(), "disabled");
            var empty = new StringWriter(); var none = new StartupTimings(true, clock, empty);
            none.ResetTimings(); none.PrintTimings();
            Equal("", empty.ToString(), "groups without timings print nothing");
        });
    }
}
