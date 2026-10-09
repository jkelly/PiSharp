// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/modes/interactive/components/armin.ts.
// Armin says hi! A fun easter egg with animated XBM art. Math.random is injectable and Step() runs one interval tick, so tests
// can drive the effects deterministically.
using PiSharp.Tui.Pi;
using static PiSharp.Cli.Interactive.Mode.ThemeGlobals;

namespace PiSharp.Cli.Interactive.Mode.Components;

/// <summary>armin.ts module members: the Armin bitmap (XBM, 31x36 pixels, LSB first, 1 = background, 0 = foreground).</summary>
internal static class Armin
{
    public const int ArminWidth = 31;
    public const int ArminHeight = 36;
    internal const int WIDTH = ArminWidth;
    internal const int HEIGHT = ArminHeight;

    private static readonly byte[] BITS =
    [
        0xff, 0xff, 0xff, 0x7f, 0xff, 0xf0, 0xff, 0x7f, 0xff, 0xed, 0xff, 0x7f, 0xff, 0xdb, 0xff, 0x7f, 0xff, 0xb7, 0xff,
        0x7f, 0xff, 0x77, 0xfe, 0x7f, 0x3f, 0xf8, 0xfe, 0x7f, 0xdf, 0xff, 0xfe, 0x7f, 0xdf, 0x3f, 0xfc, 0x7f, 0x9f, 0xc3,
        0xfb, 0x7f, 0x6f, 0xfc, 0xf4, 0x7f, 0xf7, 0x0f, 0xf7, 0x7f, 0xf7, 0xff, 0xf7, 0x7f, 0xf7, 0xff, 0xe3, 0x7f, 0xf7,
        0x07, 0xe8, 0x7f, 0xef, 0xf8, 0x67, 0x70, 0x0f, 0xff, 0xbb, 0x6f, 0xf1, 0x00, 0xd0, 0x5b, 0xfd, 0x3f, 0xec, 0x53,
        0xc1, 0xff, 0xef, 0x57, 0x9f, 0xfd, 0xee, 0x5f, 0x9f, 0xfc, 0xae, 0x5f, 0x1f, 0x78, 0xac, 0x5f, 0x3f, 0x00, 0x50,
        0x6c, 0x7f, 0x00, 0xdc, 0x77, 0xff, 0xc0, 0x3f, 0x78, 0xff, 0x01, 0xf8, 0x7f, 0xff, 0x03, 0x9c, 0x78, 0xff, 0x07,
        0x8c, 0x7c, 0xff, 0x0f, 0xce, 0x78, 0xff, 0xff, 0xcf, 0x7f, 0xff, 0xff, 0xcf, 0x78, 0xff, 0xff, 0xdf, 0x78, 0xff,
        0xff, 0xdf, 0x7d, 0xff, 0xff, 0x3f, 0x7e, 0xff, 0xff, 0xff, 0x7f,
    ];

    internal static readonly int BYTES_PER_ROW = (WIDTH + 7) / 8;
    /// <summary>Half-block rendering.</summary>
    internal static readonly int DISPLAY_HEIGHT = (HEIGHT + 1) / 2;

    /// <summary>Get pixel at (x, y): true = foreground, false = background.</summary>
    public static bool IsArminPixel(int x, int y)
    {
        if (y >= HEIGHT) return false;
        var byteIndex = y * BYTES_PER_ROW + (int)Math.Floor(x / 8.0);
        var bitIndex = x % 8;
        // JS reads undefined past the end (undefined >> n is 0, so the pixel is foreground).
        var value = byteIndex >= 0 && byteIndex < BITS.Length ? BITS[byteIndex] : 0;
        return ((value >> bitIndex) & 1) == 0;
    }

    /// <summary>Get the character for a cell (2 vertical pixels packed).</summary>
    internal static string GetChar(int x, int row)
    {
        var upper = IsArminPixel(x, row * 2);
        var lower = IsArminPixel(x, row * 2 + 1);
        if (upper && lower) return "█";
        if (upper) return "▀";
        if (lower) return "▄";
        return " ";
    }

    /// <summary>Build the final image grid.</summary>
    internal static string[][] BuildFinalGrid()
    {
        var grid = new string[DISPLAY_HEIGHT][];
        for (var row = 0; row < DISPLAY_HEIGHT; row++)
        {
            var line = new string[WIDTH];
            for (var x = 0; x < WIDTH; x++) line[x] = GetChar(x, row);
            grid[row] = line;
        }
        return grid;
    }
}

internal sealed class ArminComponent : IComponent, IDisposableComponent
{
    internal static readonly string[] EFFECTS = ["typewriter", "scanline", "rain", "fade", "crt", "glitch", "dissolve"];
    private static int WIDTH => Armin.WIDTH;
    private static int DISPLAY_HEIGHT => Armin.DISPLAY_HEIGHT;

    private sealed class Drop { public int Y; public int Settled; }

    private readonly ITui ui;
    private readonly Func<double> random;
    private IDisposable? interval;
    private readonly string effect;
    private readonly string[][] finalGrid;
    private string[][] currentGrid;
    // effectState, per effect.
    private int statePos, stateRow, stateExpansion, statePhase, stateGlitchFrames, stateIdx;
    private Drop[] stateDrops = [];
    private (int Row, int X)[] statePositions = [];
    private List<string> cachedLines = [];
    private int cachedWidth;
    private int gridVersion;
    private int cachedVersion = -1;

    public ArminComponent(ITui ui) : this(ui, null, null, true) { }

    /// <summary>Test seam: <paramref name="random"/> replaces Math.random, <paramref name="effect"/> skips the random pick,
    /// and <paramref name="startAnimation"/> false leaves the ticking to <see cref="Step"/>.</summary>
    internal ArminComponent(ITui ui, Func<double>? random, string? effect, bool startAnimation)
    {
        this.ui = ui;
        this.random = random ?? Random.Shared.NextDouble;
        this.effect = effect ?? EFFECTS[(int)Math.Floor(this.random() * EFFECTS.Length)];
        finalGrid = Armin.BuildFinalGrid();
        currentGrid = CreateEmptyGrid();

        InitEffect();
        if (startAnimation) StartAnimation();
    }

    internal string Effect => effect;
    internal bool IsAnimating => interval is not null;

    public void Invalidate() => cachedWidth = 0;

    public List<string> Render(int width)
    {
        if (width == cachedWidth && cachedVersion == gridVersion) return cachedLines;

        const int padding = 1;
        var availableWidth = width - padding;

        cachedLines = currentGrid.Select(row =>
        {
            // Clip row to available width before applying color
            var clipped = string.Concat(JsSlice(row, 0, availableWidth));
            var padRight = Math.Max(0, width - padding - clipped.Length);
            return " " + theme.Fg("accent", clipped) + new string(' ', padRight);
        }).ToList();

        // Add "ARMIN SAYS HI" at the end
        const string message = "ARMIN SAYS HI";
        var msgPadRight = Math.Max(0, width - padding - message.Length);
        cachedLines.Add(" " + theme.Fg("accent", message) + new string(' ', msgPadRight));

        cachedWidth = width;
        cachedVersion = gridVersion;

        return cachedLines;
    }

    private static string[][] CreateEmptyGrid() =>
        Enumerable.Range(0, DISPLAY_HEIGHT).Select(_ => Enumerable.Repeat(" ", WIDTH).ToArray()).ToArray();

    private (int Row, int X)[] ShuffledPositions()
    {
        var positions = new List<(int Row, int X)>();
        for (var row = 0; row < DISPLAY_HEIGHT; row++)
            for (var x = 0; x < WIDTH; x++) positions.Add((row, x));
        // Fisher-Yates shuffle
        for (var i = positions.Count - 1; i > 0; i--)
        {
            var j = (int)Math.Floor(random() * (i + 1));
            (positions[i], positions[j]) = (positions[j], positions[i]);
        }
        return [.. positions];
    }

    private void InitEffect()
    {
        switch (effect)
        {
            case "typewriter":
                statePos = 0;
                break;
            case "scanline":
                stateRow = 0;
                break;
            case "rain":
                // Track falling position for each column
                stateDrops = Enumerable.Range(0, WIDTH).Select(_ => new Drop { Y = -(int)Math.Floor(random() * DISPLAY_HEIGHT * 2), Settled = 0 }).ToArray();
                break;
            case "fade":
                statePositions = ShuffledPositions();
                stateIdx = 0;
                break;
            case "crt":
                stateExpansion = 0;
                break;
            case "glitch":
                statePhase = 0; stateGlitchFrames = 8;
                break;
            case "dissolve":
            {
                // Start with random noise
                string[] chars = [" ", "░", "▒", "▓", "█", "▀", "▄"];
                currentGrid = Enumerable.Range(0, DISPLAY_HEIGHT)
                    .Select(_ => Enumerable.Range(0, WIDTH).Select(_ => chars[(int)Math.Floor(random() * chars.Length)]).ToArray()).ToArray();
                // Shuffle positions for gradual resolve
                statePositions = ShuffledPositions();
                stateIdx = 0;
                break;
            }
        }
    }

    private void StartAnimation()
    {
        var fps = effect == "glitch" ? 60 : 30;
        interval = ui.Loop.SetInterval(() => Step(), 1000.0 / fps);
    }

    /// <summary>One interval tick: advance the effect, bump the grid version and request a render. Returns whether the effect finished.</summary>
    internal bool Step()
    {
        var done = TickEffect();
        UpdateDisplay();
        ui.RequestRender();
        if (done) StopAnimation();
        return done;
    }

    private void StopAnimation()
    {
        if (interval is not null)
        {
            interval.Dispose();
            interval = null;
        }
    }

    private bool TickEffect() => effect switch
    {
        "typewriter" => TickTypewriter(),
        "scanline" => TickScanline(),
        "rain" => TickRain(),
        "fade" => TickFade(),
        "crt" => TickCrt(),
        "glitch" => TickGlitch(),
        "dissolve" => TickDissolve(),
        _ => true
    };

    private bool TickTypewriter()
    {
        const int pixelsPerFrame = 3;
        for (var i = 0; i < pixelsPerFrame; i++)
        {
            var row = statePos / WIDTH;
            var x = statePos % WIDTH;
            if (row >= DISPLAY_HEIGHT) return true;
            currentGrid[row][x] = finalGrid[row][x];
            statePos++;
        }
        return false;
    }

    private bool TickScanline()
    {
        if (stateRow >= DISPLAY_HEIGHT) return true;

        // Copy row
        for (var x = 0; x < WIDTH; x++) currentGrid[stateRow][x] = finalGrid[stateRow][x];
        stateRow++;
        return false;
    }

    private bool TickRain()
    {
        var allSettled = true;
        currentGrid = CreateEmptyGrid();

        for (var x = 0; x < WIDTH; x++)
        {
            var drop = stateDrops[x];

            // Draw settled pixels
            for (var row = DISPLAY_HEIGHT - 1; row >= DISPLAY_HEIGHT - drop.Settled; row--)
                if (row >= 0) currentGrid[row][x] = finalGrid[row][x];

            // Check if this column is done
            if (drop.Settled >= DISPLAY_HEIGHT) continue;

            allSettled = false;

            // Find the target row for this column (lowest non-space pixel)
            var targetRow = -1;
            for (var row = DISPLAY_HEIGHT - 1 - drop.Settled; row >= 0; row--)
            {
                if (finalGrid[row][x] != " ")
                {
                    targetRow = row;
                    break;
                }
            }

            // Move drop down
            drop.Y++;

            // Draw falling drop
            if (drop.Y >= 0 && drop.Y < DISPLAY_HEIGHT)
            {
                if (targetRow >= 0 && drop.Y >= targetRow)
                {
                    // Settle
                    drop.Settled = DISPLAY_HEIGHT - targetRow;
                    drop.Y = -(int)Math.Floor(random() * 5) - 1;
                }
                else
                {
                    // Still falling
                    currentGrid[drop.Y][x] = "▓";
                }
            }
        }

        return allSettled;
    }

    private bool TickFade()
    {
        const int pixelsPerFrame = 15;
        for (var i = 0; i < pixelsPerFrame; i++)
        {
            if (stateIdx >= statePositions.Length) return true;
            var (row, x) = statePositions[stateIdx];
            currentGrid[row][x] = finalGrid[row][x];
            stateIdx++;
        }
        return false;
    }

    private bool TickCrt()
    {
        var midRow = DISPLAY_HEIGHT / 2;

        currentGrid = CreateEmptyGrid();

        // Draw from middle expanding outward
        var top = midRow - stateExpansion;
        var bottom = midRow + stateExpansion;

        for (var row = Math.Max(0, top); row <= Math.Min(DISPLAY_HEIGHT - 1, bottom); row++)
            for (var x = 0; x < WIDTH; x++) currentGrid[row][x] = finalGrid[row][x];

        stateExpansion++;
        return stateExpansion > DISPLAY_HEIGHT;
    }

    private bool TickGlitch()
    {
        if (statePhase < stateGlitchFrames)
        {
            // Glitch phase: show corrupted version
            currentGrid = finalGrid.Select(row =>
            {
                var offset = (int)Math.Floor(random() * 7) - 3;
                var glitchRow = row.ToArray();

                // Random horizontal offset
                if (random() < 0.3)
                {
                    var shifted = JsSlice(glitchRow, offset).Concat(JsSlice(glitchRow, 0, offset)).ToArray();
                    return JsSlice(shifted, 0, WIDTH);
                }

                // Random vertical swap
                if (random() < 0.2)
                {
                    var swapRow = (int)Math.Floor(random() * DISPLAY_HEIGHT);
                    return finalGrid[swapRow].ToArray();
                }

                return glitchRow;
            }).ToArray();
            statePhase++;
            return false;
        }

        // Final frame: show clean image
        currentGrid = finalGrid.Select(row => row.ToArray()).ToArray();
        return true;
    }

    private bool TickDissolve()
    {
        const int pixelsPerFrame = 20;
        for (var i = 0; i < pixelsPerFrame; i++)
        {
            if (stateIdx >= statePositions.Length) return true;
            var (row, x) = statePositions[stateIdx];
            currentGrid[row][x] = finalGrid[row][x];
            stateIdx++;
        }
        return false;
    }

    private void UpdateDisplay() => gridVersion++;

    public void Dispose() => StopAnimation();

    /// <summary>Array.prototype.slice: negative indices count from the end.</summary>
    private static string[] JsSlice(string[] array, int start, int? end = null)
    {
        var length = array.Length;
        var from = start < 0 ? Math.Max(length + start, 0) : Math.Min(start, length);
        var stop = end is { } e ? (e < 0 ? Math.Max(length + e, 0) : Math.Min(e, length)) : length;
        return from >= stop ? [] : array[from..stop];
    }
}
