// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/node/app/layout.ts, packages/chord/src/node/app/secrets.ts.
// Authored native regression; not an upstream execution capture.
using System.Security.AccessControl;
using System.Security.Principal;
using System.Diagnostics;
using PiSharp.Chord;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Hosting.App;

internal static class AppLayoutTests
{
    public static async Task RunAsync()
    {
        var parent = Path.Combine(Environment.CurrentDirectory, "artifacts", "chord-app-layout", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        try
        {
            var path = Path.Combine(parent, "data");
            AppLayout layout;
            await using (var opened = await Layout.OpenAsync(path, ChordContext.Background))
            {
                layout = opened.Layout;
                Require(Directory.Exists(layout.ServerDirectory) && Directory.Exists(layout.FilesDirectory) && Directory.Exists(layout.WorkerBasesDirectory), "private layout is complete");
                Require(Path.GetFullPath(path).Equals(layout.DataDir, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal), "canonical data directory returned");
                await MustRefuse(() => Layout.OpenAsync(OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path, ChordContext.Background), "a second server acquired the same directory");
                var kind = new HostKind("document", new() { ["id"] = Schemas.Str() });
                var upper = Layout.WorkerDirectory(layout, kind.Address(("id", System.Text.Json.Nodes.JsonValue.Create("A./\\"))));
                var lower = Layout.WorkerDirectory(layout, kind.Address(("id", System.Text.Json.Nodes.JsonValue.Create("a./\\"))));
                Require(!upper.Equals(lower, StringComparison.OrdinalIgnoreCase), "uppercase values remain distinct on a case-insensitive filesystem");
                var leaf = Path.GetFileName(upper);
                Require(!leaf.Contains('.') && !leaf.Contains('/') && !leaf.Contains('\\'), "address key is one encoded directory");
                var longKey = Layout.WorkerDirectory(layout, kind.Address(("id", System.Text.Json.Nodes.JsonValue.Create(new string('x', 500)))));
                Require(Path.GetFileName(longKey).StartsWith("sha256-", StringComparison.Ordinal), "long keys use their SHA256 digest");

                var token = Secrets.NewToken();
                Require(token.Length == 43 && Secrets.MatchesHash(token, Secrets.HashToken(token)), "256-bit secret verifies against its hash");
                Require(!Secrets.MatchesHash(token + "x", Secrets.HashToken(token)) && !Secrets.MatchesHash(token, new string('g', 64)), "mismatched and malformed hashes fail closed");
                var values = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Secrets.SecretFileAsync(layout.CookieSecretFile)));
                Require(values.Distinct(StringComparer.Ordinal).Count() == 1, "concurrent create-only secret writers use the same winner");
                Require(await Secrets.ReadSecretAsync(layout.CookieSecretFile) == values[0], "secret persisted completely");
                Require(!await Secrets.WriteSecretAsync(layout.CookieSecretFile, "replacement", replace: false), "create-only never overwrites");
                await Secrets.WriteSecretAsync(layout.CookieSecretFile, token);
                Require(await Secrets.ReadSecretAsync(layout.CookieSecretFile) == token, "atomic replacement persisted");
                Require(!Directory.EnumerateFiles(layout.ServerDirectory, ".*.tmp").Any(), "secret temporary files are cleaned");
                VerifyPermissions(layout.DataDir, directory: true);
                VerifyPermissions(layout.CookieSecretFile, directory: false);

                var loose = Path.Combine(layout.ServerDirectory, "loose");
                File.WriteAllText(loose, "test");
                if (OperatingSystem.IsWindows())
                {
                    var acl = new System.IO.FileInfo(loose).GetAccessControl();
                    acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
                    new System.IO.FileInfo(loose).SetAccessControl(acl);
                }
                else File.SetUnixFileMode(loose, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
                Layout.PrivateFile(loose); VerifyPermissions(loose, directory: false);

                await using var store = await Layout.OpenStoreAsync(layout.StoreFile, ChordContext.Background);
                Require(store.LastSeq == 0, "native store opens in the private layout");
                VerifyPermissions(layout.StoreFile, directory: false);
                foreach (var sidecar in new[] { layout.StoreFile + "-wal", layout.StoreFile + "-shm" })
                    if (File.Exists(sidecar)) VerifyPermissions(sidecar, directory: false);
            }
            await using (var reopened = await Layout.OpenAsync(path, ChordContext.Background)) Require(reopened.Layout.DataDir == layout.DataDir, "closing releases the directory lock");

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            await MustRefuse(() => Layout.OpenAsync(home, ChordContext.Background), "home directory admitted");
            await MustRefuse(() => Layout.OpenAsync(Path.GetPathRoot(path)!, ChordContext.Background), "filesystem root admitted");
            var outside = Path.Combine(parent, "outside"); Directory.CreateDirectory(outside);
            var linked = Path.Combine(path, "redirect");
            await CreateDirectoryAliasAsync(linked, outside);
            try
            {
                try { Layout.PrivateDirectory(linked); throw new Exception("internal symbolic link admitted"); }
                catch (IOException) { }
                await using var canonical = await Layout.OpenAsync(outside, ChordContext.Background);
                await MustRefuse(() => Layout.OpenAsync(linked, ChordContext.Background), "symlink alias acquired a second lock");
            }
            finally { Directory.Delete(linked); }
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    private static async Task CreateDirectoryAliasAsync(string linked, string outside)
    {
        try { Directory.CreateSymbolicLink(linked, outside); return; }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows()) { }
        catch (IOException error) when (OperatingSystem.IsWindows() && (error.HResult & 0xffff) == 1314) { }

        // Junctions exercise the same reparse-point refusal and canonical-lock paths on Windows,
        // but do not require SeCreateSymbolicLinkPrivilege. Both names are fixed fixture-owned
        // paths below WorkingDirectory, so no shell interpretation of workspace paths is needed.
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"))
        {
            Arguments = "/d /c mklink /J data\\redirect outside",
            WorkingDirectory = Path.GetDirectoryName(outside)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(start) ?? throw new IOException("Could not start the junction fixture helper.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errorOutput = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        catch { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } throw; }
        await Task.WhenAll(output, errorOutput);
        Require(process.ExitCode == 0 && new DirectoryInfo(linked).LinkTarget is not null, "Windows junction fixture could not be created: " + errorOutput.Result);
    }

    private static void VerifyPermissions(string path, bool directory)
    {
        if (OperatingSystem.IsWindows())
        {
            var owner = WindowsIdentity.GetCurrent().User!;
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            FileSystemSecurity security = directory ? new DirectoryInfo(path).GetAccessControl() : new System.IO.FileInfo(path).GetAccessControl();
            Require(security.AreAccessRulesProtected, "Windows ACL inherits broader access");
            Require(owner.Equals(security.GetOwner(typeof(SecurityIdentifier))), "Windows ACL owner differs");
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                Require(rule.AccessControlType == AccessControlType.Allow && (rule.IdentityReference.Equals(owner) || rule.IdentityReference.Equals(system)), "Windows ACL grants another principal access");
        }
        else
        {
            var expected = UnixFileMode.UserRead | UnixFileMode.UserWrite | (directory ? UnixFileMode.UserExecute : 0);
            Require(File.GetUnixFileMode(path) == expected, "Unix permissions are not private");
        }
    }

    private static async Task MustRefuse(Func<Task<AppDataDirectory>> open, string failure)
    {
        try { await using var unexpected = await open(); }
        catch (IOException) { return; }
        throw new Exception(failure);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception("App layout regression: " + message); }
}
