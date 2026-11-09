using System.Runtime.InteropServices;
using PiSharp.AI.Authentication.OAuth;
using PiSharp.Cli;
using PiSharp.Cli.Authentication;
using PiSharp.Cli.Pi;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> LifecycleGapCases() =>
    [
        ("lifecycle.catalogue-oauth-persistence-before-eof-exit", () => CatalogueCredentialDrain("eof")),
        ("lifecycle.catalogue-oauth-persistence-before-signal-exit", () => CatalogueCredentialDrain("signal")),
        ("lifecycle.catalogue-oauth-first-signal-during-eof-drain", () => CatalogueCredentialDrain("eof-signal")),
        ("lifecycle.catalogue-oauth-persistence-before-stream-open-failure", () => CatalogueCredentialDrain("open-failure")),
        ("lifecycle.first-signal-graceful-repeated-signal-forces-exit", () =>
        {
            var exits = new List<int>();
            using var signals = new ShutdownSignals(register: false, exits.Add);
            signals.Signal(PosixSignal.SIGTERM);
            Check(signals.Token.IsCancellationRequested && signals.ExitCode == 143 && exits.Count == 0, "first signal must drain");
            signals.Signal(PosixSignal.SIGHUP);
            Equal(129, exits.Single(), "repeated signal forces its exit code");
            Equal(143, signals.ExitCode, "first graceful exit reason stays stable");
            return Task.CompletedTask;
        })
    ];

    private static async Task CatalogueCredentialDrain(string end)
    {
        using var sandbox = new Sandbox("credential-drain-" + end);
        sandbox.Vars["OPENAI_API_KEY"] = "synthetic-openai-key";
        var path = sandbox.Write(Path.Combine(sandbox.AgentDir, "auth.json"),
            """{"openrouter":{"type":"oauth","access":"synthetic-old","refresh":"synthetic-r1","expires":1}}""");
        var source = new PausedCredentialStore(new AuthJsonCredentialStore(path));
        var tokens = new RotatingCatalogueOAuth();
        var input = new CredentialDrainInput();
        using var output = new MemoryStream();
        using var stdout = new StringWriter(); using var stderr = new StringWriter();
        using var signals = new ShutdownSignals(register: false);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        Task? refresh = null;
        var host = sandbox.Host(stdout, stderr, null, rpcInput: input, rpcOutput: output) with
        {
            LiveRuntime = sandbox.Runtime() with
            {
                Credentials = () => source,
                CreateAuthHttp = () => new System.Net.Http.HttpMessageInvoker(new UnusedCatalogueAuthHttp()),
                OAuthFlows = (provider, _) => provider == "openrouter" ? tokens : throw new InvalidOperationException("Unexpected catalogue OAuth provider")
            },
            Signals = () => signals,
            CatalogRefreshStarted = task => refresh = task,
            OpenRpcOutput = () =>
            {
                if (end == "open-failure")
                {
                    source.AllowRotation.TrySetResult();
                    // Ensure failure occurs while the actual registry's token rotation is awaiting persistence.
                    source.BeforePersistence.Task.WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
                    throw new IOException("authored output acquisition failure");
                }
                return output;
            }
        };
        var run = PiCommand.RunAsync(["--mode", "rpc", "--provider", "openai", "--model", "gpt-4.1"], host, stop.Token);
        try
        {
            if (end != "open-failure")
            {
                // Ensure the foreground session already owns its input before the background
                // mutation takes auth.json's lock; otherwise startup itself could hide a lost join.
                await input.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
                source.AllowRotation.TrySetResult();
            }
            await source.BeforePersistence.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Check(tokens.Refreshes == 1 && refresh is { IsCompleted: false }, "real registry did not reach fake rotated-token barrier");
            if (end == "signal") signals.Signal(PosixSignal.SIGTERM); else input.Release();
            await input.Closed.Task.WaitAsync(TimeSpan.FromSeconds(20));
            if (end == "eof-signal") signals.Signal(PosixSignal.SIGTERM);
            // Give the complete host exit path a chance to run; a discarded refresh would return here.
            var first = await Task.WhenAny(run, Task.Delay(200));
            Check(!ReferenceEquals(first, run), "host exited before persisting its rotated OAuth credential");
            Check(File.ReadAllText(path).Contains("synthetic-r1", StringComparison.Ordinal), "persistence barrier was bypassed");
        }
        finally
        {
            source.AllowRotation.TrySetResult(); source.ReleasePersistence.TrySetResult(); input.Release();
            // Even a failing assertion must release and join its original host before the
            // sandbox deletes credential files or disposes the fake HTTP dependency.
            try { await run.WaitAsync(TimeSpan.FromSeconds(20)); }
            catch (IOException error) when (end == "open-failure" && error.Message == "authored output acquisition failure") { }
        }
        if (end == "open-failure")
        {
            try { await run.WaitAsync(TimeSpan.FromSeconds(20)); throw new InvalidOperationException("expected stream acquisition failure"); }
            catch (IOException error) when (error.Message == "authored output acquisition failure") { }
        }
        else Equal(end is "signal" or "eof-signal" ? 143 : 0, await run.WaitAsync(TimeSpan.FromSeconds(20)), "exit; " + stderr);
        Check(refresh!.IsCompleted, "owned catalogue refresh did not settle before return");
        var saved = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!["openrouter"]!;
        Equal("r2", saved["refresh"]!.GetValue<string>(), "rotated credential persisted");
        Equal("new-access", saved["access"]!.GetValue<string>(), "access credential persisted");
    }

    // OpenRouter is the current credential-requiring catalogue overlay. Its built-in refresh
    // is identity, so rotation is explicitly admitted through the existing runtime OAuth seam;
    // this verifies the ownership guarantee without claiming a default OpenRouter token rotates.
    private sealed class RotatingCatalogueOAuth : IProviderOAuth
    {
        public int Refreshes;
        public string Name => "Synthetic catalogue OAuth";
        public bool IsSubscription => false;
        public string? LoginLabel => null;
        public Task<OAuthCredentialSnapshot> LoginAsync(IProviderAuthInteraction interaction, ProviderLoginOptions? options, CancellationToken token) =>
            throw new InvalidOperationException("The catalogue must not attempt login");
        public Task<OAuthCredentialSnapshot> RefreshAsync(string provider, OAuthCredentialSnapshot current, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Check(provider == "openrouter" && current.Refresh == "synthetic-r1", "unexpected fake refresh input");
            Interlocked.Increment(ref Refreshes);
            return Task.FromResult(new OAuthCredentialSnapshot("new-access", "r2", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds()));
        }
        public ProviderModelAuth ToAuth(OAuthCredentialSnapshot credential) => new(credential.Access);
    }

    private sealed class UnusedCatalogueAuthHttp : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken token) =>
            throw new InvalidOperationException("Synthetic OAuth must not send HTTP");
    }

    private sealed class PausedCredentialStore(AuthJsonCredentialStore inner) : IProviderCredentialSource
    {
        public TaskCompletionSource AllowRotation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource BeforePersistence { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleasePersistence { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<OAuthCredentialSnapshot?> ReadAsync(string provider, CancellationToken token) => inner.ReadAsync(provider, token);
        public Task<StoredCredentialEntry?> ReadEntryAsync(string provider, CancellationToken token) => inner.ReadEntryAsync(provider, token);
        public Task<IReadOnlyList<(string Provider, string Type)>> ListAsync(CancellationToken token) => inner.ListAsync(token);
        public async Task<OAuthCredentialSnapshot?> ModifyAsync(string provider,
            Func<OAuthCredentialSnapshot?, CancellationToken, Task<OAuthCredentialSnapshot?>> mutation, CancellationToken token)
        {
            await AllowRotation.Task.WaitAsync(token);
            return await inner.ModifyAsync(provider, async (current, currentToken) =>
            {
                var replacement = await mutation(current, currentToken);
                BeforePersistence.TrySetResult();
                await ReleasePersistence.Task;
                return replacement;
            }, token);
        }
    }

    private sealed class CredentialDrainInput : Stream
    {
        private readonly TaskCompletionSource eof = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => eof.TrySetResult();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { Entered.TrySetResult(); await eof.Task.WaitAsync(cancellationToken); return 0; }
        protected override void Dispose(bool disposing) { Closed.TrySetResult(); base.Dispose(disposing); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
