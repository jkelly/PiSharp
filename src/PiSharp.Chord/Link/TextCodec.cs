// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 next/link/text.ts.
using System.Globalization;
using System.Text.Json.Nodes;

namespace PiSharp.Chord.Link;

internal sealed class TextCodec : ICodec
{
    internal static TextCodec Instance { get; } = new();
    public string Name => "text";
    private static string N(long n) => n.ToString(CultureInfo.InvariantCulture);
    private static string J(JsonNode? value) => ChordJson.Stringify(value);
    private static string Q(string text) => ChordJson.Quote(text);
    private static string List<T>(IEnumerable<T> values, Func<T,string> encode) => "[" + string.Join(",", values.Select(encode)) + "]";
    private static string Failures(IReadOnlyList<FailedMember> values) => List(values,f=>"["+N(f.Member)+","+J(f.Error.Json)+"]");
    private static string FailedField(IReadOnlyList<FailedMember> values) => values.Count==0 ? "" : ","+Failures(values);
    private static string Payload(string head, Part? part, IReadOnlyList<ReadOnlyMemory<byte>>? segments) => part is null ? head :
        head[..^1]+","+Parts.Tagged(part)+(segments is null ? "" : ","+List(segments,s=>N(s.Length)))+"]";
    internal static string Describe(Message message)
    {
        var text = message switch
        {
            HelloMessage x => "[\"h\","+N(x.Protocol)+","+Q(x.Incarnation)+","+N(x.Calls)+"]",
            HoldMessage x => Hold(x),
            ReleaseMessage x => "[\"R\","+N(x.Hold)+"]",
            CallMessage x => Payload("[\"c\","+N(x.Call)+","+N(x.Hold)+","+N(x.Method)+"]",x.Args,x.Segments),
            AbortMessage x => "[\"x\","+N(x.Call)+"]",
            BoundMessage x => "[\"b\","+List(x.Holds,e=>"["+N(e.Hold)+","+N(e.Variant)+","+List(e.Members,p=>"["+N(p.Member)+","+Parts.Tagged(p.Value)+"]")+FailedField(e.Failed)+"]")+"]",
            StateMessage x => "[\"s\","+List(x.Holds,e=>"["+N(e.Hold)+","+List(e.Members,p=>"["+N(p.Member)+","+Parts.Tagged(p.Ops)+"]")+(e.Retired.Count==0 ? FailedField(e.Failed) : ","+Failures(e.Failed)+","+List(e.Retired,N))+"]")+"]",
            FailedMessage x => "[\"f\","+N(x.Hold)+","+Q(x.Cause.Kind[..1])+","+J(x.Cause.Error.Json)+(x.RetryIn is null ? "" : ","+N(x.RetryIn.Value))+"]",
            ResultMessage x => Payload("[\"r\","+N(x.Call)+"]",x.Value,x.Segments),
            ErrorMessage x => "[\"e\","+N(x.Call)+","+J(x.Error.Json)+"]",
            GoneMessage x => "[\"g\","+N(x.Hold)+","+J(x.Error.Json)+"]",
            RevokeMessage x => "[\"V\","+J(x.Revocation)+"]",
            RetryMessage x => "[\"T\","+N(x.Hold)+","+N(x.Hops)+"]",
            LoadingMessage x => "[\"l\","+N(x.Hold)+"]",
            AckMessage => "[\"a\"]", ClosedMessage => "[\"C\"]", PingMessage => "[\"p\"]", PongMessage => "[\"P\"]",
            _ => throw new ArgumentException("text: an unknown kind"),
        };
        return message.Origin is { } origin ? "[\"@\","+N(origin)+","+text+"]" : text;
    }
    private static string Hold(HoldMessage x)
    {
        var optional = new List<string> { J(x.Principal), x.Incarnation is null ? "null" : Q(x.Incarnation), x.Chain is null ? "null" : List(x.Chain,Q), x.Limit is null ? "null" : N(x.Limit.Value) };
        while(optional.Count>0 && optional[^1]=="null") optional.RemoveAt(optional.Count-1);
        return "[\"H\","+N(x.Hold)+","+Q(x.Type)+","+N(x.Version)+","+Q(x.Fingerprint)+","+Parts.Tagged(x.Params)+",["+Q(x.Address.Kind)+","+J(x.Address.Params)+"]"+(optional.Count==0 ? "" : ","+string.Join(",",optional))+"]";
    }
    public byte[] Encode(Message message)
    {
        var text = Parts.OutgoingUtf8.GetBytes(Describe(message));
        var segments = message switch { CallMessage x=>x.Segments,ResultMessage x=>x.Segments,_=>null };
        if(segments is null) return text;
        var length=checked(text.Length+1+segments.Sum(s=>s.Length)); var output=new byte[length]; text.CopyTo(output,0); output[text.Length]=10;
        var offset=text.Length+1; foreach(var segment in segments) { segment.Span.CopyTo(output.AsSpan(offset)); offset+=segment.Length; } return output;
    }
    public Message Decode(ReadOnlyMemory<byte> bytes)
    {
        var newline=bytes.Span.IndexOf((byte)10); var text=newline<0 ? bytes : bytes[..newline]; var rest=newline<0 ? ReadOnlyMemory<byte>.Empty : bytes[(newline+1)..];
        var tuple = Array(Parts.ParseJson(text)); long? origin=null;
        if(ChordJson.AsString(tuple.ElementAtOrDefault(0))=="@")
        {
            Arity(tuple,3); origin=Number(tuple[1]); tuple=Array(tuple[2]);
            if(tuple.Count==0 || ChordJson.AsString(tuple[0])=="@") throw new ArgumentException("text: an origin wraps one message");
        }
        return Fields(tuple,rest) with { Origin=origin };
    }
    private static Message Fields(JsonArray f, ReadOnlyMemory<byte> rest)
    {
        var tag=String(f.ElementAtOrDefault(0));
        if(tag is not ("c" or "r") && rest.Length>0) throw new ArgumentException("text: byte segments without a payload that carries them");
        long Num(int i)=>Number(f[i]); string Str(int i)=>String(f[i]);
        switch(tag)
        {
            case "h": Arity(f,4); return new HelloMessage(Num(1),Str(2),Num(3));
            case "H":
                Arity(f,7,8,9,10,11); var a=Array(f[6]); Arity(a,2);
                var principal=f.Count>7 ? f[7]?.DeepClone() : null; var incarnation=f.Count>8 && f[8] is not null ? Str(8) : null;
                var chain=f.Count>9 && f[9] is not null ? Array(f[9]).Select(String).ToArray() : null;
                long? limit=f.Count>10 && f[10] is not null ? Num(10) : null;
                return new HoldMessage(Num(1),Str(2),Num(3),Str(4),Parts.FromTagged(f[5]),new WireAddress(String(a[0]),a[1]?.DeepClone()),principal,incarnation,chain,limit);
            case "R": Arity(f,2); return new ReleaseMessage(Num(1));
            case "c": Arity(f,4,5,6); var args=ReadPayload(f,4,rest); return new CallMessage(Num(1),Num(2),Num(3),args.Part,args.Segments);
            case "x": Arity(f,2); return new AbortMessage(Num(1));
            case "b":
                Arity(f,2); return new BoundMessage(Array(f[1]).Select(raw=>
                {
                    var e=Array(raw); Arity(e,3,4);
                    return new BoundEntry(Number(e[0]),Number(e[1]),Pairs(e[2]).Select(p=>new BoundMember(Number(p[0]),Parts.FromTagged(p[1]))).ToArray(),e.Count>3 ? ReadFailures(e[3]) : []);
                }).ToArray());
            case "s":
                Arity(f,2); return new StateMessage(Array(f[1]).Select(raw=>
                {
                    var e=Array(raw); Arity(e,2,3,4);
                    return new StateEntry(Number(e[0]),Pairs(e[1]).Select(p=>new StateMember(Number(p[0]),Parts.FromTagged(p[1]))).ToArray(), e.Count>2 ? ReadFailures(e[2]) : [],e.Count>3 ? Array(e[3]).Select(Number).ToArray() : []);
                }).ToArray());
            case "f":
                Arity(f,4,5); var cause=Str(2) switch { "t"=>"transient","b"=>"broken","w"=>"waiting",_=>throw new ArgumentException("text: an unknown failure kind") };
                return new FailedMessage(Num(1),new FailureCause(cause,Error(f[3])),f.Count==5 ? Num(4) : null);
            case "r": Arity(f,2,3,4); var payload=ReadPayload(f,2,rest); return new ResultMessage(Num(1),payload.Part,payload.Segments);
            case "e": Arity(f,3); return new ErrorMessage(Num(1),Error(f[2]));
            case "g": Arity(f,3); return new GoneMessage(Num(1),Error(f[2]));
            case "V": Arity(f,2); return new RevokeMessage(f[1]?.DeepClone());
            case "T": Arity(f,3); return new RetryMessage(Num(1),Num(2));
            case "l": Arity(f,2); return new LoadingMessage(Num(1));
            case "a": Arity(f,1); return new AckMessage();
            case "C": Arity(f,1); return new ClosedMessage();
            case "p": Arity(f,1); return new PingMessage();
            case "P": Arity(f,1); return new PongMessage();
            default: throw new ArgumentException("text: an unknown kind");
        }
    }
    private static (Part? Part,IReadOnlyList<ReadOnlyMemory<byte>>? Segments) ReadPayload(JsonArray fields,int start, ReadOnlyMemory<byte> rest)
    {
        if(fields.Count<=start+1)
        {
            if(rest.Length>0) throw new ArgumentException("text: byte segments without a payload that carries them");
            return (fields.Count==start ? null : Parts.FromTagged(fields[start]),null);
        }
        var lengths=Array(fields[start+1]); var segments=new List<ReadOnlyMemory<byte>>(); var offset=0;
        foreach(var item in lengths)
        {
            var n=Number(item); if(n<0 || n>rest.Length-offset) throw new ArgumentException("text: a segment length");
            segments.Add(rest.Slice(offset,(int)n)); offset+=(int)n;
        }
        if(offset!=rest.Length) throw new ArgumentException("text: the segments do not fill the message");
        return (Parts.FromTagged(fields[start]),segments);
    }
    private static WireError Error(JsonNode? node) => new(node?.DeepClone() as JsonObject ?? throw MessageCheck.Invalid("wire error"));
    private static FailedMember[] ReadFailures(JsonNode? raw) => Pairs(raw).Select(p=>new FailedMember(Number(p[0]),Error(p[1]))).ToArray();
    private static IEnumerable<JsonArray> Pairs(JsonNode? raw) => Array(raw).Select(p=> { var a=Array(p); Arity(a,2); return a; });
    private static JsonArray Array(JsonNode? node) => node as JsonArray ?? throw new ArgumentException("text: expected a list");
    private static string String(JsonNode? node) => ChordJson.AsString(node) ?? throw new ArgumentException("text: expected a string");
    private static long Number(JsonNode? node)
    {
        var n=ChordJson.AsNumber(node);
        if(n is null || !double.IsFinite(n.Value) || n!=Math.Truncate(n.Value) || Math.Abs(n.Value)>MessageCheck.MaxSafeInteger) throw new ArgumentException("text: expected a safe integer");
        return (long)n.Value;
    }
    private static void Arity(JsonArray fields,params int[] allowed) { if(!allowed.Contains(fields.Count)) throw new ArgumentException("text: incorrect tuple arity"); }
}
