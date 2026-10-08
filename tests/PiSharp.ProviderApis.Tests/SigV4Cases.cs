using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using PiSharp.AI.Protocols.Bedrock;

internal static partial class Program
{
    private static readonly AwsCredentials SuiteCredentials = new("AKIDEXAMPLE", "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY");
    private static readonly DateTimeOffset SuiteTime = new(2015, 8, 30, 12, 36, 0, TimeSpan.Zero);

    /// <summary>One AWS signing test suite case (region us-east-1, service "service"): the request, the expected canonical request,
    /// the string-to-sign hash and the signature, embedded from awslabs/aws-c-auth tests/aws-signing-test-suite/v4.</summary>
    private sealed record Vector(string Name, string Method, string Path, (string, string)[] Query, (string, string)[] Headers, string Body,
        bool SignBody, string? Token, string Canonical, string StringToSignHash, string Signature);

    private static readonly Vector[] Vectors =
    [
        new("get-vanilla", "GET", "/", [], [("Host", "example.amazonaws.com")], "", false, null,
            "GET\n/\n\nhost:example.amazonaws.com\nx-amz-date:20150830T123600Z\n\nhost;x-amz-date\ne3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            "bb579772317eb040ac9ed261061d46c1f17a8133879d6129b6e1c25292927e63", "5fa00fa31553b73ebf1942676e86291e8372ff2a2260956d9b8aae1d763fbf31"),
        new("get-vanilla-with-session-token", "GET", "/", [], [("Host", "example.amazonaws.com")], "", false,
            "6e86291e8372ff2a2260956d9b8aae1d763fbf315fa00fa31553b73ebf194267",
            "GET\n/\n\nhost:example.amazonaws.com\nx-amz-date:20150830T123600Z\nx-amz-security-token:6e86291e8372ff2a2260956d9b8aae1d763fbf315fa00fa31553b73ebf194267\n\nhost;x-amz-date;x-amz-security-token\ne3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            "067b36aa60031588cea4a4cde1f21215227a047690c72247f1d70b32fbbfad2b", "07ec1639c89043aa0e3e2de82b96708f198cceab042d4a97044c66dd9f74e7f8"),
        new("post-x-www-form-urlencoded", "POST", "/", [], [("Content-Type", "application/x-www-form-urlencoded"), ("Host", "example.amazonaws.com"), ("Content-Length", "13")],
            "Param1=value1", true, null,
            "POST\n/\n\ncontent-length:13\ncontent-type:application/x-www-form-urlencoded\nhost:example.amazonaws.com\nx-amz-content-sha256:9095672bbd1f56dfc5b65f3e153adc8731a4a654192329106275f4c7b24d0b6e\nx-amz-date:20150830T123600Z\n\ncontent-length;content-type;host;x-amz-content-sha256;x-amz-date\n9095672bbd1f56dfc5b65f3e153adc8731a4a654192329106275f4c7b24d0b6e",
            "b1edd1d03544c25390e32085d55b57acc9a3961bb59415ff86c45c3d89d16cfb", "d3875051da38690788ef43de4db0d8f280229d82040bfac253562e56c3f20e0b"),
        new("get-utf8", "GET", "/ሴ", [], [("Host", "example.amazonaws.com")], "", false, null,
            "GET\n/%E1%88%B4\n\nhost:example.amazonaws.com\nx-amz-date:20150830T123600Z\n\nhost;x-amz-date\ne3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            "2a0a97d02205e45ce2e994789806b19270cfbbb0921b278ccf58f5249ac42102", "8318018e0b0f223aa2bbf98705b62bb787dc9c0e678f255a891fd03141be5d85"),
        new("get-header-value-trim", "GET", "/", [], [("Host", "example.amazonaws.com"), ("My-Header1", " value1"), ("My-Header2", " \"a   b   c\"")], "", false, null,
            "GET\n/\n\nhost:example.amazonaws.com\nmy-header1:value1\nmy-header2:\"a b c\"\nx-amz-date:20150830T123600Z\n\nhost;my-header1;my-header2;x-amz-date\ne3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            "a726db9b0df21c14f559d0a978e563112acb1b9e05476f0a6a1c7d68f28605c7", "acc3ed3afb60bb290fc8d2dd0098b9911fcaa05412b367055dee359757a9c736"),
        new("get-vanilla-query-order-key-case", "GET", "/", [("Param2", "value2"), ("Param1", "value1")], [("Host", "example.amazonaws.com")], "", false, null,
            "GET\n/\nParam1=value1&Param2=value2\nhost:example.amazonaws.com\nx-amz-date:20150830T123600Z\n\nhost;x-amz-date\ne3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            "816cd5b414d056048ba4f7c5386d6e0533120fb1fcfa93762cf0fc39e2cf19e0", "b97d918cfa904a5beff61c982a1b6f458b799221646efd99d3219ec94cdf2500"),
        new("post-vanilla", "POST", "/", [], [("Host", "example.amazonaws.com")], "", false, null,
            "POST\n/\n\nhost:example.amazonaws.com\nx-amz-date:20150830T123600Z\n\nhost;x-amz-date\ne3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            "553f88c9e4d10fc9e109e2aeb65f030801b70c2f6468faca261d401ae622fc87", "5da7c1a2acd57cee7505fc6676e4e544621c30862966e37dddb68e92efbe5d6b"),
        new("get-unreserved", "GET", "/-._~0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz", [], [("Host", "example.amazonaws.com")], "", false, null,
            "GET\n/-._~0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz\n\nhost:example.amazonaws.com\nx-amz-date:20150830T123600Z\n\nhost;x-amz-date\ne3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            "6a968768eefaa713e2a6b16b589a8ea192661f098f37349f4e2c0082757446f9", "07ef7494c76fa4850883e2b006601f940f8a34d404d0cfa977f52a65bbf5f24f"),
        new("get-vanilla-utf8-query", "GET", "/", [("ሴ", "bar")], [("Host", "example.amazonaws.com")], "", false, null,
            "GET\n/\n%E1%88%B4=bar\nhost:example.amazonaws.com\nx-amz-date:20150830T123600Z\n\nhost;x-amz-date\ne3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            "eb30c5bed55734080471a834cc727ae56beb50e5f39d1bff6d0d38cb192a7073", "2cdec8eed098649ff3a119c94853b13c643bcf08f8b0a1d91e12c9027818dd04"),
        new("get-vanilla-query-unreserved", "GET", "/", [("-._~0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz", "-._~0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz")],
            [("Host", "example.amazonaws.com")], "", false, null,
            "GET\n/\n-._~0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz=-._~0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz\nhost:example.amazonaws.com\nx-amz-date:20150830T123600Z\n\nhost;x-amz-date\ne3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            "c30d4703d9f799439be92736156d47ccfb2d879ddf56f5befa6d1d6aab979177", "9c3e54bfcdf0b19771a7f523ee5669cdf59bc7cc0884027167c21bb143a40197"),
        new("get-header-key-duplicate", "GET", "/", [], [("Host", "example.amazonaws.com"), ("My-Header1", "value2"), ("My-Header1", "value2"), ("My-Header1", "value1")], "", false, null,
            "GET\n/\n\nhost:example.amazonaws.com\nmy-header1:value2,value2,value1\nx-amz-date:20150830T123600Z\n\nhost;my-header1;x-amz-date\ne3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            "dc7f04a3abfde8d472b0ab1a418b741b7c67174dad1551b4117b15527fbe966c", "c9d5ea9f3f72853aea855b47ea873832890dbdd183b4468f858259531a5138ea"),
        new("get-header-value-order", "GET", "/", [], [("Host", "example.amazonaws.com"), ("My-Header1", "value4"), ("My-Header1", "value1"), ("My-Header1", "value3"), ("My-Header1", "value2")], "", false, null,
            "GET\n/\n\nhost:example.amazonaws.com\nmy-header1:value4,value1,value3,value2\nx-amz-date:20150830T123600Z\n\nhost;my-header1;x-amz-date\ne3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            "31ce73cd3f3d9f66977ad3dd957dc47af14df92fcd8509f59b349e9137c58b86", "08c7e5a9acfcfeb3ab6b2185e75ce8b1deb5e634ec47601a50643f830c755c01"),
        new("post-header-key-sort", "POST", "/", [], [("Host", "example.amazonaws.com"), ("My-Header1", "value1")], "", false, null,
            "POST\n/\n\nhost:example.amazonaws.com\nmy-header1:value1\nx-amz-date:20150830T123600Z\n\nhost;my-header1;x-amz-date\ne3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            "9368318c2967cf6de74404b30c65a91e8f6253e0a8659d6d5319f1a812f87d65", "c5410059b04c1ee005303aed430f6e6645f61f4dc9e1461ec8f8916fdf18852c"),
        new("post-vanilla-query", "POST", "/", [("Param1", "value1")], [("Host", "example.amazonaws.com")], "", false, null,
            "POST\n/\nParam1=value1\nhost:example.amazonaws.com\nx-amz-date:20150830T123600Z\n\nhost;x-amz-date\ne3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            "9d659678c1756bb3113e2ce898845a0a79dbbc57b740555917687f1b3340fbbd", "28038455d6de14eafc1f9222cf5aa6f1a96197d7deb8263271d420d138af7f11"),
        new("get-space-normalized", "GET", "/example space/", [], [("Host", "example.amazonaws.com")], "", false, null,
            "GET\n/example%20space/\n\nhost:example.amazonaws.com\nx-amz-date:20150830T123600Z\n\nhost;x-amz-date\ne3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            "63ee75631ed7234ae61b5f736dfc7754cdccfedbff4b5128a915706ee9390d86", "652487583200325589f1fba4c7e578f72c47cb61beeca81406b39ddec1366741"),
    ];

    private static void SigV4Vectors()
    {
        foreach (var vector in Vectors)
        {
            var credentials = vector.Token is null ? SuiteCredentials : SuiteCredentials with { SessionToken = vector.Token };
            var signature = AwsSigV4.Sign(new(vector.Method, "example.amazonaws.com", vector.Path,
                    [.. vector.Query.Select(pair => KeyValuePair.Create(pair.Item1, pair.Item2))],
                    [.. vector.Headers.Select(pair => KeyValuePair.Create(pair.Item1, pair.Item2))], Encoding.UTF8.GetBytes(vector.Body)),
                credentials, "us-east-1", "service", SuiteTime, applyChecksum: vector.SignBody);
            Equal(vector.Canonical, signature.CanonicalRequest, vector.Name + " canonical request");
            Equal("AWS4-HMAC-SHA256\n20150830T123600Z\n20150830/us-east-1/service/aws4_request\n" + vector.StringToSignHash, signature.StringToSign, vector.Name + " string to sign");
            Equal(vector.Signature, signature.Signature, vector.Name + " signature");
            Equal($"AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20150830/us-east-1/service/aws4_request, SignedHeaders={vector.Canonical.Split('\n')[^2]}, Signature={vector.Signature}",
                signature.Authorization, vector.Name + " authorization");
        }
    }

    /// <summary>The AWS documentation's signing-key derivation example (secret wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY, 20120215,
    /// us-east-1, iam).</summary>
    private static void SigningKeyExample()
    {
        var key = AwsSigV4.SigningKey("wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY", "20120215", "us-east-1", "iam");
        Equal("f4780e2d9f65fa895f9c67b32ce1baf0b0d8a43505a000a1a9e090d414db404d", Convert.ToHexStringLower(key), "kSigning");
        var date = HMACSHA256.HashData(Encoding.UTF8.GetBytes("AWS4wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY"), Encoding.UTF8.GetBytes("20120215"));
        Equal("969fbb94feb542b71ede6f87fe4d5fa29c789342b0f407474670f0c2489e0a0d", Convert.ToHexStringLower(date), "kDate");
    }

    /// <summary>The SDK escapes the model id into the path ("%3A") and the canonical path escapes it again ("%253A").</summary>
    private static void SigV4BedrockPath()
    {
        Equal("/model/us.anthropic.claude-sonnet-4-5-20250929-v1%253A0/converse-stream",
            AwsSigV4.CanonicalPath("/model/" + AwsSigV4.EscapeUri("us.anthropic.claude-sonnet-4-5-20250929-v1:0") + "/converse-stream"), "double escape");
        Equal("/model/arn%253Aaws%253Abedrock%253Aus-west-2%253A123456789012%253Aapplication-inference-profile%252Fabc/converse-stream",
            AwsSigV4.CanonicalPath("/model/" + AwsSigV4.EscapeUri("arn:aws:bedrock:us-west-2:123456789012:application-inference-profile/abc") + "/converse-stream"), "arn");
        Equal("%21%27%28%29%2A", AwsSigV4.EscapeUri("!'()*"), "extended escape");
        Equal("/a/c/", AwsSigV4.CanonicalPath("/a/./b/../c//"), "normalized");
    }

    private static byte[] EventMessage(string eventType, string payload, string messageType = "event") =>
        AwsEventStream.Encode([new(":event-type", eventType), new(":content-type", "application/json"), new(":message-type", messageType)], Encoding.UTF8.GetBytes(payload));

    /// <summary>A stream that returns the given chunks one read at a time.</summary>
    private sealed class ChunkedStream(IEnumerable<byte[]> chunks) : Stream
    {
        private readonly Queue<byte[]> queue = new(chunks);
        private byte[]? current; private int offset;
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int index, int count)
        {
            while (current is null || offset >= current.Length) { if (queue.Count == 0) return 0; current = queue.Dequeue(); offset = 0; }
            var taken = Math.Min(count, current.Length - offset);
            Array.Copy(current, offset, buffer, index, taken); offset += taken; return taken;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static async Task<List<AwsEventStreamMessage>> DecodeAll(Stream stream)
    {
        var result = new List<AwsEventStreamMessage>();
        await foreach (var message in AwsEventStream.DecodeAsync(stream)) result.Add(message);
        return result;
    }

    private static async Task EventStreamSplits()
    {
        // The empty message: total 16, no headers, prelude CRC 0x05c248eb, message CRC 0x7d98c8ff.
        var empty = AwsEventStream.Encode([], []);
        Equal("0000001000000000" + "05c248eb" + "7d98c8ff", Convert.ToHexStringLower(empty), "empty message bytes");
        var first = EventMessage("contentBlockDelta", """{"contentBlockIndex":0,"delta":{"text":"Hé"},"p":"abcd"}""");
        var second = EventMessage("messageStop", """{"stopReason":"end_turn","p":"x"}""");
        var all = first.Concat(second).ToArray();
        for (var split = 1; split < all.Length; split++)
        {
            var decoded = await DecodeAll(new ChunkedStream([all[..split], all[split..]]));
            Equal(2, decoded.Count, "messages at split " + split);
            Equal("contentBlockDelta", decoded[0].HeaderString(":event-type"), "first event type");
            Equal("""{"contentBlockIndex":0,"delta":{"text":"Hé"},"p":"abcd"}""", Encoding.UTF8.GetString(decoded[0].Payload), "first payload");
            Equal("messageStop", decoded[1].HeaderString(":event-type"), "second event type");
        }
        var byteByByte = await DecodeAll(new ChunkedStream(all.Select(value => new[] { value })));
        Equal(2, byteByByte.Count, "byte-by-byte delivery");
        // Typed headers: bool, byte, short, int, long, bytes, string, timestamp and uuid.
        using var headers = new MemoryStream();
        void Header(string name, byte type, byte[] value) { headers.WriteByte((byte)name.Length); headers.Write(Encoding.ASCII.GetBytes(name)); headers.WriteByte(type); headers.Write(value); }
        byte[] Big(long value, int size) { var bytes = new byte[8]; BinaryPrimitives.WriteInt64BigEndian(bytes, value); return bytes[(8 - size)..]; }
        Header("t", 0, []); Header("f", 1, []); Header("b", 2, [0xFE]); Header("s", 3, Big(-2, 2)); Header("i", 4, Big(70000, 4)); Header("l", 5, Big(5_000_000_000, 8));
        Header("y", 6, [0, 2, 1, 2]); Header("z", 7, [0, 2, (byte)'o', (byte)'k']); Header("d", 8, Big(1_440_938_160_000, 8));
        Header("u", 9, [0x01, 0x23, 0x45, 0x67, 0x89, 0xab, 0xcd, 0xef, 0x01, 0x23, 0x45, 0x67, 0x89, 0xab, 0xcd, 0xef]);
        var headerBytes = headers.ToArray();
        var message = new byte[12 + headerBytes.Length + 4];
        BinaryPrimitives.WriteUInt32BigEndian(message, (uint)message.Length);
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(4), (uint)headerBytes.Length);
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(8), Crc32.Compute(message.AsSpan(0, 8)));
        headerBytes.CopyTo(message, 12);
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(message.Length - 4), Crc32.Compute(message.AsSpan(0, message.Length - 4)));
        var typed = AwsEventStream.Decode(message);
        Check(typed.Headers["t"] is true && typed.Headers["f"] is false && (sbyte)typed.Headers["b"] == -2 && (short)typed.Headers["s"] == -2 &&
            (int)typed.Headers["i"] == 70000 && (long)typed.Headers["l"] == 5_000_000_000 && ((byte[])typed.Headers["y"]).SequenceEqual(new byte[] { 1, 2 }) &&
            (string)typed.Headers["z"] == "ok" && (DateTimeOffset)typed.Headers["d"] == DateTimeOffset.FromUnixTimeMilliseconds(1_440_938_160_000) &&
            (Guid)typed.Headers["u"] == Guid.Parse("01234567-89ab-cdef-0123-456789abcdef") && typed.Payload.Length == 0, "typed headers");
    }

    private static async Task EventStreamFailures()
    {
        var message = EventMessage("messageStart", """{"role":"assistant"}""");
        var badPrelude = (byte[])message.Clone(); badPrelude[8] ^= 0xFF;
        Equal(AwsEventStreamFailure.PreludeChecksum, (await Throws<AwsEventStreamException>(() => DecodeAll(new MemoryStream(badPrelude)))).Failure, "prelude CRC");
        var badMessage = (byte[])message.Clone(); badMessage[^6] ^= 0x01;
        Equal(AwsEventStreamFailure.MessageChecksum, (await Throws<AwsEventStreamException>(() => DecodeAll(new MemoryStream(badMessage)))).Failure, "payload corruption");
        var badCrc = (byte[])message.Clone(); badCrc[^1] ^= 0x01;
        Equal(AwsEventStreamFailure.MessageChecksum, (await Throws<AwsEventStreamException>(() => DecodeAll(new MemoryStream(badCrc)))).Failure, "message CRC");
        Equal(AwsEventStreamFailure.TruncatedMessage, (await Throws<AwsEventStreamException>(() => DecodeAll(new MemoryStream(message[..^3])))).Failure, "truncated");
        var tooShort = new byte[16]; BinaryPrimitives.WriteUInt32BigEndian(tooShort, 12); BinaryPrimitives.WriteUInt32BigEndian(tooShort.AsSpan(8), Crc32.Compute(tooShort.AsSpan(0, 8)));
        Equal(AwsEventStreamFailure.InvalidLength, (await Throws<AwsEventStreamException>(() => DecodeAll(new MemoryStream(tooShort)))).Failure, "length below 16");
        // A failure after a complete first message still yields that message first.
        var decoded = new List<AwsEventStreamMessage>();
        var error = await Throws<AwsEventStreamException>(async () => { await foreach (var item in AwsEventStream.DecodeAsync(new MemoryStream([.. message, .. badCrc]))) decoded.Add(item); });
        Check(decoded.Count == 1 && error.Failure == AwsEventStreamFailure.MessageChecksum, "first message survives a later CRC failure");
    }
}
