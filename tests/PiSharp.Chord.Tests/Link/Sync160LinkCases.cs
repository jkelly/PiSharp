using System.Text;
using System.Text.Json.Nodes;
using PiSharp.Chord.Data;
using PiSharp.Chord.Link;
using WireFailure = PiSharp.Chord.Link.FailureCause;

// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/test/link/message.test.ts and channel.test.ts, what 1.6.0 added:
// a hello without a call window, event acks, the event message, the failure cause denied, and a channel's fence (slice 14 §2.3). The
// protocol version stays 1. message.test.ts's EVERY_KIND, with its 1.6.0 additions, is run through upstream's own codecs by
// generate-goldens160.mjs into tests/fixtures/chord-link-v160.json, which link.160.frozen-codec-goldens checks byte for byte in both
// codecs; the 1.5.2 fixture beside it keeps passing unchanged (link.152.frozen-codec-goldens). The other cases port the assertions the
// two test files added, with upstream's bytes, tuples and messages, on the native test links of LinkCases.cs (a ScriptLink delivers
// what it is pushed synchronously, which stands in for upstream's `await transported()`). Authored cases are named link.160-*.
internal static partial class Program
{
    private static readonly JsonObject Sync160WireError = new() { ["kind"] = "chord", ["error"] = new JsonObject { ["code"] = "unavailable", ["message"] = "m" } };

    private static Message Sync160Text(string tuple) => TextCodec.Instance.Decode(Encoding.UTF8.GetBytes(tuple));

    private static FailedMessage Sync160Failed(string kind) => new(0, new WireFailure(kind, new WireError((JsonObject)Sync160WireError.DeepClone())));

    /// <summary>An open channel on a script link (upstream <c>fenced</c>): the peer answers no <c>ping</c> by itself.</summary>
    private static (Channel Channel, ScriptLink Link, LinkHandler Handler) Sync160Fenced(ICodec codec, List<Exception>? errors = null)
    {
        var link = new ScriptLink(); var handler = new LinkHandler();
        var channel = new Channel(link, handler, new(codec, LinkIncarnation, 1, true, error => errors?.Add(error), new LinkClock()));
        channel.Start();
        link.Push(codec.Encode(new HelloMessage(1, LinkIncarnation, 1)));
        return (channel, link, handler);
    }

    private static int Sync160Pings(ICodec codec, ScriptLink link) => link.Sent.Count(bytes => codec.Decode(bytes) is PingMessage);

    private static IEnumerable<(string, Func<Task>)> Sync160LinkCases()
    {
        yield return Case("link.160.frozen-codec-goldens", () =>
        {
            var root = Directory.GetCurrentDirectory();
            while (!File.Exists(Path.Combine(root, "tests/fixtures/chord-link-v160.json")))
                root = Directory.GetParent(root)?.FullName ?? throw new Exception("fixture missing");
            var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "tests/fixtures/chord-link-v160.json")))!;
            Equal("faac585884ae2c013a6b1400f3e9e77f10b34bac", fixture["sourceSha"]!.GetValue<string>(), "frozen identity");
            var kinds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in fixture["rows"]!.AsArray())
            {
                var text = Convert.FromBase64String(row!["text"]!.GetValue<string>());
                var binary = Convert.FromBase64String(row["binary"]!.GetValue<string>());
                var decoded = MessageCheck.Check(TextCodec.Instance.Decode(text));
                kinds.Add(decoded.Kind);
                Sequence(text, TextCodec.Instance.Encode(decoded), "text " + row["id"]);
                Sequence(binary, BinaryCodec.Instance.Encode(decoded), "binary " + row["id"]);
                var binaryDecoded = MessageCheck.Check(BinaryCodec.Instance.Decode(binary));
                Sequence(binary, BinaryCodec.Instance.Encode(binaryDecoded), "binary roundtrip " + row["id"]);
                Sequence(text, TextCodec.Instance.Encode(binaryDecoded), "cross-codec " + row["id"]);
            }
            Equal(62, fixture["rows"]!.AsArray().Count, "every frame of the fixture");
            // One message of every kind, the 1.6.0 one included.
            Equal(19, kinds.Count, "every kind");
            Check(kinds.Contains("event"), "the event kind");
        });
        yield return Case("link.message.binary-writes-no-call-window-as-0-and-an-event-ack-as-the-third-flag-and-its-count", () =>
        {
            Equal(1 + 1 + 16 + 1, BinaryCodec.Instance.Encode(new HelloMessage(1, LinkIncarnation, 1)).Length, "a hello with a window");
            // No call window is written as 0 (a window is at least 1) and read back as absent.
            var none = BinaryCodec.Instance.Encode(new HelloMessage(1, LinkIncarnation));
            Equal((byte)0, none[^1], "no window is 0");
            Equal(new HelloMessage(1, LinkIncarnation), (HelloMessage)BinaryCodec.Instance.Decode(none), "read back as absent");
            Check(((HelloMessage)BinaryCodec.Instance.Decode(none)).Calls is null, "no window");
            Sequence([(byte)13], BinaryCodec.Instance.Encode(new AckMessage()), "a state ack stays one byte");
            // An event ack: the kind byte's third flag, then its count.
            Sequence([(byte)(13 | 0x20), (byte)3], BinaryCodec.Instance.Encode(new AckMessage(3)), "an event ack");
            Equal(new AckMessage(3), (AckMessage)BinaryCodec.Instance.Decode(BinaryCodec.Instance.Encode(new AckMessage(3))), "an event ack read back");
            Check(((AckMessage)BinaryCodec.Instance.Decode(new byte[] { 13 })).Events is null, "a state ack has no count");
            Refuses(() => BinaryCodec.Instance.Decode(new byte[] { 14 | 0x20, 1 }), "binary: an unknown kind");
            // An origin: the kind byte's high bit, then the origin as a varint.
            Sequence([(byte)(13 | 0x80), (byte)0xac, (byte)0x02], BinaryCodec.Instance.Encode(new AckMessage { Origin = 300 }), "an ack of an origin");
            Sequence([(byte)(13 | 0x80 | 0x20), (byte)0xac, (byte)0x02, (byte)0xa0, (byte)0x1f], BinaryCodec.Instance.Encode(new AckMessage(4000) { Origin = 300 }),
                "an event ack of an origin");
        });
        yield return Case("link.message.binary-writes-a-failures-cause-as-one-byte-0-transient-1-broken-2-waiting-3-denied-refuses-another", () =>
        {
            Sequence([(byte)0, (byte)1, (byte)2, (byte)3],
                new[] { "transient", "broken", "waiting", "denied" }.Select(kind => BinaryCodec.Instance.Encode(Sync160Failed(kind))[2]), "the cause bytes");
            var unknown = BinaryCodec.Instance.Encode(Sync160Failed("waiting"));
            unknown[2] = 4;
            Refuses(() => BinaryCodec.Instance.Decode(unknown), "an unknown failure kind");
            Equal("denied", ((FailedMessage)BinaryCodec.Instance.Decode(BinaryCodec.Instance.Encode(Sync160Failed("denied")))).Cause.Kind, "denied read back");
            Refuses(() => BinaryCodec.Instance.Encode(Sync160Failed("refused")), "binary: an unknown failure kind");
        });
        yield return Case("link.message.text-writes-denied-as-d-an-event-ack-with-its-count-and-events-as-entries", () =>
        {
            Equal("""["f",2,"d",{"kind":"chord","error":{"code":"unavailable","message":"m"}}]""", TextCodec.Describe(Sync160Failed("denied") with { Hold = 2 }), "denied");
            Equal("denied", ((FailedMessage)Sync160Text("""["f",2,"d",{"kind":"chord","error":{"code":"unavailable","message":"m"}}]""")).Cause.Kind, "denied read back");
            Refuses(() => Sync160Text("""["f",2,"x",{}]"""), "an unknown failure kind");
            Equal("""["a"]""", TextCodec.Describe(new AckMessage()), "a state ack");
            Equal("""["a",2]""", TextCodec.Describe(new AckMessage(2)), "an event ack");
            Equal(new AckMessage(2), (AckMessage)Sync160Text("""["a",2]"""), "an event ack read back");
            Equal("""["E",[[1,0,9,[0,{"y":1}]]]]""", TextCodec.Describe(new EventMessage([new WireEvent(1, 0, 9, new Part.Text("""{"y":1}"""))])), "an event message");
            Equal("""["@",7,["E",[]]]""", TextCodec.Describe(new EventMessage([]) { Origin = 7 }), "a relay's filtered one");
            Equal($$"""["h",1,"{{LinkIncarnation}}"]""", TextCodec.Describe(new HelloMessage(1, LinkIncarnation)), "a hello without a window");
            Equal($$"""["h",1,"{{LinkIncarnation}}",256]""", TextCodec.Describe(new HelloMessage(1, LinkIncarnation, 256)), "a hello with a window");
            Check(((HelloMessage)Sync160Text($$"""["h",1,"{{LinkIncarnation}}"]""")).Calls is null, "no window read back");
            Equal(256L, ((HelloMessage)Sync160Text($$"""["h",1,"{{LinkIncarnation}}",256]""")).Calls, "a window read back");
            var events = (EventMessage)Sync160Text("""["@",2,["E",[[1,0,9,[0,{"y":1}]],[300,2,0,[0,null]]]]]""");
            Equal(2L, events.Origin, "the origin");
            Sequence([(1L, 0L, 9L, """{"y":1}"""), (300L, 2L, 0L, "null")], events.Events.Select(e => (e.Hold, e.Member, e.At, Parts.Text(e.Value))), "the entries");
            // Malformed tuples.
            Refuses(() => Sync160Text("""["E",[[1,0,9]]]"""), "text: an event entry with 3 fields");
            Refuses(() => Sync160Text("""["E",[[1,0,9,[0,null],5]]]"""), "text: an event entry with 5 fields");
            Refuses(() => Sync160Text("""["E"]"""), "text: incorrect tuple arity");
            Refuses(() => Sync160Text("""["E",[1]]"""), "text: expected a list");
            Refuses(() => Sync160Text("""["a",1,2]"""), "text: incorrect tuple arity");
            Refuses(() => Sync160Text("""["h",1]"""), "text: incorrect tuple arity");
        });
        yield return Case("link.message.an-event-keeps-a-deflated-part-as-it-is-in-both-codecs", () =>
        {
            // The codecs never inflate: a part its origin deflated travels as its bytes.
            var deflated = new Part.Deflated(new byte[] { 0x4b, 0x04, 0x00 });
            var message = new EventMessage([new WireEvent(0, 1, 5, deflated)]) { Origin = 2 };
            foreach (var codec in new ICodec[] { BinaryCodec.Instance, TextCodec.Instance })
            {
                var decoded = (EventMessage)MessageCheck.Check(codec.Decode(codec.Encode(message)));
                Equal(2L, decoded.Origin, codec.Name + " origin");
                var entry = decoded.Events.Single();
                Sequence([0L, 1L, 5L], [entry.Hold, entry.Member, entry.At], codec.Name + " entry");
                Check(entry.Value is Part.Deflated kept && kept.Value.Span.SequenceEqual(deflated.Value.Span), codec.Name + " keeps the deflated part");
                Sequence(codec.Encode(message), codec.Encode(decoded), codec.Name + " round trip");
            }
        });
        yield return Case("link.160-the-message-check-covers-the-new-fields", () =>
        {
            // hello.calls: opt(int({ min: 1 })); ack.events: opt(int({ min: 1 })).
            MessageCheck.Check(new HelloMessage(1, LinkIncarnation));
            MessageCheck.Check(new HelloMessage(1, LinkIncarnation, 1));
            Throws<ArgumentException>(() => MessageCheck.Check(new HelloMessage(1, LinkIncarnation, 0)), "a window of 0");
            MessageCheck.Check(new AckMessage());
            MessageCheck.Check(new AckMessage(1) { Origin = 3 });
            Throws<ArgumentException>(() => MessageCheck.Check(new AckMessage(0)), "an event ack of 0");
            // A flagged binary ack whose count is 0 decodes and then fails the check, as upstream's schema refuses it.
            Throws<ArgumentException>(() => MessageCheck.Check(BinaryCodec.Instance.Decode(new byte[] { 13 | 0x20, 0 })), "a binary event ack of 0");
            Throws<ArgumentException>(() => MessageCheck.Check(Sync160Text($$"""["h",1,"{{LinkIncarnation}}",0]""")), "a text window of 0");
            // event: hold an id, member an index, at a whole number of milliseconds, each at least 0; an origin is allowed, a hello's is not.
            MessageCheck.Check(new EventMessage([new WireEvent(0, 0, 1_762_819_200_123, new Part.Text("null"))]) { Origin = 1 });
            MessageCheck.Check(new EventMessage([]));
            foreach (var bad in new[] { new WireEvent(-1, 0, 0, new Part.Text("1")), new WireEvent(0, -1, 0, new Part.Text("1")), new WireEvent(0, 0, -1, new Part.Text("1")) })
                Throws<ArgumentException>(() => MessageCheck.Check(new EventMessage([bad])), "a negative field");
            Throws<ArgumentException>(() => MessageCheck.Check(new EventMessage([]) { Origin = 0 }), "an origin of 0");
            // failed.cause.kind: the four kinds, which are FailureKind's names (the remote layer maps one to the other by name).
            foreach (var kind in MessageCheck.CauseKinds) MessageCheck.Check(Sync160Failed(kind));
            Throws<ArgumentException>(() => MessageCheck.Check(Sync160Failed("refused")), "an unknown cause kind");
            Sequence(Enum.GetNames<FailureKind>().Select(name => name.ToLowerInvariant()), MessageCheck.CauseKinds, "the wire kinds are FailureKind's names, in order");
            // The third flag belongs to a call and an ack only (0x20 on hello, release, closed, event).
            foreach (var kind in new byte[] { 0, 2, 14, 18 })
                Refuses(() => BinaryCodec.Instance.Decode(new byte[] { (byte)(kind | 0x20), 0 }), "binary: an unknown kind");
            Refuses(() => BinaryCodec.Instance.Decode(new byte[] { 19 }), "binary: an unknown kind");
            Check(BinaryCodec.Instance.Decode(new byte[] { 18, 0 }) is EventMessage { Events.Count: 0 }, "kind 18 is event");
        });
        foreach (var codec in new ICodec[] { BinaryCodec.Instance, TextCodec.Instance })
        {
            yield return Case($"link.channel.a-fence-runs-after-the-pong-of-its-own-ping-a-liveness-pong-in-transit-does-not-end-it.{codec.Name}", () =>
            {
                var (channel, link, _) = Sync160Fenced(codec);
                var ran = new List<string>();
                // A liveness `ping` goes out (the link was silent); its `pong` is still in transit when the fence starts.
                channel.Tick(20_000, 0, 15_000, 30_000);
                channel.Fence(() => ran.Add("first"));
                channel.Fence(() => ran.Add("second"));
                Equal(3, Sync160Pings(codec, link), "three pings");
                link.Push(codec.Encode(new PongMessage()));
                Sequence([], ran, "the liveness pong ends no fence");
                link.Push(codec.Encode(new PongMessage()));
                Sequence(["first"], ran, "the first fence's pong");
                link.Push(codec.Encode(new PongMessage()));
                Sequence(["first", "second"], ran, "the second fence's pong");
                Check(!channel.Closed, "the channel stays open");
            });
            yield return Case($"link.channel.a-fence-is-cancelled-by-what-it-returned-and-runs-nothing-once-the-link-closed.{codec.Name}", () =>
            {
                var (channel, link, _) = Sync160Fenced(codec);
                var ran = new List<string>();
                var cancel = channel.Fence(() => ran.Add("cancelled"));
                channel.Fence(() => ran.Add("kept"));
                cancel();
                link.Push(codec.Encode(new PongMessage()));
                link.Push(codec.Encode(new PongMessage()));
                Sequence(["kept"], ran, "the cancelled fence does not run");
                channel.Fence(() => ran.Add("closed"));
                channel.Close();
                link.Push(codec.Encode(new PongMessage()));
                Sequence(["kept"], ran, "a closed link runs none");
                // Asked of a closed channel: never runs, sends nothing, and its cancel is harmless.
                var sent = link.Sent.Count;
                var late = channel.Fence(() => ran.Add("late"));
                late();
                Equal(sent, link.Sent.Count, "nothing is sent on a closed channel");
                Sequence(["kept"], ran, "a fence of a closed channel never runs");
            });
        }
        yield return Case("link.160-a-fence-asked-before-the-handshake-waits-for-it-and-one-may-cancel-or-ask-another-as-it-runs", () =>
        {
            var codec = BinaryCodec.Instance; var link = new ScriptLink();
            var channel = new Channel(link, new LinkHandler(), new(codec, LinkIncarnation, 1, true, _ => { }, new LinkClock()));
            channel.Start();
            var ran = new List<string>();
            channel.Fence(() => ran.Add("early"));
            Equal(0, Sync160Pings(codec, link), "its ping waits for the peer's hello, as every message does");
            link.Push(codec.Encode(new HelloMessage(1, LinkIncarnation)));
            Equal(1, Sync160Pings(codec, link), "sent once the channel opened");
            Sequence([], ran, "not run yet");
            link.Push(codec.Encode(new PongMessage()));
            Sequence(["early"], ran, "run at its pong");
            // A fence that cancels a later one as it runs, and asks a new one: the cancelled one never runs, the new one waits for its own pong.
            Action? cancelSecond = null;
            channel.Fence(() => { ran.Add("a"); cancelSecond!(); channel.Fence(() => ran.Add("c")); });
            cancelSecond = channel.Fence(() => ran.Add("b"));
            link.Push(codec.Encode(new PongMessage()));
            link.Push(codec.Encode(new PongMessage()));
            Sequence(["early", "a"], ran, "the cancelled fence did not run, the new one waits");
            link.Push(codec.Encode(new PongMessage()));
            Sequence(["early", "a", "c"], ran, "the new fence ran at its own pong");
            // A pong nobody waits for is ignored.
            link.Push(codec.Encode(new PongMessage()));
            Check(!channel.Closed, "an extra pong is no error");
        });
        yield return Case("link.160-a-channel-without-a-call-window-says-so-in-its-hello-and-reads-the-peers", () =>
        {
            foreach (var codec in new ICodec[] { BinaryCodec.Instance, TextCodec.Instance })
            {
                var link = new ScriptLink(); var handler = new LinkHandler();
                var channel = new Channel(link, handler, new(codec, LinkIncarnation, null, true, _ => { }, new LinkClock()));
                channel.Start();
                Check(codec.Decode(link.Sent[0]) is HelloMessage { Protocol: 1, Calls: null }, codec.Name + ": a hello without calls");
                Check(channel.PeerCalls is null, codec.Name + ": no window before the peer's hello");
                link.Push(codec.Encode(new HelloMessage(1, LinkIncarnation)));
                Check(channel.Opened && handler.OpenCount == 1, codec.Name + ": open");
                Check(channel.PeerCalls is null, codec.Name + ": the peer announced no window");
                var windowed = new ScriptLink();
                var other = new Channel(windowed, new LinkHandler(), new(codec, LinkIncarnation, 256, false, _ => { }, new LinkClock()));
                other.Start();
                Equal(256L, ((HelloMessage)codec.Decode(windowed.Sent[0])).Calls, codec.Name + ": a hello with its window");
                windowed.Push(codec.Encode(new HelloMessage(1, LinkIncarnation, 12)));
                Equal(12L, other.PeerCalls, codec.Name + ": the peer's window");
                other.Close();
                Check(other.PeerCalls is null, codec.Name + ": none once closed");
            }
        });
    }
}
