using System.Text.Json;

internal sealed class AssertionLedger
{
    private readonly Dictionary<string, GroupState> _groups = new(StringComparer.Ordinal);
    private readonly List<object> _attempts = [];
    private readonly Criterion[] _criteria;
    private sealed record Criterion(string Id, string Name, string PriorGroup, string[] RequiredGroups, string Scope);
    private sealed class GroupState
    {
        internal int Started, Completed, Failed, InFlight;
        internal readonly List<string> OpenReasons = [], Failures = [];
        internal string Status => Failed > 0 ? "FAILED" : OpenReasons.Count > 0 ? "OPEN" : InFlight > 0 ? "STARTED" : Completed > 0 ? "COMPLETED" : "UNEXECUTED";
        internal object Snapshot(string group) => new { group, status = Status, started = Started, completed = Completed, failed = Failed,
            inFlight = InFlight, openReasons = OpenReasons.ToArray(), failures = Failures.ToArray() };
    }
    internal AssertionLedger(JsonElement scenario, JsonElement dependencies)
    {
        var originals = scenario.GetProperty("assertions").EnumerateArray().ToDictionary(c => c.GetProperty("id").GetString()!, StringComparer.Ordinal);
        if (originals.Count != dependencies.GetArrayLength()) throw new InvalidOperationException("Criterion dependency count differs from frozen input.");
        _criteria = dependencies.EnumerateArray().Select(c =>
        {
            var id = c.GetProperty("id").GetString()!; var name = c.GetProperty("name").GetString()!;
            if (!originals.TryGetValue(id, out var original) || name != original.GetProperty("name").GetString())
                throw new InvalidOperationException("Criterion identity/name differs from frozen input.");
            var groups = c.GetProperty("requiredGroups").EnumerateArray().Select(g => g.GetString()!).ToArray();
            var scope = c.GetProperty("scope").GetString()!;
            if (groups.Length == 0 || groups.Any(string.IsNullOrEmpty) || groups.Distinct(StringComparer.Ordinal).Count() != groups.Length ||
                scope is not ("CASE" or "ALL_VARIANTS"))
                throw new InvalidOperationException("Invalid constituent dependencies or criterion scope.");
            return new Criterion(id, name, c.GetProperty("priorGroup").GetString()!, groups, scope);
        }).ToArray();
        if (_criteria.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != _criteria.Length)
            throw new InvalidOperationException("Duplicate criterion dependency identity.");
    }
    private GroupState Group(string group)
    {
        if (!_groups.TryGetValue(group, out var state)) { state = new(); _groups.Add(group, state); }
        return state;
    }
    private string GroupStatus(string group) => _groups.TryGetValue(group, out var state) ? state.Status : "UNEXECUTED";
    internal void Check(string group, Action assertion)
    {
        var state = Group(group); state.Started++; state.InFlight++;
        _attempts.Add(new { group, status = "STARTED", attempt = state.Started });
        try { assertion(); state.Completed++; _attempts.Add(new { group, status = "COMPLETED", attempt = state.Started }); }
        catch (Exception error)
        {
            state.Failed++; state.Failures.Add(error.ToString());
            _attempts.Add(new { group, status = "FAILED", attempt = state.Started, failure = error.ToString() }); throw;
        }
        finally { state.InFlight--; }
    }
    internal void Open(string group, string reason)
    {
        Group(group).OpenReasons.Add(reason); _attempts.Add(new { group, status = "OPEN", reason });
    }
    private static string DeriveStatus(IEnumerable<string> dependencyStates, bool requiredVariantsPresent = true)
    {
        var states = dependencyStates.ToArray();
        if (states.Contains("FAILED", StringComparer.Ordinal)) return "FAILED";
        if (states.Contains("OPEN", StringComparer.Ordinal)) return "OPEN";
        if (states.Length == 0 || states.All(status => status == "UNEXECUTED")) return "UNEXECUTED";
        return requiredVariantsPresent && states.All(status => status == "COMPLETED") ? "COMPLETED" : "INCOMPLETE";
    }
    internal object[] CriterionSummary(IReadOnlyList<AssertionLedger> variants, int expectedVariants)
    {
        if (expectedVariants < 1 || variants.Count > expectedVariants) throw new InvalidOperationException("Invalid criterion variant accounting.");
        return _criteria.Select(c =>
        {
            IReadOnlyList<AssertionLedger> required = c.Scope == "ALL_VARIANTS" ? variants : new[] { this };
            var count = c.Scope == "ALL_VARIANTS" ? expectedVariants : 1;
            return (object)new { id = c.Id, name = c.Name, priorGroup = c.PriorGroup, requiredGroups = c.RequiredGroups,
                scope = c.Scope, requiredVariants = count, observedVariants = required.Count,
                status = DeriveStatus(required.SelectMany(v => c.RequiredGroups.Select(v.GroupStatus)), required.Count == count),
                variantDependencies = required.Select((v, index) => new { variant = index,
                    dependencies = c.RequiredGroups.Select(group => new { group, status = v.GroupStatus(group) }).ToArray() }).ToArray() };
        }).ToArray();
    }
    internal object Snapshot(IReadOnlyList<AssertionLedger> variants, int expectedVariants) => new
    {
        attempts = _attempts.ToArray(),
        groups = _groups.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Value.Snapshot(pair.Key)).ToArray(),
        namedCriteria = CriterionSummary(variants, expectedVariants)
    };
    // OPEN is qualification evidence, not a failed behavioral assertion. Keep
    // constituent states unchanged and let the report distinguish both outcomes.
    internal string VariantDependencyStatus() =>
        _groups.Values.Any(group => group.Failed > 0) ? "FAILED" :
        DeriveStatus(_criteria.Select(criterion => DeriveStatus(criterion.RequiredGroups.Select(GroupStatus))));
}
