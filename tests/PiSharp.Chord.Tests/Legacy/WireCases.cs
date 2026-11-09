using System.Text.Json.Nodes;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Security.Cryptography;
using PiSharp.Chord;
using PiSharp.Chord.Legacy;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> LegacyWireCases()
    {
        yield return Case("legacy.wire.upstream-frame-validation", () =>
        {
            var fixture = LegacyFixture("wire-v150.json");
            Equal(LegacyProtocol.Version, fixture["protocol"]!.GetValue<int>(), "protocol pin");
            foreach (var sample in fixture["fixtures"]!.AsArray())
                Equal(sample!["valid"]!.GetValue<bool>(), LegacyProtocol.IsFrame(sample["frame"]), sample["id"]!.GetValue<string>());
        });
        yield return Case("legacy.wire.upstream-codec-goldens", async () =>
        {
            var fixture = LegacyFixture("codec-v150.json");
            Equal(LegacyProtocol.SourceCommit, fixture["sourceCommit"]!.GetValue<string>(), "source pin");
            var native = new JsonArray();
            foreach (var sample in fixture["fixtures"]!.AsArray())
            {
                var id = sample!["id"]!.GetValue<string>(); var frame = new LegacyFrame(sample["frame"]!.AsObject());
                Check(LegacyProtocol.IsFrame(frame), id + " validates");
                var actual = LegacyCodec.Encode(frame, true); var expected = sample["websocket"]!;
                var actualIpc = LegacyCodec.PackObject(frame);
                if (expected["text"] is { } text)
                {
                    LegacyTextEqual(text.GetValue<string>(), actual.Text, id + " exact UTF16 text");
                    Equal(sample["size"]!.GetValue<long>(), actual.Size, id + " exact text size");
                    LegacyTextEqual(ChordJson.Stringify(sample["ipc"]!["chord2"]), ChordJson.Stringify(actualIpc), id + " exact IPC frame");
                }
                else
                {
                    Check(actual.IsBinary, id + " exact packing threshold");
                    var actualBinary = actual.Binary ?? throw new InvalidOperationException(id + " expected a packed binary message");
                    var captured = Convert.FromBase64String(expected["base64"]!.GetValue<string>());
                    var headerSize = checked(4 + (int)BinaryPrimitives.ReadUInt32BigEndian(captured));
                    Sequence(captured.AsSpan(0, headerSize).ToArray(), actualBinary.AsSpan(0, headerSize).ToArray(), id + " exact binary header and envelope");
                    Equal((long)actualBinary.Length, actual.Size, id + " reports actual encoded bytes");
                    Equal(sample["size"]!.GetValue<long>(), (long)captured.Length, id + " preserved captured byte size");
                    var upstream = LegacyCodec.Decode(new(null, captured));
                    LegacyTextEqual(sample["inflated"]!.GetValue<string>(), LegacyCodec.InflateMessage(captured, 2 * 1024 * 1024), id + " native decodes upstream WebSocket bytes");
                    LegacyTextEqual(ChordJson.Stringify(frame[upstream.Packed!.Field]), ChordJson.Stringify(upstream.UnpackPayload(2 * 1024 * 1024)), id + " upstream payload");
                    Sequence(captured, LegacyCodec.Encode(upstream).Binary!, id + " upstream WebSocket relay unchanged");
                    var capturedIpc = sample["ipc"]!["chord2"]!.AsObject();
                    var upstreamIpc = LegacyCodec.UnpackObject(capturedIpc);
                    LegacyTextEqual(ChordJson.Stringify(frame[upstreamIpc.Packed!.Field]), ChordJson.Stringify(upstreamIpc.UnpackPayload(2 * 1024 * 1024)), id + " native decodes upstream IPC bytes");
                    LegacyTextEqual(ChordJson.Stringify(capturedIpc), ChordJson.Stringify(LegacyCodec.PackObject(upstreamIpc, false)), id + " upstream IPC relay unchanged");
                    var expectedEnvelope = (JsonObject)capturedIpc.DeepClone(); expectedEnvelope.Remove("z");
                    var actualEnvelope = (JsonObject)actualIpc.DeepClone(); actualEnvelope.Remove("z");
                    LegacyTextEqual(ChordJson.Stringify(expectedEnvelope), ChordJson.Stringify(actualEnvelope), id + " exact IPC envelope");
                    Equal(capturedIpc["z"]![0]!.GetValue<string>(), actualIpc["z"]![0]!.GetValue<string>(), id + " exact IPC packed field");
                }
                var decoded = LegacyCodec.Decode(actual);
                Check(LegacyProtocol.IsFrame(decoded), id + " decoded frame validates");
                if (actual.Text is not null) LegacyTextEqual(actual.Text, LegacyCodec.Encode(decoded).Text, id + " exact decoded text replay");
                if (decoded.Packed is not null)
                {
                    LegacyTextEqual(sample["inflated"]!.GetValue<string>(), LegacyCodec.InflateMessage(actual.Binary!, 2 * 1024 * 1024), id + " inflate message");
                    LegacyTextEqual(ChordJson.Stringify(frame[decoded.Packed.Field]), ChordJson.Stringify(decoded.UnpackPayload(2 * 1024 * 1024)), id + " decode");
                    Sequence(actual.Binary!, LegacyCodec.Encode(decoded).Binary!, id + " relay without inflation");
                }
                native.Add(new JsonObject { ["id"] = id, ["frame"] = sample["frame"]!.DeepClone(), ["inflated"] = sample["inflated"]!.DeepClone(),
                    ["ipc"] = actualIpc, ["websocket"] = actual.Binary is { } binary
                        ? new JsonObject { ["base64"] = Convert.ToBase64String(binary) } : new JsonObject { ["text"] = actual.Text } });
            }
            await LegacyNodeVerify(native);
        });
        yield return Case("legacy.wire.schema-omission-and-validation", () =>
        {
            Check(LegacyProtocol.IsFrame(LegacyProtocol.Frame("result", ("id", JsonValue.Create(1)))), "void value omitted");
            Check(LegacyProtocol.IsFrame(LegacyProtocol.Frame("call", ("id", JsonValue.Create(1)), ("hold", JsonValue.Create(2)), ("member", JsonValue.Create("ping")))), "void args omitted");
            Check(!LegacyProtocol.IsFrame(LegacyProtocol.Frame("ack", ("n", JsonValue.Create(0)))), "ack positive");
            Check(!LegacyProtocol.IsFrame(LegacyProtocol.Frame("hello", ("name", JsonValue.Create("server")), ("kind", JsonValue.Create("invalid")), ("codeVersion", JsonValue.Create(1)))), "kind checked");
            var shape = LegacyProtocol.Frame("shape", ("id", JsonValue.Create(1)), ("members", new JsonArray("n")), ("contract", JsonValue.Create("hash")), ("state", new JsonArray("n")));
            Check(LegacyProtocol.IsFrame(shape.With("future", JsonValue.Create(true))), "forward fields open");
            Check(!LegacyProtocol.IsFrame(new JsonObject { ["t"] = "bound", ["entries"] = new JsonArray() }), "v2 frames rejected");
            var invalid = new LegacyFrame(new JsonObject { ["t"] = "state", ["hold"] = 1, ["m"] = "x", ["as"] = "ops", ["ops"] = new JsonArray(1) });
            Check(!LegacyProtocol.IsFrame(invalid), "ops structurally lists");
            Throws<FormatException>(() => LegacyCodec.Decode(new(null, [0, 0, 0, 10, 123])), "truncated binary header");
            Throws<FormatException>(() => LegacyCodec.Decode(new(null, [0, 0])), "short binary header");
            var packed = LegacyCodec.Encode(LegacyProtocol.Frame("result", ("id", JsonValue.Create(1)), ("value", JsonValue.Create(new string('x', 70000)))), true);
            Throws<FormatException>(() => LegacyCodec.Decode(packed, false), "untrusted binary refused");
            Throws<InvalidDataException>(() => LegacyCodec.Decode(packed).UnpackPayload(10), "explicit inflate bound");
            const string rawPayload = " {\"10\":1.00,\"2\":2, \"same\":1,\"same\":2} ";
            using var compressed = new MemoryStream();
            using (var deflate = new System.IO.Compression.DeflateStream(compressed, System.IO.Compression.CompressionLevel.Fastest, leaveOpen:true))
                deflate.Write(System.Text.Encoding.UTF8.GetBytes(rawPayload));
            var opaque = new LegacyFrame(new JsonObject { ["t"] = "result", ["id"] = 1 }, new LegacyPacked("value", compressed.ToArray()));
            Equal("{\"t\":\"result\",\"id\":1,\"value\":" + rawPayload + "}", LegacyCodec.InflateMessage(LegacyCodec.Encode(opaque).Binary!), "text relay preserves inflated JSON verbatim");
        });
    }
    private static JsonNode LegacyFixture(string name)
        => JsonNode.Parse(File.ReadAllText(LegacyFixturePath(name)))!;
    private static string LegacyFixturePath(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        var relative = Path.Combine("tests/PiSharp.Chord.Tests/Legacy/fixtures", name);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, relative))) directory = directory.Parent;
        Check(directory is not null, "find legacy fixture " + name);
        return Path.Combine(directory!.FullName, relative);
    }
    private static async Task LegacyNodeVerify(JsonArray native)
    {
        var script = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(LegacyFixturePath("codec-v150.json"))!, "..", "verify-codec-interop.mjs"));
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier:false, throwOnInvalidBytes:true);
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("PISHARP_NODE_EXECUTABLE") ?? "node")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = utf8, StandardOutputEncoding = utf8, StandardErrorEncoding = utf8,
        };
        start.ArgumentList.Add(script);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Node for independent legacy DEFLATE interoperability");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var output = process.StandardOutput.ReadToEndAsync(deadline.Token); var error = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            await process.StandardInput.WriteAsync(ChordJson.Stringify(new JsonObject { ["fixtures"] = native }).AsMemory(), deadline.Token);
            process.StandardInput.Close(); await process.WaitForExitAsync(deadline.Token);
            var diagnostic = await error;
            if (diagnostic.Length > 4096) diagnostic = diagnostic[..2048] + " [truncated; " + LegacyTextSummary(diagnostic) + "]";
            Equal(0, process.ExitCode, "Node decodes native frames: " + diagnostic);
            var result = JsonNode.Parse(await output)!; Equal(8, result["fixtures"]!.GetValue<int>(), "independent Node fixture count");
            Equal(3, result["packed"]!.GetValue<int>(), "independent Node packed count");
        }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
    }
    private static void LegacyTextEqual(string? expected, string? actual, string what)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new InvalidOperationException(what + ": expected(" + LegacyTextSummary(expected) + "), actual(" + LegacyTextSummary(actual) + ").");
    }
    private static string LegacyTextSummary(string? text) => text is null ? "null" :
        $"utf16={text.Length},utf8={Encoding.UTF8.GetByteCount(text)},sha256={Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))}";
}
