using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using PiSharp.AI.Compression;

/// <summary>openai-codex-responses.ts compressRequestBodyZstd: PiSharp's RFC 8878 encoder, checked by its own decoder and by Node's
/// zlib.zstdDecompressSync (Node 22.15 or later; the reference case is skipped, and says so, without one).</summary>
internal static partial class Program
{
    private static readonly (string Id, Func<Task> Run)[] ZstdCases =
    [
        ("zstd.frames-round-trip-through-the-decoder", ZstdRoundTrip),
        ("zstd.node-reference-decompresses-every-frame", ZstdNodeReference),
    ];

    internal static string ZstdReferenceStatus = "not run";

    /// <summary>Inputs covering every block, literal and sequence form: empty, tiny, RLE, incompressible, ASCII and UTF-8 JSON, block
    /// and 2 MiB window boundaries, matches across blocks.</summary>
    private static List<(string Name, byte[] Data)> ZstdInputs()
    {
        var random = new Random(1234);
        byte[] Random(int size) { var bytes = new byte[size]; random.NextBytes(bytes); return bytes; }
        string Conversation(int turns) => "{\"model\":\"gpt-5.5\",\"input\":[" + string.Join(",", Enumerable.Range(0, turns).Select(turn =>
            $$"""{"role":"{{(turn % 2 == 0 ? "user" : "assistant")}}","content":[{"type":"input_text","text":"Turn {{turn}}: please read src/file{{turn % 17}}.cs and explain the {{(turn % 3 == 0 ? "café ✓ naïve" : "parser")}} logic around line {{turn * 7}}."}]}""")) +
            "],\"tools\":[{\"type\":\"function\",\"name\":\"read\",\"parameters\":{\"type\":\"object\"}}]}";
        var image = Convert.ToBase64String(Random(300_000));
        // Mixed data: copies from earlier offsets (short and long, near and far, overlapping) with mutations and fresh runs, so every
        // repeat-offset case, literal length and match length code occurs.
        byte[] Mixed(int size, int alphabet)
        {
            var bytes = new byte[size]; var position = 0;
            while (position < size)
            {
                var length = Math.Min(size - position, random.Next(10) < 3 ? random.Next(1, 40) : random.Next(3, random.Next(10) == 0 ? 70_000 : 300));
                if (position > 8 && random.Next(3) != 0)
                {
                    var offset = random.Next(5) == 0 ? random.Next(1, 9) : random.Next(1, Math.Min(position, random.Next(2) == 0 ? 64 : 1 << 21) + 1);
                    for (var index = 0; index < length; index++) bytes[position + index] = bytes[position + index - offset];
                    if (random.Next(4) == 0) bytes[position + random.Next(length)] ^= (byte)random.Next(1, 256);
                }
                else for (var index = 0; index < length; index++) bytes[position + index] = (byte)random.Next(alphabet);
                position += length;
            }
            return bytes;
        }
        return
        [
            .. Enumerable.Range(0, 24).Select(index => ($"mixed {index}", Mixed(random.Next(1, index < 20 ? 300_000 : 2_600_000), index % 3 == 0 ? 256 : 4 + index * 5))),
            ("empty", []), ("one byte", [42]), ("two bytes", [1, 2]), ("short text", Encoding.UTF8.GetBytes("Hello, Codex!")),
            ("rle 1000", Enumerable.Repeat((byte)'a', 1000).ToArray()), ("rle 300k", Enumerable.Repeat((byte)7, 300_000).ToArray()),
            ("random 5000", Random(5000)), ("random 200k", Random(200_000)),
            ("ascii json 2k", Encoding.UTF8.GetBytes(Conversation(10))), ("utf-8 json 600k", Encoding.UTF8.GetBytes(Conversation(3000))),
            ("binary symbols", Enumerable.Range(0, 70_000).Select(index => (byte)(index * index % 251 + (index % 5 == 0 ? 3 : 0))).ToArray()),
            ("base64 image body", Encoding.UTF8.GetBytes("{\"input\":[{\"type\":\"input_image\",\"image_url\":\"data:image/png;base64," + image + "\"}]}")),
            ("block boundary", Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("abcdefghij", 13_108)) + "tail")),
            ("over the window", Encoding.UTF8.GetBytes(Conversation(11_000))),
            ("periodic short", Enumerable.Range(0, 50_000).Select(index => (byte)"xyz"[index % 3]).ToArray()),
        ];
    }

    private static Task ZstdRoundTrip()
    {
        foreach (var (name, data) in ZstdInputs())
        {
            var frame = Zstd.Compress(data);
            var back = ZstdDecoder.Decompress(frame);
            Check(back.AsSpan().SequenceEqual(data), $"{name}: round trip ({data.Length} -> {frame.Length} bytes)");
            Equal(0xFD2FB528u, BitConverter.ToUInt32(frame, 0), name + " magic");
            if (data.Length > 4096 && name.Contains("json", StringComparison.Ordinal)) Check(frame.Length * 4 < data.Length, $"{name} compresses: {data.Length} -> {frame.Length}");
            if (name.StartsWith("random", StringComparison.Ordinal)) Check(frame.Length <= data.Length + data.Length / 128 + 32, name + " raw blocks bound the size");
        }
        // The window: above 2 MiB the frame has a 2 MiB window descriptor instead of a single segment.
        var large = Zstd.Compress(Encoding.UTF8.GetBytes(new string('q', 3 * 1024 * 1024)));
        Equal(0x80, large[4] & 0xE0, "4-byte content size, no single segment");
        Equal((byte)((21 - 10) << 3), large[5], "window descriptor 2^21");
        Check(Assert<InvalidDataException>(() => ZstdDecoder.Decompress([0x28, 0xB5, 0x2F, 0xFD, 0x20, 0x05, 0x01, 0x00])), "truncated frame rejected");
        return Task.CompletedTask;

        static bool Assert<T>(Action run) where T : Exception { try { run(); return false; } catch (T) { return true; } }
    }

    private static async Task ZstdNodeReference()
    {
        var node = FindOnPath(OperatingSystem.IsWindows() ? "node.exe" : "node");
        if (node is null) { ZstdReferenceStatus = "skipped: node is not on PATH"; return; }
        var directory = Temp("zstd");
        var inputs = ZstdInputs();
        for (var index = 0; index < inputs.Count; index++)
            await File.WriteAllBytesAsync(Path.Combine(directory, $"{index}.zst"), Zstd.Compress(inputs[index].Data));
        await File.WriteAllTextAsync(Path.Combine(directory, "check.cjs"), """
            const zlib = require('zlib'); const fs = require('fs'); const crypto = require('crypto'); const path = require('path');
            if (typeof zlib.zstdDecompressSync !== 'function') { console.log('NOZSTD'); process.exit(0); }
            const dir = process.argv[2]; const out = [];
            for (const name of fs.readdirSync(dir).filter(f => f.endsWith('.zst')).sort((a, b) => parseInt(a) - parseInt(b))) {
              const content = zlib.zstdDecompressSync(fs.readFileSync(path.join(dir, name)));
              const level3 = zlib.zstdCompressSync(content, { params: { [zlib.constants.ZSTD_c_compressionLevel]: 3 } }).length;
              out.push(name + ' ' + crypto.createHash('sha256').update(content).digest('hex') + ' ' + level3);
            }
            console.log(out.join('\n'));
            """);
        var start = new ProcessStartInfo(node) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(Path.Combine(directory, "check.cjs")); start.ArgumentList.Add(directory);
        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync(); var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Check(process.ExitCode == 0, "node failed: " + error);
        if (output.Trim() == "NOZSTD") { ZstdReferenceStatus = "skipped: this node has no zlib.zstdDecompressSync"; return; }
        var lines = output.Trim().Split('\n').Select(line => line.Trim().Split(' ')).ToArray();
        long ours = 0, reference = 0;
        for (var index = 0; index < inputs.Count; index++)
        {
            Equal($"{index}.zst {Convert.ToHexStringLower(SHA256.HashData(inputs[index].Data))}", lines[index][0] + " " + lines[index][1], inputs[index].Name + " decompressed by node");
            if (!inputs[index].Name.Contains("json", StringComparison.Ordinal) && !inputs[index].Name.Contains("window", StringComparison.Ordinal)) continue;
            var size = Zstd.Compress(inputs[index].Data).Length; var level3 = long.Parse(lines[index][2]);
            Check(size <= level3 * 13 / 10, $"{inputs[index].Name}: PiSharp {size} bytes, libzstd level 3 {level3}");
            ours += size; reference += level3;
        }
        ZstdReferenceStatus = $"node {FileVersionInfo.GetVersionInfo(node).ProductVersion ?? "?"}: {inputs.Count} frames decoded; JSON inputs: PiSharp {ours} bytes, libzstd level 3 {reference}";
    }

    private static string? FindOnPath(string executable) => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
        .Where(directory => directory.Length > 0).Select(directory => Path.Combine(directory.Trim('"'), executable)).FirstOrDefault(File.Exists);
}
