using System.Text.Json;
using PiSharp.Tui;
using PiSharp.Tui.Input;

if (args.Length != 3 || File.Exists(args[2])) throw new ArgumentException("Frozen Source identity/editor inputs and fresh report required");
using var source = JsonDocument.Parse(File.ReadAllText(args[0]));
var rows = source.RootElement.GetProperty("cases").EnumerateArray().ToArray();
if (rows.Length != 38) throw new InvalidOperationException("Compact38packet MOK inventory changed");
var failedCases = 0; var failedSplits = 0; var splits = 0; var identityFailed = 0;
var cases = new List<object>();
object Project(TerminalInputEvent e) => e switch { TerminalKey k => new { type = "key", k.Key, modifiers = (int)k.Modifiers, action = (int)k.Action }, TerminalUnknownSequence u => new { type = "unknown", raw = u.Sequence }, TerminalText t => new { type = "text", text = t.Text }, _ => new { type = e.GetType().Name } };
TerminalInputEvent[] Decode(string raw, int? split = null)
{
    var d = new TerminalInputDecoder();
    return split is int n ? d.Feed(raw.AsSpan(0,n)).Concat(d.Feed(raw.AsSpan(n))).Concat(d.Complete()).ToArray() : d.Feed(raw.AsSpan()).Concat(d.Complete()).ToArray();
}
(string Key, TerminalModifiers Modifiers) Expected(JsonElement row) => (row.GetProperty("expectedKey").GetString()!, (TerminalModifiers)row.GetProperty("semantic").GetInt32());
foreach (var row in rows)
{
    var raw = row.GetProperty("raw").GetString()!; var expected = Expected(row); var action = (TerminalKeyAction)row.GetProperty("action").GetInt32();
    var canonical = new TerminalKey(expected.Key,expected.Modifiers,action);
    var whole = Decode(raw); var wholeMatches = whole.Length == 1 && whole[0] is TerminalKey key && key == canonical;
    if (!wholeMatches) failedCases++;
    var schedule = new List<object>();
    for (var n=0;n<=raw.Length;n++)
    {
        var events = Decode(raw,n); var matches = events.Length == 1 && events[0] is TerminalKey k && k == canonical;
        var invariant = JsonSerializer.Serialize(events.Select(Project)) == JsonSerializer.Serialize(whole.Select(Project));
        splits++; if (!matches || !invariant) failedSplits++;
        schedule.Add(new { n, matches, invariant });
    }
    var identity = wholeMatches && whole[0].Equals((TerminalInputEvent)canonical) && whole[0].Equals((object)canonical) && whole[0].GetHashCode() == canonical.GetHashCode() && whole[0].ToString() == canonical.ToString();
    if (whole[0] is TerminalKey identityKey) identity &= JsonSerializer.Serialize(identityKey) == JsonSerializer.Serialize(canonical) && (identityKey with { }) == canonical;
    if (!identity) identityFailed++;
    cases.Add(new { id = row.GetProperty("id").GetString(), raw, expected = Project(canonical), actual = whole.Select(Project).ToArray(), wholeMatches, identity, schedule });
}
var boundary = MokBoundaryTests.Run(rows);
var acknowledged = await MokSourceInputTests.Run(args[1]);
var legacy = await LegacyInputParityTests.Run(args[1]);
var interrupt = await MokInterruptTests.Run(Path.Combine(Path.GetDirectoryName(args[0])!, "mok-interrupt-source-observations.json"));
var boundaryFailed = JsonSerializer.SerializeToElement(boundary).GetProperty("failed").GetInt32();
var acknowledgedFailed = JsonSerializer.SerializeToElement(acknowledged).GetProperty("failed").GetInt32();
var legacyFailed = JsonSerializer.SerializeToElement(legacy).GetProperty("failed").GetInt32();
var interruptFailed = JsonSerializer.SerializeToElement(interrupt).GetProperty("failed").GetInt32();
var failed = failedCases + failedSplits + identityFailed + boundaryFailed + acknowledgedFailed + legacyFailed + interruptFailed;
File.WriteAllText(args[2],JsonSerializer.Serialize(new { sourceCases = rows.Length, protocol = "xterm-modifyOtherKeys", splits, failedCases, failedSplits, identityFailed, cases, boundary, acknowledged, legacy, interrupt, failed, allOwnedExecutionsJoined = true, fullNativeGate = false, physicalTerminal = false, completePackageAcceptance = false,
    scope = "modifyOtherKeys38structural packet forms/all splits and20public Source editor witnesses on both actual input routes; Source exact raw binding versus printable lock semantics retained;17Sourceinterrupt bindings on both actual routes and two held-read cancellation joins" },new JsonSerializerOptions { WriteIndented = true })+"\n");
Console.WriteLine(JsonSerializer.Serialize(new { sourceCases = rows.Length, splits, failedCases, failedSplits, identityFailed, boundaryFailed, acknowledgedCases = 20, acknowledgedFailed, legacyCases = 20, legacyFailed, interruptCases = 34, interruptFailed, failed, allOwnedExecutionsJoined = true }));
return failed == 0 ? 0 : 1;