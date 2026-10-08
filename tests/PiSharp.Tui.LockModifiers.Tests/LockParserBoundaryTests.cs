using System.Text.Json;
using PiSharp.Tui;
using PiSharp.Tui.Input;

internal static class LockParserBoundaryTests
{
    internal static object Run(JsonElement[] rows)
    {
        var controls = new List<object>(); var failed = 0; var rejectedMasks = 0;
        void Check(string id, bool passed) { if (!passed) failed++; controls.Add(new { id, passed }); }
        bool Unknown(string raw)
        {
            var d = new TerminalInputDecoder(); var e = d.Feed(raw.AsSpan()).Concat(d.Complete()).ToArray();
            return e.Length == 1 && e[0] is TerminalUnknownSequence u && u.Sequence == raw;
        }
        for (var mask = 0; mask < 256; mask++) if ((mask & ~207) != 0)
            foreach (var action in new[] {1,2,3}) { rejectedMasks++; Check($"unsupported-mask-{mask}-action-{action}", Unknown($"\u001b[120;{mask+1}:{action}u")); }
        foreach (var modifier in new[] { "0", "-1", "257", "2147483647", "2147483648", "+65", "" })
            Check("invalid-modifier-"+modifier, Unknown("\u001b[120;"+modifier+"u"));
        foreach (var code in new[] { "-1", "55296", "56319", "1114112", "2147483647", "2147483648" })
            Check("invalid-scalar-"+code, Unknown("\u001b["+code+";193u"));
        foreach (var raw in new[] { "\u001b[120;193:0u", "\u001b[120;193:4u", "\u001b[120;193:1:1u", "\u001b[120;193;120u", "\u001b[120;;193u" }) Check("unsupported-grammar-"+raw, Unknown(raw));
        // R30 explicitly supersedes only the authored old single-scalar alternate rejection.
        // Its old raw/expected/passed report stays frozen; pinned Source parses and inserts x.
        var alternate = new TerminalInputDecoder();
        var admitted = alternate.Feed("\u001b[120:88;193u").Concat(alternate.Complete()).ToArray();
        Check("source-backed-alternate-admission-\u001b[120:88;193u", admitted.Length == 1 &&
            admitted[0] is TerminalKey { Key: "x", Modifiers: TerminalModifiers.None, Action: TerminalKeyAction.Press } &&
            TerminalInputDecoder.ForEditorInput(admitted[0], false) is TerminalKey { Key: "x" });
        foreach (var mask in new[] {64,128,192}) foreach (var final in "ABCDHF") Check($"legacy-unmodified-boundary-{mask}-{final}",Unknown($"\u001b[1;{mask+1}{final}"));
        var eofPrefixes = 0; var timeoutPrefixes = 0; var prefixFailures = new List<object>();
        foreach (var row in rows)
        {
            var raw = row.GetProperty("raw").GetString()!;
            for (var n=0;n<raw.Length;n++)
            {
                var prefix = raw[..n]; var d = new TerminalInputDecoder(); var e = d.Feed(prefix.AsSpan()).Concat(d.Complete()).ToArray();
                var passed = n == 0 ? e.Length == 0 : n == 1 ? e.Length == 1 && e[0] is TerminalKey { Key: "Escape" } : e.Length == 1 && e[0] is TerminalUnknownSequence u && u.Sequence == prefix;
                eofPrefixes++; if (!passed) { failed++; if(prefixFailures.Count<25)prefixFailures.Add(new { id = row.GetProperty("id").GetString(), n, kind = "eof" }); }
                if(n==0)continue;
                var time = new ManualClock(); var timed = new TerminalInputDecoder(timeProvider:time); var initial = timed.Feed(prefix.AsSpan());
                time.Advance(n==1?34:249); var before = timed.FlushTimeouts(); time.Advance(1); var expired = timed.FlushTimeouts();
                var expectedExpired = n==1 ? expired.Length==1 && expired[0] is TerminalKey {Key:"Escape"} : expired.Length==1 && expired[0] is TerminalUnknownSequence t && t.Sequence==prefix;
                var after = timed.Feed("z".AsSpan()).Concat(timed.Complete()).ToArray();
                var timedPassed = initial.Length==0 && before.Length==0 && expectedExpired && after.Length==1 && after[0] is TerminalText {Text:"z"};
                timeoutPrefixes++; if(!timedPassed){failed++;if(prefixFailures.Count<25)prefixFailures.Add(new {id=row.GetProperty("id").GetString(),n,kind="timeout"});}
            }
        }
        foreach (var group in rows.GroupBy(r=>r.GetProperty("raw").GetString()!.Length))
        {
            var raw = group.First().GetProperty("raw").GetString()!;
            var exact = new TerminalInputDecoder(new(MaximumSequenceCharacters:raw.Length, MaximumChunkCharacters:raw.Length));
            Check("exact-bounds-"+raw.Length,exact.Feed(raw.AsSpan()).Single() is TerminalKey);
            foreach(var sequenceBound in new[]{true,false})
            {
                var d=new TerminalInputDecoder(sequenceBound?new(MaximumSequenceCharacters:raw.Length-1):new(MaximumChunkCharacters:raw.Length-1));
                var resource=false;var closed=false;
                try{d.Feed(raw.AsSpan());}catch(TerminalInputException e){resource=e.Failure==TerminalInputFailure.ResourceLimit;}
                try{d.Feed("x".AsSpan());}catch(TerminalInputException e){closed=e.Failure==TerminalInputFailure.Closed;}
                Check("rejected-closed-"+(sequenceBound?"sequence-":"chunk-")+raw.Length,resource&&closed);
            }
        }
        return new { failed, rejectedMasks, controls, eofPrefixes, timeoutPrefixes, prefixFailures, declaredAllowedMask=207, noSourceExpectationChanged=true };
    }
    private sealed class ManualClock : TimeProvider
    {
        private long now;
        public override long TimestampFrequency=>1000;
        public override long GetTimestamp()=>now;
        internal void Advance(long milliseconds)=>now+=milliseconds;
    }
}
