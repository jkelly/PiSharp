using PiSharp.Cli.Authentication;
using PiSharp.Cli.Interactive;

// /logout (interactive-mode.ts showOAuthSelector("logout"), getLogoutProviderOptions; auth-storage.ts delete): the providers with a
// stored auth.json credential, sorted by name, then the selected one is deleted. Authored expectations; no network.
internal static partial class Program
{
    // Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/coding-agent/src/core/auth-storage.ts (getRetryableLockErrorCode,
    // MAX_EPERM_RETRIES; #10801). On Windows a just-removed lock folder can stay "delete pending" and taking the lock fails with EPERM
    // (access denied with no lock present): it is retried at most 5 times. Elsewhere, and for any other error, it fails at once.
    private static async Task AuthLockEpermRetries()
    {
        var directory = Temp("auth-lock-" + Guid.NewGuid().ToString("N")); var authPath = Path.Combine(directory, "auth.json");
        Directory.CreateDirectory(directory);
        try
        {
            var attempts = 0;
            IAsyncDisposable Failing(string path, int failures, Func<Exception> error) =>
                ++attempts <= failures ? throw error() : new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            var transient = new AuthJsonCredentialStore(authPath) { CreateLockFile = path => Failing(path, 3, () => new UnauthorizedAccessException("denied")) };
            if (OperatingSystem.IsWindows())
            {
                await transient.WriteApiKeyAsync("openai", "k", null, CancellationToken.None);
                Equal(4, attempts, "three EPERM failures, then the lock");
                Check(File.ReadAllText(authPath).Contains("\"openai\"", StringComparison.Ordinal), "the write went through");
                attempts = 0;
                var denied = new AuthJsonCredentialStore(authPath) { CreateLockFile = path => Failing(path, int.MaxValue, () => new UnauthorizedAccessException("denied")) };
                await ThrowsAsync<UnauthorizedAccessException>(() => denied.WriteApiKeyAsync("openai", "k", null, CancellationToken.None), "persistent EPERM");
                Equal(1 + AuthJsonCredentialStore.MaxEpermRetries, attempts, "EPERM retried at most 5 times");
            }
            else
            {
                await ThrowsAsync<UnauthorizedAccessException>(() => transient.WriteApiKeyAsync("openai", "k", null, CancellationToken.None), "EACCES");
                Equal(1, attempts, "access denied fails at once off Windows");
            }
            attempts = 0;
            var other = new AuthJsonCredentialStore(authPath) { CreateLockFile = path => Failing(path, int.MaxValue, () => new IOException("disk")) };
            await ThrowsAsync<IOException>(() => other.WriteApiKeyAsync("openai", "k", null, CancellationToken.None), "other error");
            Equal(1, attempts, "another error with no lock present fails at once");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static async Task<T> ThrowsAsync<T>(Func<Task> run, string what) where T : Exception
    {
        try { await run(); } catch (T error) { return error; }
        throw new InvalidOperationException(what + ": expected " + typeof(T).Name + ".");
    }

    private static async Task LogoutRemovesStoredCredentials()
    {
        var directory = Temp("logout-" + Guid.NewGuid().ToString("N")); var authPath = Path.Combine(directory, "auth.json");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(authPath, "{\"openai\":{\"type\":\"api_key\",\"key\":\"k\"},\"anthropic\":{\"type\":\"oauth\",\"refresh\":\"r\",\"access\":\"a\",\"expires\":1}}");
        try
        {
            var host = new ProviderLoginHost(new AuthJsonCredentialStore(authPath), () => throw new InvalidOperationException("No HTTP for logout."));
            var view = new LockedWriter();
            using var frontend = new InteractiveSessionFrontend(view);
            frontend.Bind((_, _) => Task.CompletedTask); frontend.BindLogin(host);
            await frontend.LineAsync("/logout", CancellationToken.None);
            await WaitFor(view, "(Enter a number to select, /cancel to cancel)");
            await frontend.LineAsync("/cancel", CancellationToken.None);
            await frontend.LoginCompletion;
            await frontend.LineAsync("/logout", CancellationToken.None);
            await WaitFor(view, "(Enter a number to select, /cancel to cancel)", 2);
            await frontend.LineAsync("3", CancellationToken.None);
            await frontend.LineAsync("1", CancellationToken.None);
            await frontend.LoginCompletion;
            await frontend.LineAsync("/logout", CancellationToken.None);
            await WaitFor(view, "(Enter a number to select, /cancel to cancel)", 3);
            await frontend.LineAsync("1", CancellationToken.None);
            await frontend.LoginCompletion;
            await frontend.LineAsync("/logout", CancellationToken.None);
            await frontend.LoginCompletion;
            var both = "Select provider to logout:\n1: Anthropic ✓ configured\n2: OpenAI ✓ configured\n(Enter a number to select, /cancel to cancel)";
            // Cancelling the selector closes it without a message, as upstream.
            Equal(string.Join("\n", both, both, "[login] Enter a number from 1 to 2, or /cancel.", "Logged out of Anthropic",
                "Select provider to logout:\n1: OpenAI ✓ configured\n(Enter a number to select, /cancel to cancel)",
                "Removed stored API key for OpenAI. Environment variables and models.json config are unchanged.",
                "No stored credentials to remove. /logout only removes credentials saved by /login; environment variables and models.json config are unchanged.", ""),
                view.ToString()!.ReplaceLineEndings("\n"), "logout view");
            Equal("{}", await File.ReadAllTextAsync(authPath), "auth.json emptied");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
