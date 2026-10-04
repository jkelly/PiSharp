using System.Collections.Immutable;

namespace PiSharp.Tui.Rendering;

/// <summary>Hard limits for the small viewport renderer, including active and waiting render calls.</summary>
public sealed record TerminalRenderLimits(int MaximumRows = 256, int MaximumColumns = 512,
    int MaximumCells = 65_536, int MaximumInputCharacters = 65_536, int MaximumFrameCharacters = 65_536,
    int MaximumGraphemeCharacters = 256, int MaximumPendingRenders = 8)
{
    internal void Validate()
    {
        if (MaximumRows is < 1 or > 1024 || MaximumColumns is < 1 or > 4096 ||
            MaximumCells is < 1 or > 262_144 || MaximumInputCharacters is < 1 or > 1_048_576 ||
            MaximumFrameCharacters is < 32 or > 1_048_576 || MaximumGraphemeCharacters is < 1 or > 4096 ||
            MaximumPendingRenders is < 1 or > 64)
            throw new TerminalRenderException(TerminalRenderFailure.InvalidOptions);
    }

    internal void ValidateViewport(int rows, int columns)
    {
        if (rows < 1 || rows > MaximumRows || columns < 1 || columns > MaximumColumns ||
            (long)rows * columns > MaximumCells)
            throw new TerminalRenderException(TerminalRenderFailure.ResourceLimit);
    }
}

public enum TerminalRenderFailure { InvalidOptions, ResourceLimit, InvalidUnicode, InvalidWidth, InvalidCursor }

public sealed class TerminalRenderException : Exception
{
    public TerminalRenderFailure Failure { get; }
    public TerminalRenderException(TerminalRenderFailure failure) : base(failure switch
    {
        TerminalRenderFailure.InvalidOptions => "Terminal renderer limits or width policy are invalid.",
        TerminalRenderFailure.ResourceLimit => "Terminal rendering exceeds its bounded viewport profile.",
        TerminalRenderFailure.InvalidUnicode => "Terminal text contains invalid UTF-16.",
        TerminalRenderFailure.InvalidWidth => "Terminal width policy returned an invalid cell width.",
        _ => "Terminal cursor is outside the viewport or inside a multi-cell grapheme."
    }) => Failure = failure;
}

/// <summary>Zero-based viewport coordinates. Unstyled layout rejects grapheme continuation cells.</summary>
public sealed record TerminalCursor(int Row = 0, int Column = 0, bool Visible = true);
public sealed record TerminalFrameRow(string Text, int CellWidth);

// These tokens have no public constructor or text-based authorization path.
internal readonly record struct TerminalInverseSpan(int Row, int StartUtf16, int LengthUtf16);

/// <summary>Immutable, control-free rows produced by the bounded Tui layout/factory.</summary>
public sealed class TerminalFrame
{
    public int Columns { get; }
    public ImmutableArray<TerminalFrameRow> Rows { get; }
    public TerminalCursor Cursor { get; }
    public string WidthPolicyId { get; }
    public bool IsClipped { get; }
    internal int TextCharacters { get; }
    internal int LargestGraphemeCharacters { get; }
    internal TerminalInverseSpan? InverseSpan { get; }
    internal bool PositionCursor { get; }
    internal bool IsSourcePresentation { get; }

    internal TerminalFrame(int columns, ImmutableArray<TerminalFrameRow> rows, TerminalCursor cursor,
        string widthPolicyId, bool isClipped, int textCharacters, int largestGraphemeCharacters,
        TerminalInverseSpan? inverseSpan = null, bool positionCursor = true, bool isSourcePresentation = false)
    {
        Columns = columns; Rows = rows; Cursor = cursor; WidthPolicyId = widthPolicyId;
        IsClipped = isClipped; TextCharacters = textCharacters; LargestGraphemeCharacters = largestGraphemeCharacters;
        InverseSpan = inverseSpan; PositionCursor = positionCursor; IsSourcePresentation = isSourcePresentation;
    }

    internal TerminalInverseSpan? StyleForRow(int row) => InverseSpan is { } span && span.Row == row ? span : null;
}

/// <summary>A trusted display-cell policy, separately qualified from grapheme segmentation.</summary>
public interface ITerminalWidthPolicy
{
    string Id { get; }
    int GetWidth(string grapheme);
}

public enum TerminalRenderKind { Unchanged, Full, Diff }
public sealed record TerminalRenderResult(TerminalRenderKind Kind, int WrittenCharacters, bool CacheCommitted);
public sealed record TerminalRendererSnapshot(int PendingRenders, bool CacheKnown, bool IsClosing,
    long PhysicalWritesStarted, long PhysicalWritesSettled);
