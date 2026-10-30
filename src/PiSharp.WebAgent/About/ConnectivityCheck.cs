// Decision 0011 (run W3, unit AP): Settings › About's "Check provider connectivity". It runs only when the user clicks it (POST
// /api/about/connectivity), and only through the credentials area's connection tester (IConnectionTester, WU5): each provider's active
// account (the one new chats use) is tested on a free endpoint, its models list or a sign-in check, never a completion, so a check costs
// nothing. The tests run side by side; a click while a check runs joins it. Answers are the tester's, already scrubbed (never a key or a
// token); a provider with no known test says so instead of guessing. A reply slower than a second is marked slow. The last check is kept in
// memory until the server stops ("Not checked since the server started" before the first). With --faux the app is offline: nothing is sent
// and the check says so.
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Credentials;
using PiSharp.WebAgent.Hosting;

namespace PiSharp.WebAgent.About;

/// <summary>How one provider's check went. Sent as <c>ok</c>, <c>slow</c>, <c>failed</c>, <c>notTested</c>.</summary>
public enum ConnectivityOutcome
{
    /// <summary>The provider answered and accepted the credential.</summary>
    Ok,

    /// <summary>It answered and accepted the credential, but took longer than <see cref="ConnectivityCheck.SlowAfter"/>.</summary>
    Slow,

    /// <summary>It refused the credential, failed or could not be reached.</summary>
    Failed,

    /// <summary>No test exists for the account (ambient credentials, a provider without a known free endpoint); nothing was sent.</summary>
    NotTested,
}

/// <summary>A provider the check tests: its active account.</summary>
/// <param name="Provider">The provider id.</param>
/// <param name="Name">The provider's display name.</param>
/// <param name="AccountId">The account tested.</param>
/// <param name="Kind">Where its credential comes from.</param>
/// <param name="Source">The environment variable of an environment account (a name, never its value), or null.</param>
public sealed record ConnectivityTarget(string Provider, string Name, string AccountId, AccountKind Kind, string? Source);

/// <summary>One provider's result. It never holds a secret.</summary>
/// <param name="Provider">The provider id.</param>
/// <param name="Name">The provider's display name.</param>
/// <param name="AccountId">The account tested.</param>
/// <param name="Kind">Where its credential comes from.</param>
/// <param name="Outcome">How it went.</param>
/// <param name="Detail">What to show: the request, the status and the models listed, or the tester's (scrubbed) message.</param>
public sealed record ConnectivityResult(string Provider, string Name, string AccountId, AccountKind Kind, ConnectivityOutcome Outcome, string Detail)
{
    /// <summary>The provider's HTTP status, when a request was sent.</summary>
    public int? StatusCode { get; init; }

    /// <summary>How long the test took, in milliseconds, when a request was sent.</summary>
    public long? LatencyMs { get; init; }

    /// <summary>How many models the provider listed, when it said.</summary>
    public int? ModelCount { get; init; }

    /// <summary>Whether the provider answered and accepted the credential (<see cref="ConnectivityOutcome.Ok"/> or <see cref="ConnectivityOutcome.Slow"/>).</summary>
    public bool Reachable => Outcome is ConnectivityOutcome.Ok or ConnectivityOutcome.Slow;
}

/// <summary>One run of the check.</summary>
/// <param name="CheckedAt">When it finished.</param>
/// <param name="Offline">True with <c>--faux</c>: nothing was sent.</param>
/// <param name="Message">Why nothing was tested (offline, no provider connected), or null.</param>
/// <param name="Results">One result per provider, in the catalog's order.</param>
public sealed record ConnectivityRun(DateTimeOffset CheckedAt, bool Offline, string? Message, IReadOnlyList<ConnectivityResult> Results)
{
    /// <summary>How many providers answered.</summary>
    public int Reachable => Results.Count(result => result.Reachable);

    /// <summary>How many providers were tested (a request or a sign-in check was made).</summary>
    public int Tested => Results.Count(result => result.Outcome != ConnectivityOutcome.NotTested);

    /// <summary>"7 of 8 reachable", "1 not tested" and so on.</summary>
    public string Summary
    {
        get
        {
            if (Offline || Results.Count == 0) return Message ?? "Nothing to check.";
            var text = $"{Reachable} of {Tested} reachable";
            var untested = Results.Count - Tested;
            return untested > 0 ? $"{text} · {untested} not tested" : text;
        }
    }
}

/// <summary>The provider connectivity check. A singleton; one check runs at a time.</summary>
public sealed class ConnectivityCheck(IAccountStore accounts, IConnectionTester tester, IModelCatalog catalog, WebAgentOptions options,
    CredentialsOptions? credentials = null, TimeProvider? time = null)
{
    /// <summary>The answer with <c>--faux</c>.</summary>
    public const string OfflineMessage = "The app is running offline (--faux): no provider is checked, so nothing was sent.";

    /// <summary>The answer when no provider is connected.</summary>
    public const string NothingConnectedMessage = "No provider is connected yet, so there is nothing to check.";

    /// <summary>A reply slower than this is marked slow.</summary>
    public static readonly TimeSpan SlowAfter = TimeSpan.FromSeconds(1);

    /// <summary>How many providers are tested at the same time.</summary>
    public const int Parallelism = 4;

    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private readonly Lock gate = new();
    private Task<ConnectivityRun>? running;
    private ConnectivityRun? last;

    /// <summary>The last check since the server started, or null.</summary>
    public ConnectivityRun? Last
    {
        get { lock (gate) return last; }
    }

    /// <summary>Whether the app is offline (<c>--faux</c>), so a check sends nothing.</summary>
    public bool Offline => options.Faux || credentials?.Offline == true;

    /// <summary>What a check would test now: each provider's active account, in the catalog's order (other providers after, by id).</summary>
    public IReadOnlyList<ConnectivityTarget> Targets()
    {
        var order = catalog.Providers.Select((provider, index) => (provider.Id, index)).ToDictionary(pair => pair.Id, pair => pair.index, StringComparer.Ordinal);
        return [.. accounts.List().Select(account => account.Provider).Distinct(StringComparer.Ordinal)
            .Select(provider => accounts.Active(provider))
            .OfType<AccountInfo>()
            .OrderBy(account => order.GetValueOrDefault(account.Provider, int.MaxValue)).ThenBy(account => account.Provider, StringComparer.Ordinal)
            .Select(account => new ConnectivityTarget(account.Provider, catalog.FindProvider(account.Provider)?.DisplayName ?? account.Provider, account.Id,
                account.Kind, account.Kind == AccountKind.Environment ? account.Source : null))];
    }

    /// <summary>Runs the check (or joins the one running) and keeps it as <see cref="Last"/>. With <c>--faux</c> nothing is sent.</summary>
    public Task<ConnectivityRun> RunAsync(CancellationToken cancellationToken = default)
    {
        Task<ConnectivityRun> run;
        lock (gate)
        {
            // The run belongs to every caller that joined it, so one caller giving up never cancels it; each test has its own deadline.
            run = running ??= Task.Run(CheckAsync, CancellationToken.None);
        }
        return run.WaitAsync(cancellationToken);
    }

    private async Task<ConnectivityRun> CheckAsync()
    {
        try
        {
            ConnectivityRun result;
            if (Offline)
            {
                result = new ConnectivityRun(clock.GetUtcNow(), true, OfflineMessage, []);
            }
            else
            {
                var targets = Targets();
                using var slots = new SemaphoreSlim(Parallelism, Parallelism);
                var results = await Task.WhenAll(targets.Select(async target =>
                {
                    await slots.WaitAsync().ConfigureAwait(false);
                    try { return await TestAsync(target).ConfigureAwait(false); }
                    finally { slots.Release(); }
                })).ConfigureAwait(false);
                result = new ConnectivityRun(clock.GetUtcNow(), false, targets.Count == 0 ? NothingConnectedMessage : null, results);
            }
            lock (gate) last = result;
            return result;
        }
        finally
        {
            lock (gate) running = null;
        }
    }

    private async Task<ConnectivityResult> TestAsync(ConnectivityTarget target)
    {
        ConnectionTestResult test;
        try
        {
            test = await tester.TestAccountAsync(target.AccountId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            return new(target.Provider, target.Name, target.AccountId, target.Kind, ConnectivityOutcome.Failed, "The account was removed during the check.");
        }
        catch (Exception error) when (error is HttpRequestException or InvalidOperationException or IOException)
        {
            return new(target.Provider, target.Name, target.AccountId, target.Kind, ConnectivityOutcome.Failed,
                SecretText.Scrub("The check failed: " + error.Message));
        }
        var outcome = !test.Supported ? ConnectivityOutcome.NotTested
            : !test.Ok ? ConnectivityOutcome.Failed
            : test.LatencyMs is { } latency && latency > SlowAfter.TotalMilliseconds ? ConnectivityOutcome.Slow
            : ConnectivityOutcome.Ok;
        return new(target.Provider, target.Name, target.AccountId, target.Kind, outcome, Describe(test, target, outcome))
        {
            StatusCode = test.StatusCode,
            LatencyMs = test.LatencyMs,
            ModelCount = test.ModelCount,
        };
    }

    /// <summary>The detail line of a result: <c>GET api.openai.com/v1/models · 200 · 17 models · key from OPENAI_API_KEY</c>, or the
    /// tester's message.</summary>
    public static string Describe(ConnectionTestResult test, ConnectivityTarget target, ConnectivityOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(test);
        ArgumentNullException.ThrowIfNull(target);
        if (outcome is not (ConnectivityOutcome.Ok or ConnectivityOutcome.Slow) || test.Endpoint is null) return test.Message;
        var parts = new List<string> { Endpoint(test.Endpoint) };
        if (test.StatusCode is { } status) parts.Add(status.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (test.ModelCount is { } count) parts.Add(count == 1 ? "1 model" : count.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " models");
        if (target.Source is { Length: > 0 } variable) parts.Add("key from " + variable);
        var text = string.Join(" · ", parts);
        return outcome == ConnectivityOutcome.Slow ? "Slow: " + text : text;
    }

    // "GET https://api.openai.com/v1/models" -> "GET api.openai.com/v1/models".
    private static string Endpoint(string endpoint)
    {
        var space = endpoint.IndexOf(' ', StringComparison.Ordinal);
        if (space < 0 || !Uri.TryCreate(endpoint[(space + 1)..], UriKind.Absolute, out var uri)) return endpoint;
        return endpoint[..space] + " " + uri.Authority + uri.AbsolutePath;
    }
}
