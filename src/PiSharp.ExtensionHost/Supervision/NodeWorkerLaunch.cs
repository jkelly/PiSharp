using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using PiSharp.ExtensionHost.Protocol;

namespace PiSharp.ExtensionHost.Supervision;

public enum NodeWorkerProbeMode { Normal, NoHello, WrongGeneration, ExitBeforeHello, IgnoreShutdown }
public enum NodeWorkerStopReason { Shutdown, Startup, UnexpectedExit, ProtocolFault, StderrLimit, StderrFault, Liveness, Generation }
public sealed record NodeWorkerSupervisionOptions(int StartupMilliseconds = 5000, int ShutdownGraceMilliseconds = 500,
    int MaximumStderrBytes = 65_536, int StderrReadBufferBytes = 4096, int LivenessMilliseconds = 2000,
    int MaximumStdoutAfterProtocolBytes = 65_536)
{
    internal void Validate()
    {
        if (StartupMilliseconds is < 100 or > 30_000 || ShutdownGraceMilliseconds is < 50 or > 10_000 ||
            MaximumStderrBytes is < 1 or > 1_048_576 || StderrReadBufferBytes is < 1 or > 65_536 ||
            LivenessMilliseconds is < 100 or > 30_000 || MaximumStdoutAfterProtocolBytes is < 1 or > 1_048_576)
            throw new ArgumentOutOfRangeException(nameof(NodeWorkerSupervisionOptions));
    }
}
/// <summary>Fixed authored peer only. Neither arbitrary arguments nor an arbitrary JavaScript entry can be supplied.</summary>
public sealed class NodeWorkerLaunch
{
    public const string RuntimeVersion = "v24.19.0";
    public const string RuntimeSha256 = "3602f2bb1a10f2cbab4c36886218a33c1ab3db87290e73b033c46c77147d0237";
    public const string EntryRelativePath = "tools/NodeWorker/protocol-probe-worker.mjs";
    public const string EntrySha256 = "96aef982693de523a759cbd0254d1fe61f6eb57ec3f1a2727b9d2c54ebab8cdb";
    public string NodePath { get; }
    public string RepositoryRoot { get; }
    public string EntryPath { get; }
    public string RunRoot { get; }
    public long WorkerGeneration { get; }
    public long SessionGeneration { get; }
    public NodeWorkerProbeMode Mode { get; }
    public NodeWorkerSupervisionOptions Options { get; }
    public WorkerProtocolOptions ProtocolOptions { get; }
    internal NodeExtensionWorkerLaunch? ExtensionProfile { get; private init; }
    internal NodeHelloWorkerLaunch? HelloProfile { get; private init; }
    internal NodeCommandInputWorkerLaunch? CommandInputProfile { get; private init; }
    internal bool HasExtensionProfile => ExtensionProfile is not null || HelloProfile is not null || CommandInputProfile is not null;
    internal string ApprovedEntrySha256 => CommandInputProfile is not null ? NodeCommandInputWorkerLaunch.EntrySha256 :
        HelloProfile is not null ? NodeHelloWorkerLaunch.EntrySha256 :
        ExtensionProfile is null ? EntrySha256 : NodeExtensionWorkerLaunch.EntrySha256;
    internal string ProfileName => CommandInputProfile is not null ? "real-command-input-0" : HelloProfile is not null ? "real-hello-tool-0" :
        ExtensionProfile is null ? "authored-fixed-node-probe" : "real-protected-paths-hook-0";
    private NodeWorkerLaunch(string node, string repository, string entry, string runRoot, long worker, long session,
        NodeWorkerProbeMode mode, NodeWorkerSupervisionOptions options, WorkerProtocolOptions protocol)
    { NodePath = node; RepositoryRoot = repository; EntryPath = entry; RunRoot = runRoot; WorkerGeneration = worker;
      SessionGeneration = session; Mode = mode; Options = options; ProtocolOptions = protocol; }
    public static NodeWorkerLaunch ForProbe(string nodeExecutable, string repositoryRoot, string freshRunRoot,
        long workerGeneration, long sessionGeneration, NodeWorkerProbeMode mode = NodeWorkerProbeMode.Normal,
        NodeWorkerSupervisionOptions? options = null, WorkerProtocolOptions? protocolOptions = null)
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("This approved runtime profile is Windows x64.");
        options ??= new(); protocolOptions ??= new(); options.Validate(); protocolOptions.Validate();
        if (protocolOptions.MaximumFrameBytes > 1_048_576 || protocolOptions.MaximumPendingCalls > 32 ||
            protocolOptions.MaximumCallbacks > 16 || protocolOptions.MaximumPendingWrites > 32 ||
            protocolOptions.MaximumProgressPerCall > 16 || protocolOptions.MaximumBufferedBytes > 8_388_608)
            throw new ArgumentOutOfRangeException(nameof(protocolOptions), "Fixed peer limits cannot be enlarged by the native caller.");
        WorkerFrameCodec.CheckIdentity(workerGeneration); WorkerFrameCodec.CheckIdentity(sessionGeneration);
        _ = new WorkerFrameCodec(protocolOptions).Encode(new(WorkerMessageKind.Hello, workerGeneration, sessionGeneration,
            Features: ["requests", "callbacks", "progress", "cancel", "tagged-values"]));
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        var node = Absolute(nodeExecutable); var repo = Absolute(repositoryRoot); var root = Absolute(freshRunRoot);
        var entry = Path.Combine(repo, EntryRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(repo) || !File.Exists(node) || !File.Exists(entry)) throw new IOException("Required approved launch input is missing.");
        NoLinks(repo); NoLinks(node); NoLinks(entry); NoLinks(root);
        if (Overlaps(root, repo) || Overlaps(root, Path.GetDirectoryName(node)!))
            throw new IOException("Run root overlaps a read-only launch input.");
        CheckFreshRoot(root);
        // These checks perform no write and no execution. Admission is repeated under held file handles at start.
        using (var runtime = OpenPinned(node, RuntimeSha256)) { }
        using (var source = OpenPinned(entry, EntrySha256)) { }
        return new(node, repo, entry, root, workerGeneration, sessionGeneration, mode, options, protocolOptions);
    }
    internal (FileStream Runtime, FileStream Entry) AcquirePins()
    {
        NoLinks(NodePath); NoLinks(EntryPath); NoLinks(RunRoot); CheckFreshRoot(RunRoot);
        var runtime = OpenPinned(NodePath, RuntimeSha256);
        try { return (runtime, OpenPinned(EntryPath, ApprovedEntrySha256)); }
        catch { runtime.Dispose(); throw; }
    }
    // Only the separately admitted sealed profile can reach this constructor. ForProbe is unchanged.
    internal static NodeWorkerLaunch FromExtension(NodeExtensionWorkerLaunch profile) =>
        new(profile.NodePath, profile.RepositoryRoot, profile.EntryPath, profile.RunRoot,
            profile.WorkerGeneration, profile.SessionGeneration, NodeWorkerProbeMode.Normal,
            new(), new()) { ExtensionProfile = profile };
    internal static NodeWorkerLaunch FromHelloExtension(NodeHelloWorkerLaunch profile) =>
        new(profile.NodePath, profile.RepositoryRoot, profile.EntryPath, profile.RunRoot,
            profile.WorkerGeneration, profile.SessionGeneration, NodeWorkerProbeMode.Normal,
            new(), new()) { HelloProfile = profile };
    internal static NodeWorkerLaunch FromCommandInputExtension(NodeCommandInputWorkerLaunch profile) =>
        new(profile.NodePath, profile.RepositoryRoot, profile.EntryPath, profile.RunRoot,
            profile.WorkerGeneration, profile.SessionGeneration, NodeWorkerProbeMode.Normal,
            new(), new()) { CommandInputProfile = profile };
    internal void CreateOwnedRoot()
    {
        NoLinks(RunRoot); CheckFreshRoot(RunRoot);
        // Directory.CreateDirectory accepts an existing directory. Atomic creation rejects that ownership ambiguity.
        if (!CreateDirectoryW(RunRoot, IntPtr.Zero)) throw new IOException("Fresh worker run root could not be created.", new Win32Exception(Marshal.GetLastPInvokeError()));
        foreach (var name in new[] { "home", "appdata", "localappdata", "temp" }) Directory.CreateDirectory(Path.Combine(RunRoot, name));
    }
    internal string ModeArgument => Mode switch
    { NodeWorkerProbeMode.Normal => "normal", NodeWorkerProbeMode.NoHello => "no-hello",
      NodeWorkerProbeMode.WrongGeneration => "wrong-generation", NodeWorkerProbeMode.ExitBeforeHello => "exit-before-hello",
      NodeWorkerProbeMode.IgnoreShutdown => "ignore-shutdown", _ => throw new InvalidOperationException() };
    internal static FileStream OpenPinned(string path, string expected)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var hash = Convert.ToHexStringLower(SHA256.HashData(file));
            if (!StringComparer.Ordinal.Equals(hash, expected)) throw new IOException("Approved launch SHA256 differs.");
            file.Position = 0; return file;
        }
        catch { file.Dispose(); throw; }
    }
    internal static string Absolute(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal) ||
            path.Any(c => char.IsControl(c))) throw new IOException("A local absolute path is required.");
        var full = Path.GetFullPath(path);
        return Path.TrimEndingDirectorySeparator(full);
    }
    internal static bool Overlaps(string first, string second) => Within(first, second) || Within(second, first);
    private static bool Within(string path, string ancestor) => StringComparer.OrdinalIgnoreCase.Equals(path, ancestor) ||
        path.StartsWith(Path.TrimEndingDirectorySeparator(ancestor) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    internal static void NoLinks(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
            if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked launch/run paths are not admitted.");
    }
    private static void CheckFreshRoot(string root)
    {
        if (Path.Exists(root) || !Directory.Exists(Path.GetDirectoryName(root)))
            throw new IOException("Run root must be new beneath an existing caller-owned parent.");
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(string path, IntPtr securityAttributes);
}
