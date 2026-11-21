// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/durable/test/env-node-list-race.test.ts ("NodeExecutionEnv.listDir under
// concurrent changes"), as sync160.env-list-race.<case>, on LocalExecutionEnv (upstream NodeExecutionEnv). Adapted:
// - Upstream mocks `node:fs/promises` so a one-shot callback runs right before the `lstat` of one path, as another process would between
//   readdir and lstat. Here the callback runs in an override of LocalFileSystem.StatListed, the internal seam ListDirAsync stats each
//   enumerated entry through (as EnvNodeCases.cs overrides SyncFile).
// Authored (no upstream case): still-fails-on-an-error-other-than-a-missing-entry pins that only a missing entry is left out;
// jsonl-read-only-open-lists-beside-a-writer-that-renames-a-reclaim-file is the user-visible failure of #11994 on the JSONL storage's own
// directory listing; lists-a-directory-emptied-during-the-listing covers every entry going missing.
using System.Collections.Immutable;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Env;
using PiSharp.Durable.Tools.Env;

internal static partial class Program
{
    /// <summary>Runs a one-shot callback right before the stat of <see cref="RacePath"/>, or of every entry when it is null (upstream's
    /// <c>race</c> and its <c>lstat</c> mock).</summary>
    private sealed class RacingListEnv(string cwd) : LocalExecutionEnv(cwd)
    {
        public string? RacePath { get; set; }
        public Action? BeforeStat { get; set; }
        public Func<string, Result<FileMetadata, FileError>?>? Replace { get; set; }

        internal override Result<FileMetadata, FileError> StatListed(string entry)
        {
            if (BeforeStat is { } callback && (RacePath is null || string.Equals(entry, RacePath, StringComparison.Ordinal)))
            {
                BeforeStat = null;
                callback();
            }
            return Replace?.Invoke(entry) ?? base.StatListed(entry);
        }
    }

    private static IEnumerable<(string, Func<Task>)> Sync160EnvListRaceCases()
    {
        const string prefix = "sync160.env-list-race.";

        yield return Case(prefix + "leaves-out-an-entry-removed-between-enumeration-and-lstat", () => WithEnvRoot(async dir =>
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "doc-1.jsonl"), "kept\n");
            await File.WriteAllTextAsync(Path.Combine(dir, "doc-2.jsonl.reclaim"), "replacement\n");
            // A writer replaces a sidecar: the reclaim file is renamed over its target after the reader enumerated it.
            var env = new RacingListEnv(dir) { RacePath = Path.Combine(dir, "doc-2.jsonl.reclaim") };
            env.BeforeStat = () => File.Move(Path.Combine(dir, "doc-2.jsonl.reclaim"), Path.Combine(dir, "doc-2.jsonl"), overwrite: true);

            var entries = (await env.ListDirAsync(".", EnvContext)).GetOrThrow();

            Check(env.BeforeStat is null, "the callback ran");
            Equal("doc-1.jsonl", string.Join(",", entries.Select(entry => entry.Name)), "entries");
        }));

        yield return Case(prefix + "lists-a-directory-emptied-during-the-listing", () => WithEnvRoot(async dir =>
        {
            foreach (var name in new[] { "a.txt", "b.txt", "c.txt" }) await File.WriteAllTextAsync(Path.Combine(dir, name), name);
            var env = new RacingListEnv(dir);
            env.BeforeStat = () =>
            {
                foreach (var file in Directory.GetFiles(dir)) File.Delete(file);
            };
            var listed = await env.ListDirAsync(".", EnvContext);
            Check(listed.IsOk, "the listing succeeds: " + listed.Error);
            Equal(0, listed.Value.Length, "entries");
        }));

        yield return Case(prefix + "still-fails-on-an-error-other-than-a-missing-entry", () => WithEnvRoot(async dir =>
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "a.txt"), "a");
            await File.WriteAllTextAsync(Path.Combine(dir, "b.txt"), "b");
            var denied = Path.Combine(dir, "b.txt");
            var env = new RacingListEnv(dir)
            {
                Replace = entry => entry == denied
                    ? (Result<FileMetadata, FileError>?)new FileError(FileErrorCode.PermissionDenied, "EACCES: permission denied, lstat", entry)
                    : null,
            };
            var listed = await env.ListDirAsync(".", EnvContext);
            Check(!listed.IsOk, "the listing fails");
            Equal(FileErrorCode.PermissionDenied, listed.Error!.Code, "code");
            Equal(denied, listed.Error.Path, "path");
        }));

        yield return Case(prefix + "jsonl-read-only-open-lists-beside-a-writer-that-renames-a-reclaim-file", () => WithEnvRoot(async dir =>
        {
            // What #11994 reported: a read-only JSONL open beside a live writer rejected with "JSONL directory listing failed: ENOENT" when
            // the writer renamed a `.reclaim` file over a sidecar during the listing. The storage lists its directory through ListDirAsync.
            await File.WriteAllTextAsync(Path.Combine(dir, "doc-1.jsonl"), "kept\n");
            await File.WriteAllTextAsync(Path.Combine(dir, "doc-2.jsonl"), "old\n");
            await File.WriteAllTextAsync(Path.Combine(dir, "doc-2.jsonl.reclaim"), "replacement\n");
            var env = new RacingListEnv(dir) { RacePath = Path.Combine(dir, "doc-2.jsonl.reclaim") };
            env.BeforeStat = () => File.Move(Path.Combine(dir, "doc-2.jsonl.reclaim"), Path.Combine(dir, "doc-2.jsonl"), overwrite: true);
            IFileSystem fs = env;
            var entries = (await fs.ListDirAsync(dir, EnvContext)).GetOrThrow();
            Equal("doc-1.jsonl,doc-2.jsonl", string.Join(",", entries.Select(entry => entry.Name).Order(StringComparer.Ordinal)), "entries");
            Equal("replacement\n", await File.ReadAllTextAsync(Path.Combine(dir, "doc-2.jsonl")), "the sidecar was replaced");
        }));
    }
}
