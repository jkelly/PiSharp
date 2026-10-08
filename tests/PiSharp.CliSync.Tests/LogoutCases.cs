using PiSharp.Cli.Authentication;
using PiSharp.Cli.Interactive;

// /logout (interactive-mode.ts showOAuthSelector("logout"), getLogoutProviderOptions; auth-storage.ts delete): the providers with a
// stored auth.json credential, sorted by name, then the selected one is deleted. Authored expectations; no network.
internal static partial class Program
{
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
