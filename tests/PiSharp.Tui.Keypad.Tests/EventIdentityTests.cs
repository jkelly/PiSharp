using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.Tui;
using PiSharp.Tui.Input;

internal static class EventIdentityTests
{
    internal static object Run(string sourcePath)
    {
        using var source = JsonDocument.Parse(File.ReadAllText(sourcePath));
        var observations = new List<object>();
        var failed = 0;
        foreach (var row in source.RootElement.GetProperty("cases").EnumerateArray())
        {
            var raw = row.GetProperty("raw").GetString()!;
            var decoder = new TerminalInputDecoder();
            var decoded = decoder.Feed(raw.AsSpan()).Concat(decoder.Complete()).Single() as TerminalKey
                ?? throw new InvalidOperationException("Identity fixture did not yield one key");
            var modifiers = (TerminalModifiers)row.GetProperty("modifier").GetInt32();
            var action = (TerminalKeyAction)row.GetProperty("action").GetInt32();
            var canonical = new TerminalKey("Enter", modifiers, action);
            var clone = decoded with { };
            var alteredClone = decoded with { Key = "Tab" };
            decoded.Deconstruct(out var keyName, out var decodedModifiers, out var decodedAction);
            var sourcePrintable = row.GetProperty("sourcePrintable").GetString();
            var projected = (TerminalKey)TerminalInputDecoder.ForPendingCharacterJump(decoded);
            var namedIdentity = decoded == canonical && decoded.Equals((object)canonical) &&
                ((TerminalInputEvent)decoded).Equals((TerminalInputEvent)canonical);
            var sameHash = decoded.GetHashCode() == canonical.GetHashCode();
            var sameFormatting = decoded.ToString() == canonical.ToString();
            var sameSerialization = JsonSerializer.Serialize(decoded) == JsonSerializer.Serialize(canonical);
            var sameDeconstruction = keyName == "Enter" && decodedModifiers == modifiers && decodedAction == action;
            var cloneCompatible = clone == canonical && clone.GetHashCode() == canonical.GetHashCode() && clone.ToString() == canonical.ToString();
            var constructedNoOrigin = ReferenceEquals(canonical, TerminalInputDecoder.ForPendingCharacterJump(canonical));
            var cloneNoOrigin = ReferenceEquals(clone, TerminalInputDecoder.ForPendingCharacterJump(clone)) &&
                ReferenceEquals(alteredClone, TerminalInputDecoder.ForPendingCharacterJump(alteredClone));
            var printableMatchesSource = sourcePrintable is null
                ? ReferenceEquals(projected, decoded) : projected.Key == sourcePrintable;
            var projectedMetadataPreserved = projected.Modifiers == modifiers && projected.Action == action;
            var passed = namedIdentity && sameHash && sameFormatting && sameSerialization && sameDeconstruction && cloneCompatible &&
                constructedNoOrigin && cloneNoOrigin && printableMatchesSource && projectedMetadataPreserved;
            if (!passed) failed++;
            observations.Add(new { id = row.GetProperty("id").GetString(), raw, sourcePrintable,
                native = new { decoded.Key, Modifiers = decoded.Modifiers.ToString(), Action = decoded.Action.ToString() },
                projected = new { projected.Key, Modifiers = projected.Modifiers.ToString(), Action = projected.Action.ToString() },
                namedIdentity, sameHash, sameFormatting, sameSerialization, sameDeconstruction, cloneCompatible, constructedNoOrigin,
                cloneNoOrigin, printableMatchesSource, projectedMetadataPreserved, passed });
        }
        var publicProperties = typeof(TerminalKey).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).Order().ToArray();
        var recordShapeUnchanged = publicProperties.SequenceEqual(new[] { "Action", "Key", "Modifiers" });
        if (!recordShapeUnchanged) failed++;
        var weak = WeakDecodedKey();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var weakOriginReleased = !weak.TryGetTarget(out _);
        if (!weakOriginReleased) failed++;
        var oldShape = new ProposedR1Key("Enter");
        var withAutoProperty = oldShape with { SourcePrintableTarget = "\ue046" };
        var r1AutoPropertyChangesEquality = oldShape != withAutoProperty;
        if (!r1AutoPropertyChangesEquality) failed++;
        return new { cases = observations.Count, failed, observations, recordShapeUnchanged, publicProperties,
            weakOriginReleased, r1AutoPropertyChangesEquality,
            cloneContract = "Original wire provenance is reference-bound and is not invented for constructed or cloned named keys; canonical record equality, hashes, with clones and public serialization stay unchanged." };
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<TerminalKey> WeakDecodedKey()
    {
        var decoder = new TerminalInputDecoder();
        var key = (TerminalKey)decoder.Feed("\u001b[57414u".AsSpan()).Single();
        decoder.Complete();
        return new WeakReference<TerminalKey>(key);
    }

    private sealed record ProposedR1Key(string Key, TerminalModifiers Modifiers = TerminalModifiers.None,
        TerminalKeyAction Action = TerminalKeyAction.Press)
    {
        internal string? SourcePrintableTarget { get; init; }
    }
}
