using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using PiSharp.Extensions.Mcp.Discovery;

internal static partial class Program
{
    private static object UnicodeProfile() => new
    {
        backend = UnicodeWordSegmenter.Backend,
        icu = UnicodeWordSegmenter.IsAvailable,
        normalizes = "e\u0301".Normalize(NormalizationForm.FormC) == "é",
        greek = ToolSearch.Tokenize("ΟΣ ΟΣΑ"),
        accents = ToolSearch.Tokenize("CAFÉ cafe\u0301"),
        cjk = ToolSearch.Tokenize("搜索仓库问题"),
        thai = ToolSearch.Tokenize("ค้นหาเอกสาร"),
        fallback = UnicodeWordSegmenter.FallbackSegments("搜索仓库问题 ค้นหาเอกสาร"),
    };

    private static async Task UnicodeProfiles()
    {
        foreach (var invariant in new[] { false, true })
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("No current executable.");
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            start.ArgumentList.Add("--unicode-profile");
            start.Environment["DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"] = invariant ? "1" : "0";
            using var child = Process.Start(start) ?? throw new InvalidOperationException("Unicode profile failed to start.");
            var stdout = child.StandardOutput.ReadToEndAsync(); var stderr = child.StandardError.ReadToEndAsync();
            try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
            finally { if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); } }
            Equal(0, child.ExitCode, "Unicode child exit: " + await stderr);
            using var profile = JsonDocument.Parse(await stdout); var root = profile.RootElement;
            string[] Words(string property) => root.GetProperty(property).EnumerateArray().Select(value => value.GetString()!).ToArray();
            Check(!string.IsNullOrWhiteSpace(root.GetProperty("backend").GetString()), "Missing segmentation backend diagnosis.");
            Equal(!invariant, root.GetProperty("normalizes").GetBoolean(), "NFC profile");
            // .NET invariant casing intentionally differs from ECMAScript contextual Final_Sigma (ος).
            Names(["οσ", "οσα"], Words("greek"), "documented .NET final sigma");
            Names(invariant ? ["café", "cafe\u0301"] : ["café", "café"], Words("accents"), "NFC normalization profile");
            var icu = root.GetProperty("icu").GetBoolean();
            Names(icu ? ["搜索", "仓库", "问题"] : ["搜索仓库问题"], Words("cjk"), "CJK backend profile");
            Names(icu ? ["ค้นหา", "เอกสาร"] : ["ค้นหาเอกสาร"], Words("thai"), "Thai backend profile");
            Names(["搜索仓库问题", "ค้นหาเอกสาร"], Words("fallback"), "deterministic no-ICU fallback");
            Console.WriteLine("Unicode profile invariant=" + invariant + ": " + root.GetRawText());
        }
    }
}
