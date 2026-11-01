// Decision 0011 (run W5, retest 1): small fixes found when every W5 unit and fix met on feature/web-agent. PresetRules.LooksSecret (a
// preset's and a pinned test case's check for a key-shaped value) knew fewer key prefixes than the masked hints (SecretText): a key with
// xai-, gsk_, nvapi-, csk-, hf_ or fw_ that this app does not store passed into a stored case. It now knows every prefix SecretText masks.
using PiSharp.WebAgent.Evals;
using PiSharp.WebAgent.Presets;
using PiSharp.WebAgent.Runs;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> W5RetestCases() =>
    [
        Case("w5-retest.looks-secret-knows-every-key-prefix-the-app-masks", () =>
        {
            // SecretText's prefixes (Credentials/SecretText.cs), each followed by a key-like tail.
            string[] prefixes = ["github_pat_", "sk-ant-api03-", "sk-ant-", "sk-proj-", "sk-svcacct-", "sk-or-v1-", "sk-or-", "nvapi-", "sk-", "gsk_",
                "xai-", "csk-", "hf_", "fw_", "ghu_", "gho_", "ghp_", "AIza"];
            const string tail = "Q7wE9rT2yU4iO6pA";
            foreach (var prefix in prefixes) Check(PresetRules.LooksSecret(prefix + tail), prefix + "… looks like a key");
            foreach (var prefix in new[] { "xai-", "gsk_", "nvapi-", "csk-", "hf_", "fw_" })
                Check(!PresetRules.LooksSecret(prefix + "ab"), prefix + " with a short tail is not a key");
            foreach (var plain in new[] { "hf-hub-cache", "fw-v2", "my_xai-notes", "csk", "nvapi", "trace-1234567890" })
                Check(!PresetRules.LooksSecret(plain), plain + " is not a key");

            // A pinned case refuses such a value without repeating it (here with no vault: the key is stored nowhere).
            var basic = new RunSpec { Provider = "faux", Model = "echo", Messages = [new RunMessage { Role = "user", Text = "hello" }] };
            foreach (var prefix in new[] { "xai-", "gsk_", "nvapi-", "csk-", "hf_", "fw_" })
            {
                var key = prefix + tail;
                var error = Throws<ArgumentException>(() => TestCase.CaseSpec(basic with { Headers = new Dictionary<string, string> { ["X-Trace"] = key } }),
                    prefix + " key in a header is refused in a case");
                Check(!error.Message.Contains(tail, StringComparison.Ordinal), "the refusal does not repeat the key: " + error.Message);
            }
        }),
    ];
}
