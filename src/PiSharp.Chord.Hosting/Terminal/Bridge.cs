// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/tui/bridge.ts.
// Native adapters retain explicit trusted-code and terminal ownership boundaries.
using System.Text.Json.Nodes;

namespace PiSharp.Chord.Hosting.Terminal;

/// <summary>Public client metadata. Credentials belong to the launcher and never enter this value.</summary>
public sealed record TerminalStart(string Base, string Combined, int Columns = 80, int Rows = 24,
    string Colors = "256", string Mode = "dark", IReadOnlyList<string>? Locales = null);

public abstract record TerminalRequest
{
    public sealed record Fetch(string Path) : TerminalRequest;
    public sealed record Base : TerminalRequest;
    public sealed record Copy(string Text) : TerminalRequest;
    public sealed record Open(string Url) : TerminalRequest;
    public sealed record Attach(string Path) : TerminalRequest;
}

public abstract record ClientMessage
{
    public sealed record Write(string Data) : ClientMessage;
    public sealed record SocketOpen(long Socket) : ClientMessage;
    public sealed record SocketSend(long Socket, ReadOnlyMemory<byte> Bytes) : ClientMessage;
    public sealed record SocketClose(long Socket) : ClientMessage;
    public sealed record Request(long Id, TerminalRequest Value) : ClientMessage;
    public sealed record Outdated : ClientMessage;
    public sealed record Failed(string Message) : ClientMessage;
    public sealed record Exit(int Code) : ClientMessage;
}

public abstract record LauncherMessage
{
    public sealed record Input(string Data) : LauncherMessage;
    public sealed record Resize(int Columns, int Rows) : LauncherMessage;
    public sealed record SocketOpened(long Socket) : LauncherMessage;
    public sealed record SocketMessage(long Socket, ReadOnlyMemory<byte> Bytes) : LauncherMessage;
    public sealed record SocketClosed(long Socket) : LauncherMessage;
    public sealed record Reply(long Id, JsonNode? Value) : LauncherMessage;
    public sealed record Attached(long Id, string Name, ReadOnlyMemory<byte> Bytes) : LauncherMessage;
    public sealed record Refused(long Id, string Message) : LauncherMessage;
}

public sealed record TerminalAttachment(string Name, ReadOnlyMemory<byte> Bytes);

/// <summary>A native client's data bridge. Implementations execute caller-trusted C# code, not downloaded code.</summary>
public interface ITerminalBridge
{
    TerminalStart Start { get; }
    void Send(ClientMessage message);
    IDisposable Subscribe(Action<LauncherMessage> listener);
}
