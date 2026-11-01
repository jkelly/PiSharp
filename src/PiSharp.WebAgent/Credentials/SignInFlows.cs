// Decision 0011 (run W2, WU5): subscription sign-ins from the browser, on the server. PiSharp.AI's own flows do the protocol work
// (OpenAICodexOAuth: ChatGPT with PKCE through the loopback callback on port 1455, or OpenAI's device code; GitHubCopilotOAuth: GitHub's
// device code, the Copilot token exchange and model enabling); this class answers their prompts for the page and tracks each flow so the
// page can poll it. Starting a flow returns the authorize URL (browser) or the user code and verification URL (device code); the PKCE
// verifier, the codes and every token stay on the server. When port 1455 is taken (the Codex CLI, another sign-in) the browser cannot call
// back, so the page asks the user to paste the redirect URL, which SubmitCodeAsync hands to the flow. A completed sign-in is stored in the
// vault: into the account being signed in again (refused, with nothing changed, when it is another identity), else into the provider's
// account with the same identity, else as a new account. Only openai-codex and github-copilot sign in here: Anthropic is API key or
// Amazon Bedrock only, and nothing starts in offline (--faux) mode. One flow runs per provider; starting another cancels it. A flow
// that waits longer than SignInTimeout is given up. Messages are scrubbed of tokens and response bodies before a page or a log sees them.
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.AI.Authentication.OAuth;
using PiSharp.WebAgent.Contracts;

namespace PiSharp.WebAgent.Credentials;

/// <summary>How a sign-in proves the user. Sent as <c>browser</c> and <c>deviceCode</c>.</summary>
public enum SignInMethod
{
    /// <summary>The provider's page in the browser, which calls back to the loopback port (or the user pastes the redirect URL).</summary>
    [JsonStringEnumMemberName("browser")] Browser,

    /// <summary>A code the user enters on the provider's verification page while the server polls.</summary>
    [JsonStringEnumMemberName("deviceCode")] DeviceCode,
}

/// <summary>Where a sign-in stands. Sent as <c>starting</c>, <c>waiting</c>, <c>working</c>, <c>succeeded</c>, <c>failed</c>, <c>cancelled</c>.</summary>
public enum SignInState
{
    /// <summary>Asking the provider for the authorize URL or the device code.</summary>
    [JsonStringEnumMemberName("starting")] Starting,

    /// <summary>Waiting for the user: to finish in the browser (or paste the redirect URL), or to enter the device code.</summary>
    [JsonStringEnumMemberName("waiting")] Waiting,

    /// <summary>The user is done; the tokens are being fetched and stored.</summary>
    [JsonStringEnumMemberName("working")] Working,

    /// <summary>Signed in and stored (<see cref="SignInStatus.Account"/>).</summary>
    [JsonStringEnumMemberName("succeeded")] Succeeded,

    /// <summary>It failed (<see cref="SignInStatus.Message"/>); nothing was stored.</summary>
    [JsonStringEnumMemberName("failed")] Failed,

    /// <summary>The user cancelled it, another sign-in of the provider replaced it, or it timed out; nothing was stored.</summary>
    [JsonStringEnumMemberName("cancelled")] Cancelled,
}

/// <summary>Starts a sign-in.</summary>
/// <param name="Provider"><c>openai-codex</c> or <c>github-copilot</c>.</param>
/// <param name="Method">How the user signs in (github-copilot has only <see cref="SignInMethod.DeviceCode"/>).</param>
public sealed record SignInRequest(string Provider, SignInMethod Method)
{
    /// <summary>The account to sign in again (its credential is replaced, its id and chats stay); null to add an account (or reconnect the
    /// one with the same identity).</summary>
    public string? AccountId { get; init; }

    /// <summary>The new account's name; default from the sign-in (<c>ChatGPT Plus</c>, <c>GitHub Copilot</c>).</summary>
    public string? Label { get; init; }

    /// <summary>A GitHub Enterprise domain for github-copilot (blank for github.com).</summary>
    public string? Domain { get; init; }
}

/// <summary>A sign-in as a page sees it: no token, verifier or authorization code, only what the user needs to act.</summary>
public sealed record SignInStatus
{
    /// <summary>The flow's id, for polling, pasting and cancelling.</summary>
    public required string FlowId { get; init; }

    /// <summary>The provider.</summary>
    public required string Provider { get; init; }

    /// <summary>The method.</summary>
    public SignInMethod Method { get; init; }

    /// <summary>Where it stands.</summary>
    public SignInState State { get; init; }

    /// <summary>Progress or the failure, scrubbed (never a token or a response body).</summary>
    public string? Message { get; init; }

    /// <summary>The provider's authorize URL to open in a new tab (browser method).</summary>
    public string? AuthorizeUrl { get; init; }

    /// <summary>The redirect URL the provider calls back (<c>http://localhost:1455/auth/callback</c>), so the page can say what to paste.</summary>
    public string? CallbackUrl { get; init; }

    /// <summary>Whether the app listens for the browser's callback; false when the port was taken, so the user pastes the redirect URL.</summary>
    public bool CallbackListening { get; init; }

    /// <summary>Whether <see cref="ISignInFlows.SubmitCodeAsync"/> accepts a pasted code or redirect URL now.</summary>
    public bool AcceptsCode { get; init; }

    /// <summary>The code to enter on the verification page (device code method).</summary>
    public string? UserCode { get; init; }

    /// <summary>The provider's verification page (device code method).</summary>
    public string? VerificationUrl { get; init; }

    /// <summary>How often the server checks whether the device code was entered, in seconds.</summary>
    public double? PollSeconds { get; init; }

    /// <summary>When the flow started.</summary>
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>When the code or the flow expires.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>The account that was signed in (succeeded), or the one being signed in again.</summary>
    public string? AccountId { get; init; }

    /// <summary>The account as stored, once it succeeded.</summary>
    public AccountInfo? Account { get; init; }

    /// <summary>Whether an existing account was signed in again (its id and chats are kept).</summary>
    public bool Reauthenticated { get; init; }

    /// <summary>Whether the account became its provider's active account.</summary>
    public bool BecameActive { get; init; }

    /// <summary>Whether the flow is over (succeeded, failed or cancelled).</summary>
    public bool Finished => State is SignInState.Succeeded or SignInState.Failed or SignInState.Cancelled;
}

/// <summary>A sign-in method the app offers.</summary>
/// <param name="Provider">The provider.</param>
/// <param name="Method">The method.</param>
/// <param name="Label">The button's text.</param>
/// <param name="Description">One line on when to use it.</param>
/// <param name="Available">False offline (<c>--faux</c>).</param>
public sealed record SignInMethodInfo(string Provider, SignInMethod Method, string Label, string Description, bool Available);

/// <summary>Subscription sign-ins, run on the server. A singleton.</summary>
public interface ISignInFlows
{
    /// <summary>The sign-in methods offered, by provider.</summary>
    IReadOnlyList<SignInMethodInfo> Methods { get; }

    /// <summary>Starts a sign-in (cancelling the provider's running one) and answers once the authorize URL or the device code is known,
    /// the flow failed, or <see cref="CredentialsOptions.SignInStartWait"/> passed.</summary>
    /// <exception cref="ArgumentException">An unknown provider or method, or an invalid domain.</exception>
    /// <exception cref="InvalidOperationException">Offline, a provider that does not sign in here (Anthropic), or an account that is not a
    /// sign-in of the provider.</exception>
    /// <exception cref="KeyNotFoundException">The account to sign in again does not exist.</exception>
    Task<SignInStatus> StartAsync(SignInRequest request, CancellationToken cancellationToken = default);

    /// <summary>The flow <paramref name="flowId"/>, or null (unknown, or finished more than ten minutes ago).</summary>
    SignInStatus? Get(string flowId);

    /// <summary>Hands a pasted authorization code or redirect URL to a browser sign-in, then answers once the sign-in settles (or after a
    /// short wait, as <see cref="SignInState.Working"/>).</summary>
    /// <exception cref="KeyNotFoundException">No such flow.</exception>
    /// <exception cref="InvalidOperationException">The flow does not take a code now.</exception>
    /// <exception cref="ArgumentException">An empty or malformed input.</exception>
    Task<SignInStatus> SubmitCodeAsync(string flowId, string input, CancellationToken cancellationToken = default);

    /// <summary>Cancels a running flow; nothing is stored. Answers the flow as it stands, or null when there is none.</summary>
    SignInStatus? Cancel(string flowId);
}

/// <summary>The sign-in flows of openai-codex (ChatGPT) and github-copilot.</summary>
public sealed class SignInFlows : ISignInFlows, IAsyncDisposable
{
    /// <summary>The ChatGPT (Codex) provider.</summary>
    public const string CodexProvider = "openai-codex";

    /// <summary>The GitHub Copilot provider.</summary>
    public const string CopilotProvider = "github-copilot";

    /// <summary>The longest pasted code or redirect URL accepted.</summary>
    public const int MaximumCodeLength = 4096;

    private const string AnthropicRefusal =
        "Anthropic connects with an API key (or through Amazon Bedrock); a Claude subscription sign-in is not offered.";

    private static readonly TimeSpan FinishedKept = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SubmitWait = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReplaceWait = TimeSpan.FromSeconds(10);

    private readonly CredentialVault vault;
    private readonly CredentialsOptions options;
    private readonly IServiceProvider? services;
    private readonly ILogger log;
    private readonly ConcurrentDictionary<string, Flow> flows = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim starts = new(1, 1);

    /// <summary>The flows over <paramref name="vault"/>; <paramref name="services"/> resolves the model catalog when GitHub Copilot asks which
    /// models to enable.</summary>
    public SignInFlows(CredentialVault vault, CredentialsOptions options, IServiceProvider? services = null, ILogger<SignInFlows>? logger = null)
    {
        this.vault = vault ?? throw new ArgumentNullException(nameof(vault));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.services = services;
        log = logger ?? (ILogger)NullLogger.Instance;
    }

    /// <inheritdoc/>
    public IReadOnlyList<SignInMethodInfo> Methods =>
    [
        new(CodexProvider, SignInMethod.Browser, "Sign in with ChatGPT", "Opens ChatGPT in a new tab; uses your ChatGPT Plus or Pro plan.", !options.Offline),
        new(CodexProvider, SignInMethod.DeviceCode, "Sign in with a device code", "For a browser on another device: enter a code at OpenAI.", !options.Offline),
        new(CopilotProvider, SignInMethod.DeviceCode, "Sign in with GitHub", "Enter a code at GitHub; uses your GitHub Copilot plan.", !options.Offline),
    ];

    /// <summary>The PiSharp.AI flow of <paramref name="provider"/> (also used to refresh a stored sign-in).</summary>
    /// <exception cref="InvalidOperationException">The provider does not sign in here.</exception>
    internal static IProviderOAuth CreateFlow(string provider, HttpMessageInvoker http, CredentialsOptions options, Func<string, bool>? isCatalogModel = null) =>
        provider switch
        {
            CodexProvider => new OpenAICodexOAuth(http, options.Environment, options.Time, options.OpenAIAuthBaseUrl, options.CodexCallbackPort),
            CopilotProvider => new GitHubCopilotOAuth(http, isCatalogModel, options.Time),
            "anthropic" => throw new InvalidOperationException(AnthropicRefusal),
            _ => throw new InvalidOperationException($"{provider} does not sign in here."),
        };

    /// <inheritdoc/>
    public async Task<SignInStatus> StartAsync(SignInRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var provider = request.Provider?.Trim() ?? "";
        if (provider == "anthropic") throw new InvalidOperationException(AnthropicRefusal);
        if (provider is not (CodexProvider or CopilotProvider)) throw new ArgumentException($"“{provider}” does not sign in here; use an API key.");
        if (!Enum.IsDefined(request.Method)) throw new ArgumentException("Choose a sign-in method.");
        if (provider == CopilotProvider && request.Method != SignInMethod.DeviceCode)
            throw new ArgumentException("GitHub Copilot signs in with a device code.");
        string? domain = null;
        if (provider == CopilotProvider && !string.IsNullOrWhiteSpace(request.Domain))
            domain = GitHubCopilotOAuth.NormalizeDomain(request.Domain) ?? throw new ArgumentException("That is not a GitHub Enterprise domain.");
        else if (!string.IsNullOrWhiteSpace(request.Domain)) throw new ArgumentException("Only GitHub Copilot takes an Enterprise domain.");
        var label = string.IsNullOrWhiteSpace(request.Label) ? null : request.Label.Trim();
        if (label is not null && (label.Length > CredentialVault.MaximumLabelLength || label.Any(char.IsControl)))
            throw new ArgumentException($"Enter a name of at most {CredentialVault.MaximumLabelLength} characters.");
        if (request.AccountId is { } accountId)
        {
            var account = vault.Get(accountId) ?? throw new KeyNotFoundException($"No account {accountId}.");
            if (account.Provider != provider || account.Kind != AccountKind.OAuth)
                throw new InvalidOperationException($"“{account.Label}” is not a {provider} sign-in.");
        }
        if (options.Offline) throw new InvalidOperationException(CredentialsOptions.OfflineMessage);
        Prune();

        Flow flow;
        await starts.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // One flow per provider: the newer one wins, and the older one lets go of the callback port before the newer one binds it.
            foreach (var running in flows.Values.Where(other => other.Provider == provider && !other.Snapshot().Finished).ToList())
            {
                running.Stop(SignInState.Cancelled, "Replaced by a newer sign-in.");
                await Task.WhenAny(running.Completion, Task.Delay(ReplaceWait, CancellationToken.None)).ConfigureAwait(false);
            }
            var listening = request.Method == SignInMethod.Browser && provider == CodexProvider && PortIsFree();
            flow = new Flow(this, provider, request.Method, request.AccountId, label, domain, listening);
            flows[flow.Id] = flow;
            flow.Begin();
        }
        finally { starts.Release(); }
        log.LogInformation("Sign-in {Flow} for {Provider} started ({Method}).", flow.Id, provider, request.Method);
        await Task.WhenAny(flow.Ready, Task.Delay(options.SignInStartWait, cancellationToken)).ConfigureAwait(false);
        return flow.Snapshot();
    }

    /// <inheritdoc/>
    public SignInStatus? Get(string flowId)
    {
        Prune();
        return flowId is not null && flows.TryGetValue(flowId, out var flow) ? flow.Snapshot() : null;
    }

    /// <inheritdoc/>
    public async Task<SignInStatus> SubmitCodeAsync(string flowId, string input, CancellationToken cancellationToken = default)
    {
        if (flowId is null || !flows.TryGetValue(flowId, out var flow)) throw new KeyNotFoundException($"No sign-in {flowId}.");
        var code = input?.Trim() ?? "";
        if (code.Length == 0) throw new ArgumentException("Paste the authorization code or the redirect URL.");
        if (code.Length > MaximumCodeLength || code.Any(char.IsControl)) throw new ArgumentException("That is not an authorization code or a redirect URL.");
        flow.Submit(code);
        await Task.WhenAny(flow.Completion, Task.Delay(SubmitWait, cancellationToken)).ConfigureAwait(false);
        return flow.Snapshot();
    }

    /// <inheritdoc/>
    public SignInStatus? Cancel(string flowId)
    {
        if (flowId is null || !flows.TryGetValue(flowId, out var flow)) return null;
        flow.Stop(SignInState.Cancelled, "Cancelled; nothing was stored.");
        return flow.Snapshot();
    }

    /// <summary>Cancels every running flow and waits for them to let go of their ports.</summary>
    public async ValueTask DisposeAsync()
    {
        var running = flows.Values.ToList();
        foreach (var flow in running) flow.Stop(SignInState.Cancelled, "The app is stopping.");
        await Task.WhenAny(Task.WhenAll(running.Select(flow => flow.Completion)), Task.Delay(ReplaceWait)).ConfigureAwait(false);
        starts.Dispose();
    }

    private void Prune()
    {
        var now = options.Time.GetUtcNow();
        foreach (var (id, flow) in flows)
            if (flow.FinishedAt is { } at && now - at > FinishedKept) flows.TryRemove(id, out _);
    }

    /// <summary>Whether the ChatGPT callback port is free now (the flow binds it a moment later; a busy port means the user pastes).</summary>
    private bool PortIsFree()
    {
        try
        {
            var host = OAuthFlows.CallbackHost(options.Environment);
            var address = IPAddress.TryParse(host.Trim('[', ']'), out var parsed) ? parsed : IPAddress.Loopback;
            var probe = new TcpListener(address, options.CodexCallbackPort) { ExclusiveAddressUse = true };
            probe.Start();
            probe.Stop();
            return true;
        }
        catch (SocketException) { return false; }
    }

    /// <summary>The identity, details and default label of a completed sign-in. None of them is a secret.</summary>
    internal static (string? Identity, Dictionary<string, string> Details, string Label) Describe(string provider, OAuthCredentialSnapshot credential)
    {
        var details = new Dictionary<string, string>(StringComparer.Ordinal);
        if (provider == CodexProvider)
        {
            var payload = OAuthFlows.JwtPayload(credential.Access);
            var plan = Claim(payload, "https://api.openai.com/auth", "chatgpt_plan_type");
            var email = Claim(payload, "https://api.openai.com/profile", "email") ?? Text(payload?["email"]);
            if (plan is not null) details["plan"] = plan;
            if (email is not null) details["email"] = email;
            var label = plan is null ? "ChatGPT" : "ChatGPT " + char.ToUpperInvariant(plan[0]) + plan[1..];
            return (credential.ProviderData.GetValueOrDefault("accountId"), details, label);
        }
        var host = credential.ProviderData.GetValueOrDefault("enterpriseUrl") is { Length: > 0 } enterprise ? enterprise : "github.com";
        details["host"] = host;
        if (GitHubCopilotOAuth.AvailableModels(credential) is { } models) details["models"] = models.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return (null, details, host == "github.com" ? "GitHub Copilot" : $"GitHub Copilot ({host})");
    }

    private static string? Claim(JsonObject? payload, string claim, string name) => payload?[claim] is JsonObject group ? Text(group[name]) : null;

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0 && text.Length <= 256 ? text : null;

    private bool IsCatalogModel(string modelId)
    {
        try { return services?.GetService(typeof(IModelCatalog)) is IModelCatalog catalog && catalog.FindModel(CopilotProvider, modelId) is not null; }
        catch (Exception) { return false; }
    }

    /// <summary>One running sign-in, and the page's side of the flow's prompts.</summary>
    private sealed class Flow : IProviderAuthInteraction
    {
        private readonly SignInFlows owner;
        private readonly Lock gate = new();
        private readonly CancellationTokenSource stop = new();
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly string? domain;
        private SignInStatus status;
        private TaskCompletionSource<string>? manual;
        private string? pendingCode;
        private SignInState? stopState;
        private string? stopMessage;

        public Flow(SignInFlows owner, string provider, SignInMethod method, string? accountId, string? label, string? domain, bool listening)
        {
            this.owner = owner;
            this.domain = domain;
            Label = label;
            Id = "sgn-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));
            Provider = provider;
            var now = owner.options.Time.GetUtcNow();
            status = new SignInStatus
            {
                FlowId = Id, Provider = provider, Method = method, State = SignInState.Starting, AccountId = accountId, StartedAt = now,
                ExpiresAt = now + owner.options.SignInTimeout, CallbackListening = listening,
                CallbackUrl = method == SignInMethod.Browser ? OpenAICodexOAuth.RedirectUri : null,
                Message = method == SignInMethod.Browser ? "Preparing the sign-in page…" : "Asking for a device code…",
            };
        }

        public string Id { get; }

        public string Provider { get; }

        public string? Label { get; }

        public Task Ready => ready.Task;

        public Task Completion => done.Task;

        public DateTimeOffset? FinishedAt { get; private set; }

        public SignInStatus Snapshot()
        {
            lock (gate) return status;
        }

        public void Begin()
        {
            stop.CancelAfter(owner.options.SignInTimeout);
            _ = Task.Run(RunAsync);
        }

        public void Stop(SignInState state, string message)
        {
            lock (gate)
            {
                if (status.Finished || stopState is not null) return;
                stopState = state;
                stopMessage = message;
            }
            stop.Cancel();
        }

        public void Submit(string code)
        {
            TaskCompletionSource<string>? prompt;
            lock (gate)
            {
                if (!status.AcceptsCode || status.Finished) throw new InvalidOperationException(status.Finished
                    ? "This sign-in is over; start another." : "This sign-in does not take a pasted code.");
                status = status with { State = SignInState.Working, AcceptsCode = false, Message = "Checking the code…" };
                prompt = manual;
                if (prompt is null) pendingCode = code;
                manual = null;
            }
            prompt?.TrySetResult(code);
        }

        // ---- IProviderAuthInteraction ----

        public Task<string> PromptAsync(AuthPrompt prompt, CancellationToken cancellationToken)
        {
            switch (prompt.Kind)
            {
                case AuthPromptKind.Select:
                    var answer = Snapshot().Method == SignInMethod.Browser ? "browser" : "device_code";
                    return prompt.Options?.Any(option => option.Id == answer) == true
                        ? Task.FromResult(answer) : Task.FromException<string>(new InvalidOperationException("That sign-in method is not offered."));
                case AuthPromptKind.Text:
                    return Task.FromResult(domain ?? "");
                case AuthPromptKind.ManualCode:
                    var source = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                    lock (gate)
                    {
                        if (pendingCode is { } code)
                        {
                            pendingCode = null;
                            return Task.FromResult(code);
                        }
                        manual = source;
                        if (status.State == SignInState.Waiting) status = status with { AcceptsCode = true };
                    }
                    cancellationToken.Register(() =>
                    {
                        lock (gate)
                        {
                            if (manual == source) manual = null;
                            status = status with { AcceptsCode = false };
                        }
                        source.TrySetCanceled(cancellationToken);
                    });
                    return source.Task;
                default:
                    return Task.FromException<string>(new InvalidOperationException("This sign-in asks for something the app cannot answer."));
            }
        }

        public void Notify(AuthEvent authEvent)
        {
            var now = owner.options.Time.GetUtcNow();
            lock (gate)
            {
                if (status.Finished) return;
                switch (authEvent.Kind)
                {
                    case AuthEventKind.AuthUrl when OAuthFlows.TrustedHttpUrl(authEvent.Url, httpsOnly: !IsLoopback(authEvent.Url)) is { } url:
                        status = status with
                        {
                            // The flow asks for a pasted code right after this; a paste that comes first is held until it does.
                            State = SignInState.Waiting, AuthorizeUrl = url, AcceptsCode = true,
                            Message = status.CallbackListening
                                ? "Finish signing in on the ChatGPT tab; this page updates by itself."
                                : $"Port {owner.options.CodexCallbackPort} is in use (the Codex CLI or another sign-in), so the browser cannot call back. " +
                                  "After signing in, copy the address of the page that fails to load and paste it here.",
                        };
                        ready.TrySetResult();
                        break;
                    case AuthEventKind.DeviceCode when OAuthFlows.TrustedHttpUrl(authEvent.VerificationUri) is { } verification:
                        status = status with
                        {
                            State = SignInState.Waiting, UserCode = authEvent.UserCode, VerificationUrl = verification,
                            PollSeconds = authEvent.IntervalSeconds,
                            ExpiresAt = authEvent.ExpiresInSeconds is { } seconds && double.IsFinite(seconds) && seconds > 0
                                ? Min(now + TimeSpan.FromSeconds(seconds), status.ExpiresAt) : status.ExpiresAt,
                            Message = "Enter the code on the verification page; this page updates by itself.",
                        };
                        ready.TrySetResult();
                        break;
                    case AuthEventKind.Progress when authEvent.Message is { Length: > 0 } message:
                        status = status with { State = SignInState.Working, AcceptsCode = false, Message = SecretText.Scrub(message) };
                        break;
                }
            }
        }

        private static bool IsLoopback(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsLoopback;

        private static DateTimeOffset? Min(DateTimeOffset value, DateTimeOffset? other) => other is { } limit && limit < value ? limit : value;

        private async Task RunAsync()
        {
            using var http = owner.options.CreateHttp();
            OAuthCredentialSnapshot? credential = null;
            try
            {
                var oauth = CreateFlow(Provider, http, owner.options, owner.IsCatalogModel);
                credential = await oauth.LoginAsync(this, new ProviderLoginOptions(AgentName: "pi"), stop.Token).ConfigureAwait(false);
                lock (gate) status = status with { State = SignInState.Working, AcceptsCode = false, Message = "Saving the sign-in…" };
                var (identity, details, label) = Describe(Provider, credential);
                var saved = await owner.vault.SaveSignInAsync(Provider, credential, identity, details, Label ?? label, Snapshot().AccountId, CancellationToken.None)
                    .ConfigureAwait(false);
                Finish(SignInState.Succeeded, saved.Reauthenticated ? $"Signed in again as “{saved.Account.Label}”." : $"Signed in as “{saved.Account.Label}”.", saved);
                owner.log.LogInformation("Sign-in {Flow} for {Provider} stored account {Account}.", Id, Provider, saved.Account.Id);
            }
            catch (Exception error) when (stop.IsCancellationRequested && error is OperationCanceledException or InvalidOperationException or TimeoutException)
            {
                string message;
                SignInState state;
                lock (gate)
                {
                    state = stopState ?? SignInState.Cancelled;
                    message = stopMessage ?? "The sign-in timed out; start it again.";
                }
                Finish(state, message, null);
                owner.log.LogInformation("Sign-in {Flow} for {Provider} ended: {State}.", Id, Provider, state);
            }
            catch (Exception error)
            {
                var message = Explain(error, credential);
                Finish(SignInState.Failed, message, null);
                owner.log.LogWarning("Sign-in {Flow} for {Provider} failed: {Message}", Id, Provider, message);
            }
            finally
            {
                // The source is not disposed (a late Stop may still cancel it, harmlessly); its timeout timer is turned off instead.
                try { stop.CancelAfter(Timeout.InfiniteTimeSpan); }
                catch (ObjectDisposedException) { }
            }
        }

        private static string Explain(Exception error, OAuthCredentialSnapshot? credential)
        {
            var text = error switch
            {
                KeyNotFoundException => "The account being signed in again was removed; nothing was stored.",
                HttpRequestException => "Could not reach the sign-in server: " + error.Message,
                TimeoutException => "The sign-in server did not answer in time.",
                _ => error.Message,
            };
            var scrubbed = SecretText.Scrub(text, credential?.Access, credential?.Refresh);
            return scrubbed.Length == 0 ? "The sign-in failed." : scrubbed;
        }

        private void Finish(SignInState state, string message, CredentialVault.SavedSignIn? saved)
        {
            TaskCompletionSource<string>? prompt;
            lock (gate)
            {
                if (status.Finished) return;
                status = status with
                {
                    State = state, Message = message, AcceptsCode = false, Account = saved?.Account, AccountId = saved?.Account.Id ?? status.AccountId,
                    Reauthenticated = saved?.Reauthenticated ?? false, BecameActive = saved?.BecameActive ?? false,
                };
                FinishedAt = owner.options.Time.GetUtcNow();
                prompt = manual;
                manual = null;
                pendingCode = null;
            }
            prompt?.TrySetCanceled();
            ready.TrySetResult();
            done.TrySetResult();
        }
    }
}
