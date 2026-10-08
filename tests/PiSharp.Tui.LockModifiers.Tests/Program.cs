using System.Text.Json;
using PiSharp.Tui;
using PiSharp.Tui.Input;

if (args.Length != 3 || File.Exists(args[2])) throw new ArgumentException("Frozen Source identity/editor inputs and fresh report required");
using var source = JsonDocument.Parse(File.ReadAllText(args[0]));
var rows = source.RootElement.GetProperty("cases").EnumerateArray().ToArray();
if (rows.Length != 7872) throw new InvalidOperationException("Complete41code/64mask/3action inventory changed");
var failedCases = 0; var failedSplits = 0; var splits = 0; var identityFailed = 0;
var cases = new List<object>();
object Project(TerminalInputEvent e) => e switch { TerminalKey k => new { type = "key", k.Key, modifiers = (int)k.Modifiers, action = (int)k.Action }, TerminalUnknownSequence u => new { type = "unknown", raw = u.Sequence }, TerminalText t => new { type = "text", text = t.Text }, _ => new { type = e.GetType().Name } };
TerminalInputEvent[] Decode(string raw, int? split = null)
{
    var d = new TerminalInputDecoder();
    return split is int n ? d.Feed(raw.AsSpan(0,n)).Concat(d.Feed(raw.AsSpan(n))).Concat(d.Complete()).ToArray() : d.Feed(raw.AsSpan()).Concat(d.Complete()).ToArray();
}
(string Key, TerminalModifiers Modifiers) Expected(JsonElement row)
{
    var parsed = row.GetProperty("sourceParsedKey").GetString() ?? throw new InvalidOperationException("Source key identity absent");
    var mods = TerminalModifiers.None;
    while (true)
    {
        var separator = parsed.IndexOf('+');
        if (separator <= 0) break;
        var modifier = parsed[..separator] switch { "shift" => TerminalModifiers.Shift, "ctrl" => TerminalModifiers.Control, "alt" => TerminalModifiers.Alt, "super" => TerminalModifiers.Super, _ => (TerminalModifiers)(-1) };
        if ((int)modifier == -1) break;
        mods |= modifier; parsed = parsed[(separator+1)..];
    }
    if ((int)mods != row.GetProperty("semantic").GetInt32()) throw new InvalidOperationException("Frozen public Source modifier disagrees with declared mask");
    return (parsed switch { "tab" => "Tab", "enter" => "Enter", "escape" => "Escape", "backspace" => "Backspace", "left" => "Left", "right" => "Right", "up" => "Up", "down" => "Down", "pageUp" => "PageUp", "pageDown" => "PageDown", "home" => "Home", "end" => "End", "insert" => "Insert", "delete" => "Delete", _ => parsed }, mods);
}
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
var boundary = LockParserBoundaryTests.Run(rows);
var acknowledged = await LockSourceInputTests.Run(args[1]);
var legacy = await LegacyInputParityTests.Run(args[1]);
var boundaryFailed = JsonSerializer.SerializeToElement(boundary).GetProperty("failed").GetInt32();
var acknowledgedFailed = JsonSerializer.SerializeToElement(acknowledged).GetProperty("failed").GetInt32();
var legacyFailed = JsonSerializer.SerializeToElement(legacy).GetProperty("failed").GetInt32();
var failed = failedCases + failedSplits + identityFailed + boundaryFailed + acknowledgedFailed + legacyFailed;
File.WriteAllText(args[2],JsonSerializer.Serialize(new { sourceCases = rows.Length, masks = 64, actions = 3, codes = 41, splits, failedCases, failedSplits, identityFailed, cases, boundary, acknowledged, legacy, failed, allOwnedExecutionsJoined = true, fullNativeGate = false, physicalTerminal = false, completePackageAcceptance = false,
    scope = "Kitty CapsLock/NumLock transparency in existing single-scalar CSI-u; complete finite declared41code/64mask/3action inventory, all two-chunk schedules, boundary/rejection/resource/EOF/timeout controls and28unchanged public Source editor witnesses on both actual input routes" },new JsonSerializerOptions { WriteIndented = true })+"\n");
Console.WriteLine(JsonSerializer.Serialize(new { sourceCases = rows.Length, splits, failedCases, failedSplits, identityFailed, boundaryFailed, acknowledgedCases = 28, acknowledgedFailed, legacyCases = 28, legacyFailed, failed, allOwnedExecutionsJoined = true }));
return failed == 0 ? 0 : 1;