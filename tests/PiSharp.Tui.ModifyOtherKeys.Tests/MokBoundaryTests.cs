using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.Tui;
using PiSharp.Tui.Input;

internal static class MokBoundaryTests
{
    internal static object Run(JsonElement[] rows)
    {
        var failed=0;var controls=new List<object>();var contexts=new List<object>();var eofPrefixes=0;var timeoutPrefixes=0;
        void Check(string id,bool pass){if(!pass)failed++;controls.Add(new{id,pass});}
        var context=typeof(TerminalInputDecoder).GetMethod("ForEditorInput",new[]{typeof(TerminalInputEvent),typeof(bool)});
        TerminalInputEvent Route(TerminalInputEvent input,bool pending)=>context is null?input:(TerminalInputEvent)context.Invoke(null,new object[]{input,pending})!;
        string Project(TerminalInputEvent input)=>JsonSerializer.Serialize(input switch{TerminalKey k=>(object)new{type="key",k.Key,modifiers=(int)k.Modifiers,action=(int)k.Action},TerminalText t=>new{type="text",text=t.Text},TerminalUnknownSequence u=>new{type="unknown",raw=u.Sequence},_=>(object)new{type=input.GetType().Name}});
        foreach(var row in rows)
        {
            var raw=row.GetProperty("raw").GetString()!;var d=new TerminalInputDecoder();var events=d.Feed(raw.AsSpan()).Concat(d.Complete()).ToArray();
            var key=new TerminalKey(row.GetProperty("expectedKey").GetString()!,(TerminalModifiers)row.GetProperty("semantic").GetInt32());
            var printable=row.GetProperty("sourcePrintable").GetString();var mask=row.GetProperty("mask").GetInt32();var code=row.GetProperty("code").GetInt32();
            TerminalInputEvent ordinary=(mask&192)!=0?printable is null?new TerminalUnknownSequence(raw):new TerminalText(printable):code==13&&(mask==0||mask==2)?new TerminalUnknownSequence(raw):key;
            TerminalInputEvent pending=printable is null?ordinary:new TerminalKey(printable,key.Modifiers);
            var ordinaryPass=events.Length==1&&Project(Route(events[0],false))==Project(ordinary);var pendingPass=events.Length==1&&Project(Route(events[0],true))==Project(pending);
            if(!ordinaryPass)failed++;if(!pendingPass)failed++;
            contexts.Add(new{id=row.GetProperty("id").GetString(),ordinaryPass,pendingPass,sourceParsed=row.GetProperty("sourceParsedKey").GetString(),sourcePrintable=printable,expectedOrdinary=Project(ordinary),expectedPending=Project(pending),actualOrdinary=events.Length==1?Project(Route(events[0],false)):null,actualPending=events.Length==1?Project(Route(events[0],true)):null});
            if(events[0] is TerminalKey actual){var clone=actual with{};Check("clone-origin-not-invented-"+row.GetProperty("id").GetString(),ReferenceEquals(clone,Route(clone,false))&&ReferenceEquals(clone,Route(clone,true)));}
            for(var n=0;n<raw.Length;n++)
            {
                var prefix=raw[..n];var end=new TerminalInputDecoder();var e=end.Feed(prefix.AsSpan()).Concat(end.Complete()).ToArray();var ok=n==0?e.Length==0:n==1?e.Length==1&&e[0] is TerminalKey{Key:"Escape"}:e.Length==1&&e[0] is TerminalUnknownSequence u&&u.Sequence==prefix;Check("eof-"+row.GetProperty("id").GetString()+"-"+n,ok);eofPrefixes++;
                if(n==0)continue;var clock=new Clock();var timed=new TerminalInputDecoder(timeProvider:clock);timed.Feed(prefix.AsSpan());clock.Advance(n==1?34:249);var before=timed.FlushTimeouts();clock.Advance(1);var after=timed.FlushTimeouts();var recovered=timed.Feed("z".AsSpan()).Concat(timed.Complete()).ToArray();Check("timeout-"+row.GetProperty("id").GetString()+"-"+n,before.Length==0&&(n==1?after.Length==1&&after[0] is TerminalKey{Key:"Escape"}:after.Length==1&&after[0] is TerminalUnknownSequence u2&&u2.Sequence==prefix)&&recovered.Length==1&&recovered[0] is TerminalText{Text:"z"});timeoutPrefixes++;
            }
        }
        foreach(var raw in new[]{"\u001b[27;0;97~","\u001b[27;17;97~","\u001b[27;33;97~","\u001b[27;209;97~","\u001b[27;257;97~","\u001b[27;2147483648;97~","\u001b[27;1;55296~","\u001b[27;1;1114112~","\u001b[27;1;2147483648~","\u001b[27;;97~","\u001b[27;1;~","\u001b[27;1:3;97~","\u001b[27;1;97;98~","\u001b[027;1;97~"}){var d=new TerminalInputDecoder();var e=d.Feed(raw.AsSpan()).Concat(d.Complete()).ToArray();Check("rejected-"+raw,e.Length==1&&e[0] is TerminalUnknownSequence u&&u.Sequence==raw);}
        foreach(var group in rows.GroupBy(r=>r.GetProperty("raw").GetString()!.Length))
        {
            var raw=group.First().GetProperty("raw").GetString()!;var exact=new TerminalInputDecoder(new(MaximumSequenceCharacters:raw.Length,MaximumChunkCharacters:raw.Length));Check("exact-bound-"+raw.Length,exact.Feed(raw.AsSpan()).Single() is TerminalKey);
            var rejected=new TerminalInputDecoder(new(MaximumSequenceCharacters:raw.Length-1));var limited=false;var closed=false;try{rejected.Feed(raw.AsSpan());}catch(TerminalInputException e){limited=e.Failure==TerminalInputFailure.ResourceLimit;}try{rejected.Complete();}catch(TerminalInputException e){closed=e.Failure==TerminalInputFailure.Closed;}Check("sequence-bound-"+raw.Length,limited&&closed);
        }
        Check("record-public-shape",typeof(TerminalKey).GetProperties(BindingFlags.Instance|BindingFlags.Public).Select(p=>p.Name).Order().SequenceEqual(new[]{"Action","Key","Modifiers"}));
        if(context is not null){var weak=DecodedWeak();GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();Check("weak-wire-origin-released",!weak.TryGetTarget(out _));}
        return new{failed,contextMethodAvailable=context is not null,contexts,controls,eofPrefixes,timeoutPrefixes,sourceExpectedObjectsChanged=false};
    }
    [MethodImpl(MethodImplOptions.NoInlining)] private static WeakReference<TerminalKey> DecodedWeak(){var d=new TerminalInputDecoder();return new((TerminalKey)d.Feed("\u001b[27;65;97~".AsSpan()).Single());}
    private sealed class Clock:TimeProvider{private long now;public override long TimestampFrequency=>1000;public override long GetTimestamp()=>now;internal void Advance(long n)=>now+=n;}
}