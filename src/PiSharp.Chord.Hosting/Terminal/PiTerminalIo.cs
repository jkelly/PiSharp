// PiSharp-only: Adapter from the existing PiSharp terminal to the Chord terminal I/O contract; no JavaScript execution.
using PiSharp.Tui.Pi;

namespace PiSharp.Chord.Hosting.Terminal;

/// <summary>Adapts Pi's existing raw terminal implementation without duplicating OS console ownership.</summary>
public sealed class PiTerminalIo(ITerminal terminal) : ITerminalIo
{
    public int Columns => terminal.Columns;
    public int Rows => terminal.Rows;
    public void Start(Action<string> input, Action resized) => terminal.Start(input, resized);
    public void Stop() => terminal.Stop();
    public void Write(string data) => terminal.Write(data);
}
