using PiSharp.Chord.Registries;

namespace PiSharp.Chord.Hosting.Http;

public sealed class HttpRouter
{
    internal sealed record Route(string Plugin, string Pattern, HttpRoute Value, Pattern Path);
    internal sealed record Socket(string Plugin, string Pattern, HttpSocket Value, Pattern Path, int MaxPayload);
    internal IReadOnlyList<Route> Routes { get; }
    internal IReadOnlyList<Socket> Sockets { get; }
    internal IReadOnlyList<(string Plugin, HttpWrapper Value)> Wraps { get; }
    internal IReadOnlyList<(string Plugin, HttpRelay Value)> Relays { get; }
    internal HashSet<string> Hosts { get; }
    internal HashSet<string> Framers { get; }
    internal IReadOnlyDictionary<string, IReadOnlyList<string>> Csp { get; }
    internal IReadOnlyList<object?> CallbackOwners { get; }
    private HttpRouter(List<Route> routes, List<Socket> sockets, List<(string, HttpWrapper)> wraps, List<(string, HttpRelay)> relays, HashSet<string> hosts, HashSet<string> framers, Dictionary<string, IReadOnlyList<string>> csp, IReadOnlyList<object?> owners)
    { Routes = routes; Sockets = sockets; Wraps = wraps; Relays = relays; Hosts = hosts; Framers = framers; Csp = csp; CallbackOwners = owners; }
    internal static HttpRouter Compose(IReadOnlyList<RegistryEntry<HttpContribution>> table, ComposeContext context)
    {
        var routes = new List<Route>(); var sockets = new List<Socket>(); var wraps = new List<(string, HttpWrapper)>(); var relays = new List<(string, HttpRelay)>();
        var hosts = new HashSet<string>(StringComparer.Ordinal); var framers = new HashSet<string>(StringComparer.Ordinal); var csp = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var entry in table.OrderBy(x => x.Plugin, StringComparer.Ordinal))
        {
            var contribution = entry.Value;
            foreach (var (key, route) in contribution.Routes)
            {
                var parts = key.Split(' ', 2); if (parts.Length != 2 || parts[0] is "TRACE" or "TRACK" or "CONNECT" || !parts[0].All(char.IsAsciiLetterUpper)) { context.Report(new($"invalid route {key}", [entry.Plugin])); continue; }
                if (routes.FirstOrDefault(x => x.Pattern == key) is { } previous) { context.Report(new($"duplicate route {key}", [previous.Plugin, entry.Plugin])); continue; }
                try { routes.Add(new(entry.Plugin, key, route, new(parts[1]))); } catch (ArgumentException) { context.Report(new($"invalid route {key}", [entry.Plugin])); }
            }
            foreach (var (key, socket) in contribution.Sockets)
            {
                if (sockets.FirstOrDefault(x => x.Pattern == key) is { } previous) { context.Report(new($"duplicate socket {key}", [previous.Plugin, entry.Plugin])); continue; }
                try { sockets.Add(new(entry.Plugin, key, socket, new(key), contribution.MaxPayload)); } catch (ArgumentException) { context.Report(new($"invalid socket {key}", [entry.Plugin])); }
            }
            wraps.AddRange(contribution.Wraps.Select(x => (entry.Plugin, x))); relays.AddRange(contribution.Relays.Select(x => (entry.Plugin, x)));
            foreach (var host in contribution.Hosts) if (!host.Any(x => x is '/' or '@' or '\r' or '\n' or '\0')) hosts.Add(host);
            foreach (var framer in contribution.Framers) if (IsOrigin(framer)) framers.Add(framer);
            foreach (var (key, values) in contribution.Csp)
                if (key.All(x => char.IsAsciiLetter(x) || x == '-') && values.All(x => !x.Any(c => c is ';' or '\r' or '\n' or '\0'))) csp[key] = csp.GetValueOrDefault(key, []).Concat(values).Distinct().ToArray();
        }
        var phases = new[] { "observe", "headers", "transform" };
        wraps = wraps.Where(x => phases.Contains(x.Item2.Phase)).OrderBy(x => Array.IndexOf(phases, x.Item2.Phase)).ToList();
        return new(routes, sockets, wraps, relays, hosts, framers, csp, table.Select(entry => entry.CallbackOwner).ToArray());
    }
    internal (Route? Route, IReadOnlyDictionary<string, string> Parameters) Match(string method, string path)
    {
        var candidates = Routes.Where(x => x.Pattern.StartsWith(method + " ", StringComparison.Ordinal)).OrderByDescending(x => x.Path.Rank, StringComparer.Ordinal);
        foreach (var candidate in candidates) if (candidate.Path.Match(path) is { } args) return (candidate, args);
        if (method == "HEAD") return Match("GET", path);
        return (null, new Dictionary<string, string>());
    }
    internal static bool IsOrigin(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" && uri.GetLeftPart(UriPartial.Authority) == value && uri.UserInfo.Length == 0;

    internal sealed class Pattern
    {
        private readonly string[] segments;
        internal string Rank { get; }
        internal Pattern(string path)
        {
            if (!path.StartsWith('/') || path.Contains('?') || path.Contains('#')) throw new ArgumentException("path must be absolute");
            segments = path.Split('/').Skip(1).ToArray();
            if (segments.Take(segments.Length - 1).Contains("*") || segments.Where(x => x.StartsWith(':')).Any(x => x.Length == 1)) throw new ArgumentException("invalid pattern");
            if (segments.Where(x => x.StartsWith(':')).Distinct().Count() != segments.Count(x => x.StartsWith(':'))) throw new ArgumentException("duplicate parameter");
            // End outranks a trailing wildcard that also matches the empty remainder.
            Rank = string.Concat(segments.Select(x => x == "*" ? '0' : x.StartsWith(':') ? '2' : '3')) + "1";
        }
        internal IReadOnlyDictionary<string, string>? Match(string path)
        {
            var actual = path.Split('/').Skip(1).ToArray(); var args = new Dictionary<string, string>();
            for (var index = 0; index < segments.Length; index++)
            {
                var part = segments[index];
                if (part == "*") { args["*"] = string.Join('/', actual.Skip(index).Select(Decode)); return args; }
                if (index >= actual.Length) return null;
                if (part.StartsWith(':')) { if (actual[index].Length == 0) return null; args[part[1..]] = Decode(actual[index]); }
                else if (!string.Equals(part, actual[index], StringComparison.Ordinal)) return null;
            }
            return actual.Length == segments.Length ? args : null;
        }
        private static string Decode(string text)
        {
            var decoded = new System.Text.StringBuilder();
            for (var index = 0; index < text.Length;)
            {
                if (text[index] != '%') { decoded.Append(text[index++]); continue; }
                var bytes = new List<byte>();
                while (index < text.Length && text[index] == '%')
                {
                    if (index + 2 >= text.Length || !byte.TryParse(text.AsSpan(index + 1, 2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var octet)) throw new HttpFailure(400);
                    bytes.Add(octet); index += 3;
                }
                try { decoded.Append(new System.Text.UTF8Encoding(false, true).GetString(bytes.ToArray())); }
                catch (System.Text.DecoderFallbackException) { throw new HttpFailure(400); }
            }
            var value = decoded.ToString();
            if (value is "." or ".." || value.Any(x => x is '/' or '\\' or '\0')) throw new HttpFailure(400);
            return value;
        }
    }
}
