// Exact next/link/binary.ts wire layout, Pi f9a26365568127461c47a07a37dcb42c140a3ac5.
using System.Text.Json.Nodes;

namespace PiSharp.Chord.Link;

internal sealed class BinaryCodec : ICodec
{
    internal static BinaryCodec Instance { get; } = new();
    public string Name => "binary";
    private static readonly string[] Kinds = ["hello", "hold", "release", "call", "abort", "bound", "state", "failed", "result", "error", "gone", "retry", "loading", "ack", "closed", "ping", "pong", "revoke"];
    private static readonly string[] Causes = ["transient", "broken", "waiting"];
    public byte[] Encode(Message message)
    {
        var w = new Writer(); var kind = Array.IndexOf(Kinds, message.Kind);
        if (kind < 0) throw new ArgumentException("binary: an unknown kind");
        var trace = message switch { HoldMessage hold => hold.Trace, CallMessage call => call.Trace, _ => null };
        var old = message is CallMessage { Old: true };
        w.Byte((byte)(kind | (message.Origin is null ? 0 : 128) | (trace is null ? 0 : 64) | (old ? 32 : 0)));
        if (message.Origin is { } origin) w.Number(origin);
        if (trace is not null) w.Json(MessageCheck.CarrierJson(trace));
        switch (message)
        {
            case HelloMessage x: w.Number(x.Protocol); w.Hex(x.Incarnation.Replace("-", ""), 16); w.Number(x.Calls); break;
            case HoldMessage x:
                w.Number(x.Hold); w.String(x.Type); w.Number(x.Version); w.Hex(x.Fingerprint, 8); w.Part(x.Params);
                w.String(x.Address.Kind); w.Json(x.Address.Params);
                w.Byte((byte)((x.Principal is null ? 0 : 1) | (x.Incarnation is null ? 0 : 2) | (x.Chain is null ? 0 : 4) | (x.Limit is null ? 0 : 8)));
                if (x.Principal is not null) w.Json(x.Principal);
                if (x.Incarnation is not null) w.Hex(x.Incarnation.Replace("-", ""), 16);
                if (x.Chain is not null) { w.Number(x.Chain.Count); foreach(var id in x.Chain) w.Hex(id.Replace("-", ""), 16); }
                if (x.Limit is { } limit) w.Number(limit);
                break;
            case ReleaseMessage x: w.Number(x.Hold); break;
            case LoadingMessage x: w.Number(x.Hold); break;
            case RetryMessage x: w.Number(x.Hold); w.Number(x.Hops); break;
            case CallMessage x: w.Number(x.Call); w.Number(x.Hold); w.Number(x.Method); w.Payload(x.Args, x.Segments); break;
            case AbortMessage x: w.Number(x.Call); break;
            case BoundMessage x:
                w.Number(x.Holds.Count);
                foreach(var e in x.Holds)
                {
                    w.Number(e.Hold); w.Number(e.Variant); w.Number(e.Members.Count);
                    foreach(var p in e.Members) { w.Number(p.Member); w.Part(p.Value); }
                    w.Number(e.Failed.Count); w.Failures(e.Failed);
                }
                break;
            case StateMessage x:
                w.Number(x.Holds.Count);
                foreach(var e in x.Holds)
                {
                    w.Number(e.Hold); w.Number(e.Members.Count);
                    foreach(var p in e.Members) { w.Number(p.Member); w.Part(p.Ops); }
                    w.Number(e.Failed.Count * 2L + (e.Retired.Count > 0 ? 1 : 0)); w.Failures(e.Failed);
                    if (e.Retired.Count > 0) { w.Number(e.Retired.Count); foreach(var p in e.Retired) w.Number(p); }
                }
                break;
            case FailedMessage x:
                w.Number(x.Hold); var cause = Array.IndexOf(Causes, x.Cause.Kind);
                if (cause < 0) throw new ArgumentException("binary: an unknown failure kind");
                w.Byte((byte)cause); w.Json(x.Cause.Error.Json); w.Number(x.RetryIn is null ? 0 : checked(x.RetryIn.Value + 1)); break;
            case ResultMessage x: w.Number(x.Call); w.Payload(x.Value, x.Segments); break;
            case ErrorMessage x: w.Number(x.Call); w.Json(x.Error.Json); break;
            case GoneMessage x: w.Number(x.Hold); w.Json(x.Error.Json); break;
            case RevokeMessage x: w.Json(x.Revocation); break;
        }
        return w.Finish();
    }
    public Message Decode(ReadOnlyMemory<byte> bytes)
    {
        var r = new Reader(bytes); var header = r.Byte();
        long? origin = (header & 128) == 0 ? null : r.Number();
        var trace = (header & 64) == 0 ? null : MessageCheck.ReadCarrier(r.Json());
        var old = (header & 32) != 0;
        var kind = header & 31;
        if (kind >= Kinds.Length) throw new ArgumentException("binary: an unknown kind");
        if (trace is not null && kind is not (1 or 3) || old && kind != 3) throw MessageCheck.Invalid("kind flags");
        Message result;
        switch (kind)
        {
            case 0: result = new HelloMessage(r.Number(), r.Uuid(), r.Number()); break;
            case 1:
                var hold = r.Number(); var type = r.String(); var version = r.Number(); var fingerprint = r.Hex(8); var p = r.Part();
                var address = new WireAddress(r.String(), r.Json()); var flags = r.Byte();
                if (flags > 15) throw new ArgumentException("binary: unknown hold flags");
                // A present JSON null cannot become an omitted optional principal in the typed representation.
                var principal = (flags & 1) == 0 ? null : r.Json() ?? throw MessageCheck.Invalid("principal");
                var incarnation = (flags & 2) == 0 ? null : r.Uuid();
                var chain = (flags & 4) == 0 ? null : r.List(r.Uuid);
                long? limit = (flags & 8) == 0 ? null : r.Number();
                result = new HoldMessage(hold,type,version,fingerprint,p,address,principal,incarnation,chain,limit,trace); break;
            case 2: result = new ReleaseMessage(r.Number()); break;
            case 3:
                var call = r.Number(); var h = r.Number(); var method = r.Number(); var args = r.Payload();
                result = new CallMessage(call,h,method,args.Part,args.Segments,trace,old ? true : null); break;
            case 4: result = new AbortMessage(r.Number()); break;
            case 5:
                result = new BoundMessage(r.List(() => new BoundEntry(r.Number(),r.Number(),
                    r.List(() => new BoundMember(r.Number(), r.Part())), r.Failures(r.Number())))); break;
            case 6:
                result = new StateMessage(r.List(() =>
                {
                    var id = r.Number(); var members = r.List(() => new StateMember(r.Number(),r.Part()));
                    var f = r.Number(); var failed = r.Failures(f / 2); var retired = f % 2 == 0 ? [] : r.List(r.Number);
                    return new StateEntry(id,members,failed,retired);
                })); break;
            case 7:
                var hid = r.Number(); var c = r.Byte();
                if (c >= Causes.Length) throw new ArgumentException("binary: an unknown failure kind");
                var error = r.Error(); var retry = r.Number(); result = new FailedMessage(hid,new FailureCause(Causes[c],error),retry == 0 ? null : retry - 1); break;
            case 8: var cid = r.Number(); var value = r.Payload(); result = new ResultMessage(cid,value.Part,value.Segments); break;
            case 9: result = new ErrorMessage(r.Number(),r.Error()); break;
            case 10: result = new GoneMessage(r.Number(),r.Error()); break;
            case 11: result = new RetryMessage(r.Number(),r.Number()); break;
            case 12: result = new LoadingMessage(r.Number()); break;
            case 13: result = new AckMessage(); break;
            case 14: result = new ClosedMessage(); break;
            case 15: result = new PingMessage(); break;
            case 16: result = new PongMessage(); break;
            case 17: result = new RevokeMessage(r.Json()); break;
            default: throw new ArgumentException("binary: an unknown kind");
        }
        r.End(); return result with { Origin = origin };
    }
    private sealed class Writer
    {
        private readonly MemoryStream _stream = new();
        internal void Byte(byte b) => _stream.WriteByte(b);
        internal void Number(long value)
        {
            MessageCheck.Number(value);
            while(value >= 128) { Byte((byte)((value % 128) | 128)); value /= 128; } Byte((byte)value);
        }
        internal void Raw(ReadOnlyMemory<byte> bytes) => _stream.Write(bytes.Span);
        internal void Hex(string text, int count)
        {
            if (text.Length != count * 2 || text.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
                throw new ArgumentException($"binary: not {count} bytes of hex");
            Raw(Convert.FromHexString(text));
        }
        internal void String(string text) => Text(text,1);
        private void Text(string text, int scale)
        {
            var bytes = Parts.OutgoingUtf8.GetBytes(text); var length = (long)bytes.Length * scale;
            // Upstream deliberately uses a five-byte padded LEB128 length for >=4096 UTF-16 units.
            if (text.Length >= 4096)
                for(var index=0;index<5;index++) { Byte((byte)((byte)(length % 128) | (index < 4 ? 128 : 0))); length /= 128; }
            else Number(length);
            Raw(bytes);
        }
        internal void Json(JsonNode? node) => String(ChordJson.Stringify(node));
        internal void Part(Part p)
        {
            if (p is global::PiSharp.Chord.Link.Part.Text text) { Text(text.Value,2); return; }
            var bytes = p is global::PiSharp.Chord.Link.Part.Bytes plain ? plain.Value : ((global::PiSharp.Chord.Link.Part.Deflated)p).Value;
            Number(bytes.Length * 2L + (p is global::PiSharp.Chord.Link.Part.Deflated ? 1 : 0)); Raw(bytes);
        }
        internal void Payload(Part? p, IReadOnlyList<ReadOnlyMemory<byte>>? segments)
        {
            if (p is null) { Number(0); return; }
            Number(segments is null ? 1 : 3); Part(p);
            if (segments is null) return;
            Number(segments.Count); foreach(var s in segments) Number(s.Length); foreach(var s in segments) Raw(s);
        }
        internal void Failures(IReadOnlyList<FailedMember> failures) { foreach(var f in failures) { Number(f.Member); Json(f.Error.Json); } }
        internal byte[] Finish() => _stream.ToArray();
    }
    private sealed class Reader(ReadOnlyMemory<byte> bytes)
    {
        private int _offset;
        internal byte Byte() => _offset < bytes.Length ? bytes.Span[_offset++] : throw new ArgumentException("binary: the message ends early");
        internal long Number()
        {
            long result = 0, scale = 1;
            for(var count=0;count<8;count++)
            {
                var next = Byte(); var digit = next & 127;
                if (digit > (MessageCheck.MaxSafeInteger-result)/scale) throw new ArgumentException("binary: a varint does not fit");
                result += digit*scale; if (next<128) return result;
                scale *=128;
            }
            throw new ArgumentException("binary: a varint does not fit");
        }
        internal ReadOnlyMemory<byte> Raw(long length)
        {
            if (length < 0 || length > bytes.Length-_offset) throw new ArgumentException("binary: the message ends early");
            var result=bytes.Slice(_offset,(int)length); _offset+=(int)length; return result;
        }
        internal string String() => Parts.Utf8.GetString(Raw(Number()).Span);
        internal JsonNode? Json() => Parts.ParseJson(Raw(Number()));
        internal string Hex(int count) => Convert.ToHexString(Raw(count).Span).ToLowerInvariant();
        internal string Uuid() { var h=Hex(16); return $"{h[..8]}-{h[8..12]}-{h[12..16]}-{h[16..20]}-{h[20..]}"; }
        internal Part Part() { var n=Number(); var raw=Raw(n/2); return n%2==0 ? new global::PiSharp.Chord.Link.Part.Bytes(raw) : new global::PiSharp.Chord.Link.Part.Deflated(raw); }
        internal T[] List<T>(Func<T> read) => List(Number(),read);
        private T[] List<T>(long count, Func<T> read)
        {
            if(count > bytes.Length-_offset) throw new ArgumentException("binary: a count does not fit");
            var result=new T[(int)count]; for(var i=0;i<result.Length;i++) result[i]=read(); return result;
        }
        internal WireError Error() => new(Json() as JsonObject ?? throw MessageCheck.Invalid("wire error"));
        internal FailedMember[] Failures(long count) => List(count,()=>new FailedMember(Number(),Error()));
        internal (Part? Part,IReadOnlyList<ReadOnlyMemory<byte>>? Segments) Payload()
        {
            var flags=Number(); if(flags==0) return (null,null);
            if(flags is not (1 or 3)) throw new ArgumentException("binary: unknown payload flags");
            var p=Part(); if(flags==1) return (p,null);
            var lengths=List(Number); return (p,lengths.Select(Raw).ToArray());
        }
        internal void End() { if(_offset!=bytes.Length) throw new ArgumentException("binary: bytes after the message"); }
    }
}
