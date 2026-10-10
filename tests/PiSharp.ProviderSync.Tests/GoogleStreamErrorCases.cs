// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/api/google-generative-ai.ts and google-vertex.ts (stream
// consumption of @google/genai 2.21.0 generateContentStream / ApiClient.processStreamResponse).
using System.Net;
using System.Text;
using PiSharp.AI;
using PiSharp.AI.Protocols.GoogleGenerativeAI;
using PiSharp.AI.Protocols.GoogleVertex;
using PiSharp.Contracts;

// Expected texts were captured by running the installed @earendil-works/pi-ai@1.1.0 google-generative-ai and google-vertex
// stream() (with @google/genai 2.21.0) against a local HTTP server writing each segment as its own response chunk.
internal static partial class Program
{
    private static readonly ModelDescriptor VertexGemini = new("gemini-3-flash-preview", "google-vertex", "google-vertex");

    /// <summary>A response body whose reads return the given segments one at a time (one network chunk each).</summary>
    private sealed class SegmentStream(string[] segments) : Stream
    {
        private readonly Queue<byte[]> _pending = new(segments.Select(segment => Encoding.UTF8.GetBytes(segment)));
        private byte[]? _current; private int _offset;
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            while (_current is null || _offset == _current.Length)
            {
                if (!_pending.TryDequeue(out _current)) return 0;
                _offset = 0;
            }
            var n = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsSpan(_offset, n).CopyTo(buffer); _offset += n;
            if (_offset == _current.Length) _current = null;
            return n;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => ValueTask.FromResult(Read(buffer.Span));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => Task.FromResult(Read(buffer, offset, count));
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static async Task<AssistantMessage> GoogleSegments(string[] segments, bool vertex)
    {
        using var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("") }));
        using var client = new HttpClient(handler);
        ValueTask<Stream?> Body(HttpResponseMessage response, CancellationToken token) => ValueTask.FromResult<Stream?>(new SegmentStream(segments));
        if (!vertex)
        {
            var transport = new GoogleGenerativeAIHttpTransport(client, Gemini, new GoogleGenerativeAIOptions(GoogleMetadata(), Key) { BodyReaderFactory = Body });
            return (await new ChatClient(transport).CompleteAsync(new(Gemini, [Ask], 1)).WaitAsync(Deadline)).Message;
        }
        var metadata = JsonData.Parse(GoogleMetadata().ToString().Replace("\"api\":\"google-generative-ai\",\"provider\":\"google\"",
            "\"api\":\"google-vertex\",\"provider\":\"google-vertex\"", StringComparison.Ordinal));
        var vertexTransport = new GoogleVertexHttpTransport(client, VertexGemini, new GoogleVertexOptions(
            new("https://us-central1-aiplatform.googleapis.com/v1/projects/p/locations/us-central1/publishers/google/models/gemini-3-flash-preview:streamGenerateContent?alt=sse"),
            "inert-vertex-token", new GoogleGenerativeAIOptions(metadata) { BodyReaderFactory = Body }));
        return (await new ChatClient(vertexTransport).CompleteAsync(new(VertexGemini, [Ask], 1)).WaitAsync(Deadline)).Message;
    }

    private static async Task GoogleStreamErrorTexts()
    {
        static string Text(string text, string? finish = null) => "data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"" + text + "\"}]}" +
            (finish is null ? "" : ",\"finishReason\":\"" + finish + "\"") + "}]}\n\n";
        static string Error(int code, string status, string message) => "{\"error\":{\"code\":" + code + ",\"message\":\"" + message + "\",\"status\":\"" + status + "\"}}";
        const string Eof = "\u0000stream ended without a finish reason";
        var cases = new (string Name, string[] Segments, string? Expected, string Content)[]
        {
            // A data frame's error member is dropped by generateContentResponseFromMldev; the stream just never finishes.
            ("sse-error-frame", ["data: " + Error(500, "INTERNAL", "boom") + "\n\n"], Eof, ""),
            ("sse-error-frame-after-text", [Text("hi"), "data: " + Error(500, "INTERNAL", "boom") + "\n\n"], Eof, "hi"),
            // A whole read chunk that parses as an object with error.code in [400, 600) throws ApiError.
            ("raw-json-error-chunk", [Error(503, "UNAVAILABLE", "overloaded")],
                "got status: UNAVAILABLE. {\"error\":{\"code\":503,\"message\":\"overloaded\",\"status\":\"UNAVAILABLE\"}}", ""),
            ("raw-json-error-chunk-newline", [Error(429, "RESOURCE_EXHAUSTED", "quota") + "\n"],
                "got status: RESOURCE_EXHAUSTED. {\"error\":{\"code\":429,\"message\":\"quota\",\"status\":\"RESOURCE_EXHAUSTED\"}}", ""),
            ("text-then-raw-json-error-chunk", [Text("hi"), Error(503, "UNAVAILABLE", "overloaded")],
                "got status: UNAVAILABLE. {\"error\":{\"code\":503,\"message\":\"overloaded\",\"status\":\"UNAVAILABLE\"}}", "hi"),
            ("raw-json-error-code-string", ["{\"error\":{\"code\":\"500\",\"message\":\"x\",\"status\":\"INTERNAL\"}}"],
                "got status: INTERNAL. {\"error\":{\"code\":\"500\",\"message\":\"x\",\"status\":\"INTERNAL\"}}", ""),
            ("raw-json-error-code-float", ["{\"error\":{\"code\":500.5,\"message\":\"x\"}}"], "got status: undefined. {\"error\":{\"code\":500.5,\"message\":\"x\"}}", ""),
            ("raw-json-error-extra-fields", ["{\"a\":1,\"error\":{\"status\":\"S\",\"code\":404,\"message\":\"m\",\"details\":[{\"x\":1}]}}"],
                "got status: S. {\"a\":1,\"error\":{\"status\":\"S\",\"code\":404,\"message\":\"m\",\"details\":[{\"x\":1}]}}", ""),
            // Out-of-range or absent codes are buffered; an undelimited remainder fails at the end of the body.
            ("raw-json-error-code-200", [Error(200, "OK", "fine")], "Incomplete JSON segment at the end", ""),
            ("raw-json-error-no-code", ["{\"error\":{\"message\":\"x\"}}"], "Incomplete JSON segment at the end", ""),
            ("raw-json-error-code-399", [Error(399, "X", "x")], "Incomplete JSON segment at the end", ""),
            ("raw-json-error-code-600", [Error(600, "X", "x")], "Incomplete JSON segment at the end", ""),
            ("raw-json-error-null", ["{\"error\":null}"], "Incomplete JSON segment at the end", ""),
            ("raw-json-number", ["42"], "Incomplete JSON segment at the end", ""),
            ("trailing-incomplete", [Text("hi"), "data: {\"candidates\":[]}"], "Incomplete JSON segment at the end", "hi"),
            // Data frames are JSON.parse'd: the SyntaxError text is shown.
            ("malformed-data-frame", ["data: {bad\n\n"], "Expected property name or '}' in JSON at position 1 (line 1 column 2)", ""),
            ("malformed-after-text", [Text("hi"), "data: {bad\n\n"], "Expected property name or '}' in JSON at position 1 (line 1 column 2)", "hi"),
            ("data-plain-text", ["data: hello\n\n"], "Unexpected token 'h', \"hello\" is not valid JSON", ""),
            ("data-empty", ["data: \n\n"], "Unexpected end of JSON input", ""),
            ("data-done-marker", [Text("hi", "STOP"), "data: [DONE]\n\n"], "Unexpected token 'D', \"[DONE]\" is not valid JSON", "hi"),
            // Values that are not objects carry no response fields.
            ("data-null", ["data: null\n\n"], Eof, ""),
            ("data-array", ["data: [1]\n\n"], Eof, ""),
            ("data-string", ["data: \"s\"\n\n"], Eof, ""),
            ("no-finish", [Text("hi")], Eof, "hi"),
            ("empty-body", [""], Eof, ""),
            // Completed streams: blank remainder, comment events and CRLF delimiters.
            ("trailing-whitespace-only", [Text("hi", "STOP"), "  \n"], null, "hi"),
            ("comment-frame", [": keepalive\n\n", Text("hi", "STOP")], null, "hi"),
            ("crlf-delimiters", [Text("hi", "STOP").Replace("\n\n", "\r\n\r\n", StringComparison.Ordinal)], null, "hi"),
        };
        foreach (var vertex in new[] { false, true })
            foreach (var (name, segments, expected, content) in cases)
            {
                var message = await GoogleSegments(segments, vertex);
                var label = (vertex ? "vertex " : "google ") + name;
                var shown = expected?.Replace("\u0000", vertex ? "Google Vertex " : "Google ", StringComparison.Ordinal);
                Equal(label + ":" + (shown is null ? "stop" : "error"), label + ":" + message.StopReason.ToString().ToLowerInvariant());
                Equal(label + ":" + shown, label + ":" + (message.StopReason == StopReason.Error ? message.ExtraProperties!.Values["errorMessage"].Value.GetString() : null));
                Equal(label + ":" + content, label + ":" + string.Concat(message.Content.OfType<TextContent>().Select(text => text.Text)));
            }
    }
}
