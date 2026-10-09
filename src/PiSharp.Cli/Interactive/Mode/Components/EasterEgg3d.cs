// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/modes/interactive/components/easter-egg-3d.ts.
// Fullscreen 3D easter eggs: the pi logo (header logo click) and Armin (/arminsayshi). Both are bitmaps built from one block per
// pixel. The current screen dissolves into braille dust while the model spins in the center and its blocks play a sliding puzzle.
// Leaving plays the same timeline backwards, so the screen reassembles.
//
// The pi logo lifts off the header, flies to the center, and grows; the dust spreads out from the header logo. Armin grows out of a
// speck at the center; the dust spreads out from the center.
//
// The blocks are ray cast per braille dot. A braille cell holds 2x4 roughly square dots, so one pixel of a half-block bitmap is
// exactly 2x2 dots, and the header logo (4x2 cells) is 8x8 dots when it lifts off.
//
// JS numerics are kept: Float32Array/Uint8Array/Uint16Array buffers are float/byte/ushort arrays, Math.round is floor(x + 0.5),
// Math.hypot follows V8's compensated algorithm and hash() reproduces Math.imul/>>> on int32. performance.now() is injectable.
using System.Diagnostics;
using System.Text;
using PiSharp.Tui.Pi;
using static PiSharp.Cli.Interactive.Mode.ThemeGlobals;

namespace PiSharp.Cli.Interactive.Mode.Components;

/// <summary>Module-level members of easter-egg-3d.ts.</summary>
internal static class EasterEgg3d
{
    internal readonly record struct Rgb(double R, double G, double B)
    {
        public double this[int index] => index switch { 0 => R, 1 => G, _ => B };
    }

    internal sealed class ScreenCell
    {
        public string Text = "";
        /// <summary>1 or 2 for a grapheme, 0 for the second column of a wide grapheme.</summary>
        public int Width;
        public Rgb Fg;
        /// <summary>Null when the cell uses the terminal's default background.</summary>
        public Rgb? Bg;
        /// <summary>Seconds until the cell turns into braille dust.</summary>
        public double Delay;
        /// <summary>Number of braille dots the glyph turns into.</summary>
        public int Ink;
        public double Seed;
        public ScreenCell Clone() => (ScreenCell)MemberwiseClone();
    }

    internal sealed class Star
    {
        /// <summary>Braille character with the single dot the star occupies.</summary>
        public required string Glyph;
        /// <summary>Color the star leans toward; it only ever shows a small step from the background toward it.</summary>
        public Rgb Tint;
        /// <summary>Largest step from the background toward the tint, from 0 to 1.</summary>
        public double Strength;
        public double Phase;
        /// <summary>Flicker speed in radians per second.</summary>
        public double Speed;
    }

    internal sealed class Box
    {
        public required double[] Min;
        public required double[] Max;
        public Rgb Color;
    }

    internal sealed class Pose
    {
        public double CenterX;
        public double CenterY;
        /// <summary>Braille dots per model pixel at depth 0.</summary>
        public double Scale;
        public double Yaw;
        public double Pitch;
        public double Roll;
    }

    internal const double DEPTH = 0.7;
    internal const double FLY_START = 0.1;
    internal const double FLY_DURATION = 1.3;
    /// <summary>Braille dots per pixel when a model without an origin appears, growing from a speck at the center.</summary>
    internal const double START_SCALE = 0.1;

    /// <summary>Grid position of a block: column and row in the bitmap, and layer (-1, 0, 1) in depth.</summary>
    internal readonly record struct Cell3(int X, int Y, int Z);

    /// <summary>A bitmap built from one block per foreground pixel. Each is a block that the puzzle slides around.</summary>
    internal sealed class Model
    {
        /// <summary>Bitmap size in pixels.</summary>
        public int Columns;
        public int Rows;
        public required List<(Cell3 Home, Rgb Color)> Blocks;
        /// <summary>Camera distance from the model's center, in pixels.</summary>
        public double CameraDistance;
        /// <summary>Farthest distance of any block corner from the center, with blocks on the outer depth layers.</summary>
        public double Radius;
        /// <summary>Largest share of the screen width the spinning model covers.</summary>
        public double WidthShare;
        /// <summary>Number of blocks the puzzle moves per step, given a random number from 0 to 1.</summary>
        public required Func<double, int> PuzzleMoves;
        /// <summary>Top-left cell of the model's half-block rendering on screen; null to grow out of the center.</summary>
        public (int Column, int Row)? Origin;
    }

    internal static Model CreateModel(int columns, int rows, Func<int, int, Rgb?> pixel, double cameraDistance, double widthShare,
        (int Column, int Row)? origin, Func<double, int, int> puzzleMoves)
    {
        var blocks = new List<(Cell3 Home, Rgb Color)>();
        for (var row = 0; row < rows; row++)
            for (var column = 0; column < columns; column++)
                if (pixel(column, row) is { } color) blocks.Add((new Cell3(column, row, 0), color));
        return new Model
        {
            Columns = columns,
            Rows = rows,
            Blocks = blocks,
            CameraDistance = cameraDistance,
            Radius = Hypot(columns / 2.0, rows / 2.0, 1 + DEPTH / 2),
            WidthShare = widthShare,
            PuzzleMoves = random => puzzleMoves(random, blocks.Count),
            Origin = origin,
        };
    }

    internal static readonly Rgb CORAL = new(228, 138, 122);
    internal static readonly Rgb BLUE = new(79, 142, 179);
    internal static readonly Rgb YELLOW = new(234, 182, 93);
    internal static readonly string[] PI_LOGO_PIXELS = ["ccc.", "b.c.", "bb.y", "b..y"];

    private static Rgb? PiLogoColor(char key) => key switch { 'c' => CORAL, 'b' => BLUE, 'y' => YELLOW, _ => null };

    internal static Model PiLogoModel((int Column, int Row) origin) =>
        CreateModel(4, 4, (column, row) => PiLogoColor(PI_LOGO_PIXELS[row][column]), cameraDistance: 10, widthShare: 0.35, origin: origin,
            puzzleMoves: (random, _) => random < 0.4 ? 2 : 1);

    internal static Model ArminModel(Rgb color) =>
        CreateModel(Armin.ArminWidth, Armin.ArminHeight, (column, row) => Armin.IsArminPixel(column, row) ? color : null,
            // Far enough that the perspective stays mild for a figure about 36 blocks tall.
            cameraDistance: 80, widthShare: 0.45, origin: null,
            puzzleMoves: (random, blockCount) => (int)JsRound(blockCount * (0.03 + random * 0.04)));

    // The sliding puzzle: after the model spun for a while, blocks slide into free neighboring cells, a few at a time.
    // Each cycle shuffles, flies every block back home, and holds the model briefly.
    internal const double PUZZLE_START = FLY_START + FLY_DURATION + 3;
    internal const double PUZZLE_STEP = 0.3;
    internal const int PUZZLE_STEPS = 12;
    internal const double PUZZLE_RETURN = 1;
    internal const double PUZZLE_HOLD = 0.6;
    internal const double PUZZLE_CYCLE = PUZZLE_STEP * PUZZLE_STEPS + PUZZLE_RETURN + PUZZLE_HOLD;
    private static readonly Cell3[] PUZZLE_MOVES =
    [
        new(1, 0, 0), new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0), new(1, 0, 0),
        new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0), new(0, 0, 1), new(0, 0, -1),
    ];

    /// <summary>Block positions after each step of one shuffle cycle, starting at home. Deterministic per cycle.</summary>
    internal static List<Cell3[]> ShuffleSteps(Model model, int cycle)
    {
        var columns = model.Columns;
        var rows = model.Rows;
        var positions = model.Blocks.Select(block => block.Home).ToArray();
        var steps = new List<Cell3[]> { positions };
        var lastMoved = new HashSet<int>();
        double random = cycle * 7_919 + 1;
        double Next() => Hash(random++);
        int Key(Cell3 cell) => ((cell.Z + 1) * rows + cell.Y) * columns + cell.X;
        for (var step = 0; step < PUZZLE_STEPS; step++)
        {
            var occupied = new HashSet<int>(positions.Select(Key));
            var nextPositions = positions.ToArray();
            var moved = new List<int>();
            var movedSet = new HashSet<int>();
            var moveCount = model.PuzzleMoves(Next());
            for (var move = 0; move < moveCount; move++)
            {
                var candidates = new List<(int Block, Cell3 Target)>();
                for (var block = 0; block < positions.Length; block++)
                {
                    if (movedSet.Contains(block)) continue;
                    var (x, y, z) = positions[block];
                    foreach (var (dx, dy, dz) in PUZZLE_MOVES)
                    {
                        var target = new Cell3(x + dx, y + dy, z + dz);
                        if (target.X < 0 || target.X >= columns || target.Y < 0 || target.Y >= rows) continue;
                        if (target.Z < -1 || target.Z > 1 || occupied.Contains(Key(target))) continue;
                        candidates.Add((block, target));
                    }
                }
                // Prefer blocks that did not just move, so the puzzle does not look like one block jittering.
                var fresh = candidates.Where(candidate => !lastMoved.Contains(candidate.Block)).ToList();
                var pool = fresh.Count > 0 ? fresh : candidates;
                var index = (int)Math.Floor(Next() * pool.Count);
                if (index >= pool.Count) break;
                var choice = pool[index];
                occupied.Add(Key(choice.Target));
                nextPositions[choice.Block] = choice.Target;
                if (movedSet.Add(choice.Block)) moved.Add(choice.Block);
            }
            lastMoved.Clear();
            foreach (var block in moved) lastMoved.Add(block);
            positions = nextPositions;
            steps.Add(positions);
        }
        return steps;
    }

    internal const double FRAME_MS = 1000.0 / 30;
    internal const double DUST_DURATION = 0.35;
    internal const double WAVE_SPREAD = 0.55;
    internal const double WAVE_JITTER = 0.12;
    internal const double DISSOLVE_END = FLY_START + WAVE_SPREAD + WAVE_JITTER + DUST_DURATION;
    internal const double BACKGROUND_FADE = 0.5;
    internal const double EXIT_DURATION = 1.1;
    // A faint starfield fades in behind the logo once the screen has dissolved.
    internal const double STARS_START = 2.5;
    internal const double STARS_FADE = 2;
    internal const double STAR_DENSITY = 0.018;
    // Star tints: neutral, pale blue, pale gold, and pale rose, each mixed half with the terminal's foreground.
    internal static readonly Rgb[] STAR_TINTS = [new(255, 255, 255), new(170, 195, 255), new(255, 225, 170), new(255, 190, 205)];
    internal const int LOGO_COLOR_STEP = 5;
    // On light backgrounds most of each braille cell shows the bright background, which washes the logo out. There, the logo gets
    // a halo: its colors and coverage blurred into the cell backgrounds, strongest under the logo and fading out over HALO_RADIUS_X
    // columns and HALO_RADIUS_Y rows (cells are about twice as tall as wide).
    internal const double LIGHT_HALO_STRENGTH = 0.3;
    internal const int HALO_RADIUS_X = 6;
    internal const int HALO_RADIUS_Y = 3;
    internal const int HALO_LEVELS = 32;
    internal const int STAR_ALPHA_LEVELS = 8;
    internal const int STAR_FLICKER_LEVELS = 3;
    internal const double HINT_FADE = 0.5;

    /// <summary>Braille dot bits, indexed by row * 2 + column within the 2x4 cell.</summary>
    internal static readonly int[] DOT_BITS = [0x01, 0x08, 0x02, 0x10, 0x04, 0x20, 0x40, 0x80];
    internal static readonly string[] BRAILLE = Enumerable.Range(0, 256).Select(bits => ((char)(0x2800 + bits)).ToString()).ToArray();
    internal static readonly double[] LIGHT = Normalize([-0.45, -0.6, 0.75]);
    internal static readonly double[] HALF_VECTOR = Normalize([LIGHT[0], LIGHT[1], LIGHT[2] + 1]);

    internal static double[] Normalize(double[] v)
    {
        var length = Hypot(v[0], v[1], v[2]);
        return [v[0] / length, v[1] / length, v[2] / length];
    }

    internal static double Clamp01(double value) => value < 0 ? 0 : value > 1 ? 1 : value;

    /// <summary>Quintic smoothstep.</summary>
    internal static double Smooth(double value)
    {
        var t = Clamp01(value);
        return t * t * t * (t * (t * 6 - 15) + 10);
    }

    internal static double EaseOut(double value)
    {
        var t = 1 - Clamp01(value);
        return 1 - t * t * t;
    }

    internal static Rgb Mix(Rgb a, Rgb b, double amount) =>
        new(a.R + (b.R - a.R) * amount, a.G + (b.G - a.G) * amount, a.B + (b.B - a.B) * amount);

    internal static double Hash(double value)
    {
        unchecked
        {
            var x = (ToInt32(value) ^ (int)0x9e3779b9) * (int)0x85ebca6b;
            x ^= (int)((uint)x >> 13);
            x *= (int)0xc2b2ae35;
            x ^= (int)((uint)x >> 16);
            return (uint)x / 4294967296.0;
        }
    }

    /// <summary>Whether a background is light, by relative luminance.</summary>
    internal static bool IsLight(Rgb color) => 0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B > 128;

    internal static Rgb ToRgb(Color color)
    {
        var (r, g, b) = Colors.ToRgb(color);
        return new(r, g, b);
    }

    /// <summary>Braille dots a glyph turns into, a rough measure of how much ink it has.</summary>
    internal static int GlyphInk(string text, double seed)
    {
        if (TextUtils.JsTrim(text) == "") return 0;
        if (text.Length == 1 && ".,:;'`-_·".Contains(text[0])) return 2;
        if (text.Length == 1 && text[0] >= '─' && text[0] <= '╿') return 3;
        return 4 + (int)Math.Floor(seed * 3);
    }

    /// <summary>Parse rendered lines into cells with resolved colors. Other escape sequences (OSC, APC, cursor) are skipped.</summary>
    internal static List<List<ScreenCell>> ParseScreen(IReadOnlyList<string> lines, int width, Rgb foreground, Rgb background)
    {
        static Rgb Palette(double index)
        {
            if (double.IsNaN(index) || index != Math.Floor(index) || index < 0 || index > 255)
                throw new ArgumentException($"ANSI color index must be an integer from 0 to 255: {JsNumber(index)}");
            return ToRgb(Color.Indexed((int)index));
        }
        return lines.Select(line =>
        {
            var cells = new List<ScreenCell>();
            Rgb? fg = null;
            Rgb? bg = null;
            var dim = false;
            var inverse = false;
            void ApplySgr(string parameters)
            {
                var codes = parameters == "" ? ["0"] : parameters.Split(';');
                for (var i = 0; i < codes.Length; i++)
                {
                    var parts = codes[i].Split(':').Select(JsParseInt).ToArray();
                    var code = parts[0];
                    if (code == 38 || code == 48)
                    {
                        // Extended color, either as `38;5;n` / `38;2;r;g;b` or colon sub-parameters.
                        double[] args;
                        if (parts.Length > 1)
                        {
                            // `38:2::r:g:b` has an empty color space id, dropped here.
                            args = parts.Skip(1).Where(part => !double.IsNaN(part)).ToArray();
                        }
                        else
                        {
                            var kind = JsParseInt(i + 1 < codes.Length ? codes[i + 1] : "");
                            var count = kind == 5 ? 2 : kind == 2 ? 4 : 1;
                            args = codes.Skip(i + 1).Take(count).Select(JsParseInt).ToArray();
                            i += count;
                        }
                        Rgb? color = null;
                        if (args.Length > 0 && args[0] == 5 && args.Length > 1) color = Palette(args[1]);
                        else if (args.Length > 0 && args[0] == 2 && args.Length >= 4) color = new Rgb(args[1], args[2], args[3]);
                        if (color is { } resolved)
                        {
                            if (code == 38) fg = resolved;
                            else bg = resolved;
                        }
                    }
                    else if (code == 0)
                    {
                        fg = null;
                        bg = null;
                        dim = false;
                        inverse = false;
                    }
                    else if (code == 2) dim = true;
                    else if (code == 22) dim = false;
                    else if (code == 7) inverse = true;
                    else if (code == 27) inverse = false;
                    else if (code >= 30 && code <= 37) fg = Palette(code - 30);
                    else if (code >= 90 && code <= 97) fg = Palette(code - 90 + 8);
                    else if (code == 39) fg = null;
                    else if (code >= 40 && code <= 47) bg = Palette(code - 40);
                    else if (code >= 100 && code <= 107) bg = Palette(code - 100 + 8);
                    else if (code == 49) bg = null;
                }
            }
            void PushText(string text)
            {
                foreach (var segment in TextUtils.Graphemes(text))
                {
                    var glyphWidth = TextUtils.VisibleWidth(segment);
                    if (glyphWidth == 0 || cells.Count + glyphWidth > width) continue;
                    var cellFg = fg ?? foreground;
                    var cellBg = bg;
                    if (inverse)
                    {
                        cellFg = bg ?? background;
                        cellBg = fg ?? foreground;
                    }
                    if (dim) cellFg = Mix(cellFg, cellBg ?? background, 0.4);
                    var cell = new ScreenCell { Text = segment, Width = glyphWidth, Fg = cellFg, Bg = cellBg };
                    cells.Add(cell);
                    if (glyphWidth == 2)
                    {
                        var second = cell.Clone();
                        second.Text = "";
                        second.Width = 0;
                        cells.Add(second);
                    }
                }
            }
            var i = 0;
            while (i < line.Length)
            {
                var sequenceStart = line.IndexOf('\u001b', i);
                if (sequenceStart == -1)
                {
                    PushText(line[i..]);
                    break;
                }
                if (sequenceStart > i) PushText(line[i..sequenceStart]);
                char? kind = sequenceStart + 1 < line.Length ? line[sequenceStart + 1] : null;
                if (kind == '[')
                {
                    var end = sequenceStart + 2;
                    while (end < line.Length && (line[end] < 0x40 || line[end] > 0x7e)) end++;
                    if (end < line.Length && line[end] == 'm') ApplySgr(line[(sequenceStart + 2)..end]);
                    i = end + 1;
                }
                else if (kind is ']' or '_' or 'P' or '^')
                {
                    // String sequences end at BEL or ST (ESC \).
                    var bell = line.IndexOf('\u0007', sequenceStart + 2 > line.Length ? line.Length : sequenceStart + 2);
                    var st = line.IndexOf("\u001b\\", sequenceStart + 2 > line.Length ? line.Length : sequenceStart + 2, StringComparison.Ordinal);
                    if (bell == -1 && st == -1) break;
                    i = bell != -1 && (st == -1 || bell < st) ? bell + 1 : st + 2;
                }
                else
                {
                    i = sequenceStart + 2;
                }
            }
            return cells;
        }).ToList();
    }

    /// <summary>Row-major 3x3 rotation matrix for yaw (y), then pitch (x), then roll (z).</summary>
    internal static double[] Rotation(double yaw, double pitch, double roll)
    {
        double sy = Math.Sin(yaw), cy = Math.Cos(yaw), sx = Math.Sin(pitch), cx = Math.Cos(pitch), sz = Math.Sin(roll), cz = Math.Cos(roll);
        // Rz * Rx * Ry
        double[] ry = [cy, 0, sy, 0, 1, 0, -sy, 0, cy];
        double[] rx = [1, 0, 0, 0, cx, -sx, 0, sx, cx];
        double[] rz = [cz, -sz, 0, sz, cz, 0, 0, 0, 1];
        return Multiply(rz, Multiply(rx, ry));
    }

    internal static double[] Multiply(double[] a, double[] b)
    {
        var result = new double[9];
        for (var row = 0; row < 3; row++)
            for (var column = 0; column < 3; column++)
                result[row * 3 + column] = a[row * 3] * b[column] + a[row * 3 + 1] * b[3 + column] + a[row * 3 + 2] * b[6 + column];
        return result;
    }

    // The spin is locked to the puzzle: one turn per cycle, facing the camera in the middle of each hold.
    internal const double SPIN_RAMP = 1.4;
    internal const double SPIN_SPEED = Math.PI * 2 / PUZZLE_CYCLE;
    // How much the spin slows down while facing the camera (and speeds up while facing away), from 0 to 1.
    internal const double SPIN_LINGER = 0.6;
    internal const double FIRST_FRONT_VIEW = PUZZLE_START + PUZZLE_STEP * PUZZLE_STEPS + PUZZLE_RETURN + PUZZLE_HOLD / 2;

    /// <summary>Uniform spin that accelerates from rest over SPIN_RAMP, then turns at SPIN_SPEED.</summary>
    internal static double BaseSpinPhase(double time)
    {
        var elapsed = time - FLY_START;
        if (elapsed <= 0) return 0;
        var u = elapsed / SPIN_RAMP;
        // Integral of the quintic smoothstep ramp.
        if (u < 1) return SPIN_SPEED * SPIN_RAMP * (Math.Pow(u, 6) - 3 * Math.Pow(u, 5) + 2.5 * Math.Pow(u, 4));
        return SPIN_SPEED * (SPIN_RAMP * 0.5 + elapsed - SPIN_RAMP);
    }

    /// <summary>Extra rotation added during the flight, so the phase is a whole number of turns at every assembled front view.</summary>
    internal static readonly double SPIN_ALIGNMENT = Math.PI * 2 - BaseSpinPhase(FIRST_FRONT_VIEW) % (Math.PI * 2);

    /// <summary>Spin phase: whole turns exactly when the reassembled logo faces the camera.</summary>
    internal static double SpinPhase(double time) => BaseSpinPhase(time) + SPIN_ALIGNMENT * Smooth((time - FLY_START) / FLY_DURATION);

    /// <summary>Yaw of the logo. It matches the phase at whole turns but lingers there, so the logo reads from the front.</summary>
    internal static double SpinAngle(double time)
    {
        var phase = SpinPhase(time);
        return phase - SPIN_LINGER * Math.Sin(phase);
    }

    internal sealed class Face
    {
        public int Axis;
        /// <summary>Plane coordinate on <see cref="Axis"/> in object space.</summary>
        public double Plane;
        public double UMin, UMax, VMin, VMax;
        /// <summary>Projected bounds in braille dots, inclusive.</summary>
        public int MinX, MinY, MaxX, MaxY;
        public double Red, Green, Blue;
    }

    /// <summary>
    /// Renders the model's blocks into braille cells. Only faces that point at the camera and are not covered by a touching block
    /// are drawn. Each face is rasterized over its projected bounds by intersecting each dot's ray with the face's plane, with a
    /// depth buffer resolving overlaps. Buffers are reused between frames.
    /// </summary>
    internal sealed class BlockRaster
    {
        /// <summary>Braille dot bits per cell.</summary>
        public byte[] Bits = [];
        /// <summary>Lit dots per cell.</summary>
        public byte[] Counts = [];
        /// <summary>Summed RGB of the lit dots per cell.</summary>
        public float[] RgbSum = [];
        /// <summary>Halo tint strength per cell, from 0 to 1. Only set when a halo was requested.</summary>
        public float[] HaloAmount = [];
        /// <summary>Halo color per cell.</summary>
        public float[] HaloRgb = [];
        private int width;
        private int height;
        /// <summary>Ray parameter of the nearest hit per dot; smaller is nearer.</summary>
        private float[] depth = [];
        /// <summary>Face index + 1 of the nearest hit per dot, 0 for none.</summary>
        private ushort[] faceIds = [];
        /// <summary>Cells written by the previous frame, cleared before the next one.</summary>
        private (int MinX, int MinY, int MaxX, int MaxY)? dirty;
        /// <summary>Cells with a halo from the previous frame, cleared before the next one.</summary>
        private (int MinX, int MinY, int MaxX, int MaxY)? haloDirty;
        private readonly double cameraDistance;

        public BlockRaster(double cameraDistance) => this.cameraDistance = cameraDistance;

        public void Render(int width, int height, Pose pose, IReadOnlyList<Box> boxes, Rgb background, double haloStrength)
        {
            var dotWidth = width * 2;
            var dotHeight = height * 4;
            if (width != this.width || height != this.height)
            {
                this.width = width;
                this.height = height;
                Bits = new byte[width * height];
                Counts = new byte[width * height];
                RgbSum = new float[width * height * 3];
                HaloAmount = new float[width * height];
                HaloRgb = new float[width * height * 3];
                haloDirty = null;
                this.depth = new float[dotWidth * dotHeight];
                Array.Fill(this.depth, float.PositiveInfinity);
                this.faceIds = new ushort[dotWidth * dotHeight];
                dirty = null;
            }
            else if (dirty is { } previous)
            {
                for (var row = previous.MinY; row <= previous.MaxY; row++)
                {
                    Array.Clear(Bits, row * width + previous.MinX, previous.MaxX - previous.MinX + 1);
                    Array.Clear(Counts, row * width + previous.MinX, previous.MaxX - previous.MinX + 1);
                    Array.Clear(RgbSum, (row * width + previous.MinX) * 3, (previous.MaxX - previous.MinX + 1) * 3);
                }
                dirty = null;
            }
            if (haloDirty is { } previousHalo)
            {
                for (var row = previousHalo.MinY; row <= previousHalo.MaxY; row++)
                {
                    Array.Clear(HaloAmount, row * width + previousHalo.MinX, previousHalo.MaxX - previousHalo.MinX + 1);
                    Array.Clear(HaloRgb, (row * width + previousHalo.MinX) * 3, (previousHalo.MaxX - previousHalo.MinX + 1) * 3);
                }
                haloDirty = null;
            }

            var m = Rotation(pose.Yaw, pose.Pitch, pose.Roll);
            var centerX = pose.CenterX;
            var centerY = pose.CenterY;
            var scale = pose.Scale;
            var cameraDistance = this.cameraDistance;
            // The camera sits at (0, 0, cameraDistance) in camera space; object space is the transpose rotation.
            double[] origin = [m[6] * cameraDistance, m[7] * cameraDistance, m[8] * cameraDistance];
            var light = IsLight(background);
            var faces = VisibleFaces(m, origin, pose, boxes, dotWidth, dotHeight, light);
            if (faces.Count == 0) return;
            var minX = dotWidth;
            var minY = dotHeight;
            var maxX = -1;
            var maxY = -1;
            foreach (var face in faces)
            {
                minX = Math.Min(minX, face.MinX);
                minY = Math.Min(minY, face.MinY);
                maxX = Math.Max(maxX, face.MaxX);
                maxY = Math.Max(maxY, face.MaxY);
            }
            if (maxX < minX || maxY < minY) return;

            var depth = this.depth;
            var faceIds = this.faceIds;
            for (var faceIndex = 0; faceIndex < faces.Count; faceIndex++)
            {
                var face = faces[faceIndex];
                var a = face.Axis;
                var u = (a + 1) % 3;
                var v = (a + 2) % 3;
                var originA = origin[a];
                var originU = origin[u];
                var originV = origin[v];
                // The ray direction in object space is linear in the dot position, so it is stepped per dot.
                var stepA = m[a] / scale;
                var stepU = m[u] / scale;
                var stepV = m[v] / scale;
                var sx = (face.MinX + 0.5 - centerX) / scale;
                for (var dotY = face.MinY; dotY <= face.MaxY; dotY++)
                {
                    var sy = (dotY + 0.5 - centerY) / scale;
                    var directionA = m[a] * sx + m[3 + a] * sy - m[6 + a] * cameraDistance;
                    var directionU = m[u] * sx + m[3 + u] * sy - m[6 + u] * cameraDistance;
                    var directionV = m[v] * sx + m[3 + v] * sy - m[6 + v] * cameraDistance;
                    var index = dotY * dotWidth + face.MinX;
                    for (var dotX = face.MinX; dotX <= face.MaxX; dotX++, index++)
                    {
                        var t = (face.Plane - originA) / directionA;
                        if (t > 0 && t < depth[index])
                        {
                            var hitU = originU + t * directionU;
                            var hitV = originV + t * directionV;
                            if (hitU >= face.UMin && hitU <= face.UMax && hitV >= face.VMin && hitV <= face.VMax)
                            {
                                depth[index] = (float)t;
                                faceIds[index] = (ushort)(faceIndex + 1);
                            }
                        }
                        directionA += stepA;
                        directionU += stepU;
                        directionV += stepV;
                    }
                }
            }

            // Shade the hit dots, pack them into braille cells, and reset the dot buffers for the next frame.
            var bits = Bits;
            var counts = Counts;
            var rgb = RgbSum;
            var cellMinX = width;
            var cellMinY = height;
            var cellMaxX = -1;
            var cellMaxY = -1;
            for (var dotY = minY; dotY <= maxY; dotY++)
            {
                var index = dotY * dotWidth + minX;
                for (var dotX = minX; dotX <= maxX; dotX++, index++)
                {
                    int id = faceIds[index];
                    if (id == 0) continue;
                    var face = faces[id - 1];
                    // Points farther from the camera fade slightly toward the background for depth.
                    var fog = Clamp01(0.15 - cameraDistance * (1 - (double)depth[index]) * 0.12) * (light ? 0.5 : 1);
                    faceIds[index] = 0;
                    depth[index] = float.PositiveInfinity;
                    var cellX = dotX >> 1;
                    var cellY = dotY >> 2;
                    var cell = cellY * width + cellX;
                    bits[cell] = (byte)(bits[cell] | DOT_BITS[(dotY & 3) * 2 + (dotX & 1)]);
                    counts[cell] = (byte)(counts[cell] + 1);
                    rgb[cell * 3] = (float)(rgb[cell * 3] + (face.Red + (background.R - face.Red) * fog));
                    rgb[cell * 3 + 1] = (float)(rgb[cell * 3 + 1] + (face.Green + (background.G - face.Green) * fog));
                    rgb[cell * 3 + 2] = (float)(rgb[cell * 3 + 2] + (face.Blue + (background.B - face.Blue) * fog));
                    if (cellX < cellMinX) cellMinX = cellX;
                    if (cellX > cellMaxX) cellMaxX = cellX;
                    if (cellY < cellMinY) cellMinY = cellY;
                    if (cellY > cellMaxY) cellMaxY = cellY;
                }
            }
            if (cellMaxX >= 0) dirty = (cellMinX, cellMinY, cellMaxX, cellMaxY);
            if (haloStrength > 0) RenderHalo(haloStrength);
        }

        /// <summary>Blur the logo's coverage and color over neighboring cells with a separable tent filter, so the tint falls off
        /// smoothly past the logo's edges.</summary>
        private void RenderHalo(double strength)
        {
            if (dirty is not { } cells) return;
            var minX = Math.Max(0, cells.MinX - HALO_RADIUS_X);
            var maxX = Math.Min(width - 1, cells.MaxX + HALO_RADIUS_X);
            var minY = Math.Max(0, cells.MinY - HALO_RADIUS_Y);
            var maxY = Math.Min(height - 1, cells.MaxY + HALO_RADIUS_Y);
            var regionWidth = maxX - minX + 1;
            static double[] Tent(int radius)
            {
                var weights = Enumerable.Range(0, radius * 2 + 1).Select(i => (double)(radius + 1 - Math.Abs(i - radius))).ToArray();
                var total = weights.Aggregate(0.0, (sum, weight) => sum + weight);
                return weights.Select(weight => weight / total).ToArray();
            }
            var weightsX = Tent(HALO_RADIUS_X);
            var weightsY = Tent(HALO_RADIUS_Y);

            // Horizontal pass over the rows that contain the logo: coverage and premultiplied color per cell.
            var rows = cells.MaxY - cells.MinY + 1;
            var horizontal = new float[rows * regionWidth * 4];
            for (var y = cells.MinY; y <= cells.MaxY; y++)
            {
                for (var x = minX; x <= maxX; x++)
                {
                    double coverage = 0, red = 0, green = 0, blue = 0;
                    for (var dx = -HALO_RADIUS_X; dx <= HALO_RADIUS_X; dx++)
                    {
                        var sourceX = x + dx;
                        if (sourceX < cells.MinX || sourceX > cells.MaxX) continue;
                        var source = y * width + sourceX;
                        int count = Counts[source];
                        if (count == 0) continue;
                        var weight = weightsX[dx + HALO_RADIUS_X] / 8;
                        coverage += count * weight;
                        red += RgbSum[source * 3] * weight;
                        green += RgbSum[source * 3 + 1] * weight;
                        blue += RgbSum[source * 3 + 2] * weight;
                    }
                    var target = ((y - cells.MinY) * regionWidth + (x - minX)) * 4;
                    horizontal[target] = (float)coverage;
                    horizontal[target + 1] = (float)red;
                    horizontal[target + 2] = (float)green;
                    horizontal[target + 3] = (float)blue;
                }
            }

            // Vertical pass into the halo buffers.
            for (var y = minY; y <= maxY; y++)
            {
                for (var x = minX; x <= maxX; x++)
                {
                    double coverage = 0, red = 0, green = 0, blue = 0;
                    for (var dy = -HALO_RADIUS_Y; dy <= HALO_RADIUS_Y; dy++)
                    {
                        var sourceY = y + dy;
                        if (sourceY < cells.MinY || sourceY > cells.MaxY) continue;
                        var weight = weightsY[dy + HALO_RADIUS_Y];
                        var source = ((sourceY - cells.MinY) * regionWidth + (x - minX)) * 4;
                        coverage += horizontal[source] * weight;
                        red += horizontal[source + 1] * weight;
                        green += horizontal[source + 2] * weight;
                        blue += horizontal[source + 3] * weight;
                    }
                    var target = y * width + x;
                    // Cells under the logo keep at least their own coverage, so faces stay solidly tinted.
                    int count = Counts[target];
                    if (count > 0 && count / 8.0 > coverage)
                    {
                        coverage = count / 8.0;
                        red = RgbSum[target * 3] / (double)count * coverage;
                        green = RgbSum[target * 3 + 1] / (double)count * coverage;
                        blue = RgbSum[target * 3 + 2] / (double)count * coverage;
                    }
                    if (coverage <= 0) continue;
                    // Quantized so neighboring cells usually share a background and its escape sequence.
                    var amount = JsRound(strength * Math.Pow(Math.Min(1, coverage), 0.7) * HALO_LEVELS) / HALO_LEVELS;
                    if (amount <= 0) continue;
                    HaloAmount[target] = (float)amount;
                    HaloRgb[target * 3] = (float)(JsRound(red / coverage / LOGO_COLOR_STEP) * LOGO_COLOR_STEP);
                    HaloRgb[target * 3 + 1] = (float)(JsRound(green / coverage / LOGO_COLOR_STEP) * LOGO_COLOR_STEP);
                    HaloRgb[target * 3 + 2] = (float)(JsRound(blue / coverage / LOGO_COLOR_STEP) * LOGO_COLOR_STEP);
                }
            }
            haloDirty = (minX, minY, maxX, maxY);
        }

        internal List<Face> VisibleFaces(double[] m, double[] origin, Pose pose, IReadOnlyList<Box> boxes, int dotWidth, int dotHeight,
            bool lightBackground)
        {
            var faces = new List<Face>();
            var cameraDistance = this.cameraDistance;
            foreach (var box in boxes)
            {
                for (var a = 0; a < 3; a++)
                {
                    var u = (a + 1) % 3;
                    var v = (a + 2) % 3;
                    foreach (var side in (ReadOnlySpan<int>)[-1, 1])
                    {
                        var plane = side > 0 ? box.Max[a] : box.Min[a];
                        // Back faces point away from the camera.
                        if ((origin[a] - plane) * side <= 0) continue;
                        // Faces pressed against a neighboring block are hidden. Positions are exact while blocks rest.
                        var covered = false;
                        foreach (var other in boxes)
                        {
                            if (other != box &&
                                (side > 0 ? other.Min[a] : other.Max[a]) == plane &&
                                other.Min[u] <= box.Min[u] &&
                                other.Max[u] >= box.Max[u] &&
                                other.Min[v] <= box.Min[v] &&
                                other.Max[v] >= box.Max[v])
                            {
                                covered = true;
                                break;
                            }
                        }
                        if (covered) continue;

                        var minX = double.PositiveInfinity;
                        var minY = double.PositiveInfinity;
                        var maxX = double.NegativeInfinity;
                        var maxY = double.NegativeInfinity;
                        var corner = new double[3];
                        foreach (var cornerU in (ReadOnlySpan<double>)[box.Min[u], box.Max[u]])
                        {
                            foreach (var cornerV in (ReadOnlySpan<double>)[box.Min[v], box.Max[v]])
                            {
                                corner[a] = plane;
                                corner[u] = cornerU;
                                corner[v] = cornerV;
                                var x = m[0] * corner[0] + m[1] * corner[1] + m[2] * corner[2];
                                var y = m[3] * corner[0] + m[4] * corner[1] + m[5] * corner[2];
                                var z = m[6] * corner[0] + m[7] * corner[1] + m[8] * corner[2];
                                var perspective = pose.Scale * cameraDistance / (cameraDistance - z);
                                var screenX = pose.CenterX + x * perspective;
                                var screenY = pose.CenterY + y * perspective;
                                minX = JsMin(minX, screenX);
                                minY = JsMin(minY, screenY);
                                maxX = JsMax(maxX, screenX);
                                maxY = JsMax(maxY, screenY);
                            }
                        }
                        var faceMinX = JsMax(0, Math.Floor(minX));
                        var faceMinY = JsMax(0, Math.Floor(minY));
                        var faceMaxX = JsMin(dotWidth - 1, Math.Ceiling(maxX));
                        var faceMaxY = JsMin(dotHeight - 1, Math.Ceiling(maxY));
                        // NaN bounds fail both comparisons in JS and the face is rasterized over nothing.
                        if (faceMaxX < faceMinX || faceMaxY < faceMinY) continue;
                        if (double.IsNaN(faceMinX) || double.IsNaN(faceMinY) || double.IsNaN(faceMaxX) || double.IsNaN(faceMaxY)) continue;

                        // Faces are flat, so lighting is computed once per face. The normal in camera space is a column of the
                        // rotation matrix.
                        var nx = m[a] * side;
                        var ny = m[3 + a] * side;
                        var nz = m[6 + a] * side;
                        var diffuse = Math.Max(0, nx * LIGHT[0] + ny * LIGHT[1] + nz * LIGHT[2]);
                        var rim = Math.Max(0, nx * 0.8 - nz * 0.3);
                        // On light backgrounds, highlights toward white would vanish, so faces only get darker than the brand
                        // colors there.
                        var specular = lightBackground
                            ? 0
                            : Math.Pow(Math.Max(0, nx * HALF_VECTOR[0] + ny * HALF_VECTOR[1] + nz * HALF_VECTOR[2]), 24) * 0.6 * 255;
                        var light = lightBackground
                            ? Math.Min(1, 0.55 + diffuse * 0.45 + rim * 0.1)
                            : 0.45 + diffuse * 0.78 + rim * 0.25;
                        faces.Add(new Face
                        {
                            Axis = a,
                            Plane = plane,
                            UMin = box.Min[u],
                            UMax = box.Max[u],
                            VMin = box.Min[v],
                            VMax = box.Max[v],
                            MinX = (int)faceMinX,
                            MinY = (int)faceMinY,
                            MaxX = (int)faceMaxX,
                            MaxY = (int)faceMaxY,
                            Red = Math.Min(255, box.Color.R * light + specular),
                            Green = Math.Min(255, box.Color.G * light + specular),
                            Blue = Math.Min(255, box.Color.B * light + specular),
                        });
                    }
                }
            }
            return faces;
        }
    }

    /// <summary>Which easter egg to play. The pi logo lifts off the header logo, whose top-left cell is at Column, Row.</summary>
    internal sealed record Egg(string Kind, int Column = 0, int Row = 0)
    {
        public static Egg PiLogo(int column, int row) => new("pi-logo", column, row);
        public static Egg Armin() => new("armin");
    }

    private static bool playing;

    /// <summary>
    /// Show the animation as a fullscreen overlay until it is dismissed. The overlay takes focus and mouse input and returns focus
    /// when hidden, so the rest of the UI keeps running underneath untouched.
    /// </summary>
    public static async Task PlayEasterEgg3d(ITui tui, IReadOnlyList<string> screen, Egg egg)
    {
        if (playing) return;
        playing = true;
        // Fading needs the terminal's actual default colors; the theme only knows its own.
        var reported = await tui.QueryTerminalColors(100);
        var dark = theme.Appearance == "dark";
        static Rgb ToRgbTuple(RgbColor? rgb, Rgb fallback) => rgb is { } value ? new Rgb(value.R, value.G, value.B) : fallback;
        var foreground = ToRgbTuple(reported.Foreground, ToRgb(theme.TokenColors["text"]));
        var background = ToRgbTuple(reported.Background, dark ? new Rgb(0, 0, 0) : new Rgb(255, 255, 255));
        if (tui.HasOverlay())
        {
            playing = false;
            return;
        }
        var model = egg.Kind == "armin" ? ArminModel(ToRgb(theme.TokenColors["accent"])) : PiLogoModel((egg.Column, egg.Row));
        IOverlayHandle? overlay = null;
        var animation = new EasterEgg3dAnimation(tui, screen, model, (foreground, background), () =>
        {
            playing = false;
            overlay?.Hide();
        });
        overlay = tui.ShowOverlay(animation, new OverlayOptions { Anchor = OverlayAnchor.TopLeft, Width = SizeValue.Pct(100), MaxHeight = SizeValue.Pct(100) });
    }

    /// <summary>Resets the module's "playing" flag (tests).</summary>
    internal static void ResetForTests() => playing = false;

    // JS numeric helpers.

    /// <summary>Math.round.</summary>
    internal static double JsRound(double value) => Math.Floor(value + 0.5);

    /// <summary>Math.min / Math.max propagate NaN.</summary>
    private static double JsMin(double a, double b) => double.IsNaN(a) || double.IsNaN(b) ? double.NaN : Math.Min(a, b);
    private static double JsMax(double a, double b) => double.IsNaN(a) || double.IsNaN(b) ? double.NaN : Math.Max(a, b);

    /// <summary>ToInt32: truncate and wrap modulo 2^32.</summary>
    internal static int ToInt32(double value)
    {
        if (!double.IsFinite(value)) return 0;
        var truncated = Math.Truncate(value);
        if (Math.Abs(truncated) < 9.2e18) return unchecked((int)(long)truncated);
        var wrapped = truncated % 4294967296.0;
        if (wrapped < 0) wrapped += 4294967296.0;
        return unchecked((int)(uint)wrapped);
    }

    /// <summary>Math.hypot as V8 computes it: scaled by the largest magnitude with a compensated sum.</summary>
    internal static double Hypot(params double[] values)
    {
        var max = 0.0;
        foreach (var value in values)
        {
            var abs = Math.Abs(value);
            if (double.IsPositiveInfinity(abs)) return double.PositiveInfinity;
            if (double.IsNaN(abs)) max = double.NaN;
            else if (abs > max) max = abs;
        }
        if (double.IsNaN(max)) return double.NaN;
        if (max == 0) return 0;
        double sum = 0, compensation = 0;
        foreach (var value in values)
        {
            var n = Math.Abs(value) / max;
            var summand = n * n - compensation;
            var preliminary = sum + summand;
            compensation = preliminary - sum - summand;
            sum = preliminary;
        }
        return Math.Sqrt(sum) * max;
    }

    /// <summary>Number.parseInt(text, 10): optional whitespace and sign, then leading decimal digits; NaN when there are none.</summary>
    internal static double JsParseInt(string text)
    {
        var index = 0;
        while (index < text.Length && TextUtils.IsJsWhitespace(text[index])) index++;
        var negative = false;
        if (index < text.Length && (text[index] == '+' || text[index] == '-')) { negative = text[index] == '-'; index++; }
        var start = index;
        double result = 0;
        while (index < text.Length && text[index] is >= '0' and <= '9') { result = result * 10 + (text[index] - '0'); index++; }
        if (index == start) return double.NaN;
        return negative ? -result : result;
    }

    private static string JsNumber(double value) => double.IsNaN(value) ? "NaN" : value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

internal sealed class EasterEgg3dAnimation : IComponent, IInputHandler, IMouseHandler
{
    private sealed record ExitState(double Start, double Time, double Yaw, double TargetYaw, (double X, double Y, double Z)[] Offsets);
    private sealed record Hint(int Row, int Start, string Text, int KeyLength, EasterEgg3d.Rgb KeyColor, EasterEgg3d.Rgb Color);

    private static readonly Func<double> PerformanceNow = () => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;

    private readonly ITui tui;
    /// <summary>The screen to dissolve, as rendered lines.</summary>
    private readonly IReadOnlyList<string> screen;
    private readonly EasterEgg3d.Model model;
    private readonly EasterEgg3d.Rgb foreground;
    private readonly EasterEgg3d.Rgb background;
    private readonly Action onDone;
    private readonly Func<double> now;
    private readonly double startTime;
    private double lastRender;
    private IDisposable? timer;
    private ExitState? exit;
    private (int Cycle, List<EasterEgg3d.Cell3[]> Steps)? shuffle;
    private int screenWidth = -1;
    private int screenHeight = -1;
    /// <summary>Star per cell index, or null.</summary>
    private EasterEgg3d.Star?[] stars = [];
    private List<List<EasterEgg3d.ScreenCell>> cells = [];
    private readonly Dictionary<int, string> ansiCache = [];
    private readonly EasterEgg3d.BlockRaster raster;

    /// <param name="now">performance.now() in milliseconds; injectable for tests.</param>
    internal EasterEgg3dAnimation(ITui tui, IReadOnlyList<string> screen, EasterEgg3d.Model model,
        (EasterEgg3d.Rgb Foreground, EasterEgg3d.Rgb Background) colors, Action onDone, Func<double>? now = null)
    {
        this.tui = tui;
        this.screen = screen;
        this.model = model;
        this.now = now ?? PerformanceNow;
        startTime = this.now();
        lastRender = this.now();
        raster = new EasterEgg3d.BlockRaster(model.CameraDistance);
        foreground = colors.Foreground;
        background = colors.Background;
        this.onDone = onDone;
        timer = tui.Loop.SetInterval(() =>
        {
            // Also stop when no longer rendered, e.g. when pi hides all overlays on exit.
            if ((exit is not null && ExitProgress() >= 1) || this.now() - lastRender > 1000) Finish();
            else this.tui.RequestRender();
        }, EasterEgg3d.FRAME_MS);
    }

    /// <summary>Whether the frame timer still runs (tests).</summary>
    internal bool IsRunning => timer is not null;
    internal bool IsExiting => exit is not null;

    /// <summary>Play the exit animation. A second call skips it.</summary>
    public void Close()
    {
        if (exit is not null)
        {
            Finish();
            return;
        }
        var time = Elapsed();
        var yaw = EasterEgg3d.SpinAngle(time);
        const double turn = Math.PI * 2;
        exit = new ExitState(now(), time, yaw, Math.Ceiling(yaw / turn) * turn, BlockOffsets(time));
    }

    public void HandleInput(string data)
    {
        var keybindings = KeybindingsManager.Global;
        if (keybindings.Matches(data, "tui.select.cancel") || keybindings.Matches(data, "app.clear")) Close();
    }

    public TuiMouseEventResult? HandleMouse(TuiMouseEvent mouseEvent)
    {
        if (mouseEvent.Type == TuiMouseEventType.Click) Close();
        return new TuiMouseEventResult(Handled: true, Render: false);
    }

    public void Invalidate()
    {
        screenWidth = -1;
        screenHeight = -1;
        ansiCache.Clear();
    }

    public List<string> Render(int width)
    {
        var height = Math.Max(1, tui.Terminal.Rows);
        var foreground = this.foreground;
        var background = this.background;
        lastRender = now();
        if (width != screenWidth || height != screenHeight)
        {
            screenWidth = width;
            screenHeight = height;
            cells = PrepareCells(width, foreground);
            stars = PrepareStars(width, height);
        }

        // Dissolve time runs forward on entry and backward on exit, so the screen reassembles in reverse order.
        double dissolveTime;
        EasterEgg3d.Pose pose;
        double hintAlpha;
        double starAlpha;
        (double X, double Y, double Z)[] offsets;
        if (exit is not null)
        {
            var progress = ExitProgress();
            var landing = EasterEgg3d.Clamp01(progress / 0.8);
            var settle = 1 - EasterEgg3d.Smooth(landing);
            dissolveTime = Math.Min(exit.Time, EasterEgg3d.DISSOLVE_END) * (1 - progress);
            pose = Pose(width, height, FlyProgress(exit.Time) * settle, exit.Time);
            pose.Yaw = exit.Yaw + (exit.TargetYaw - exit.Yaw) * EasterEgg3d.EaseOut(landing);
            pose.Pitch *= settle;
            pose.Roll *= settle;
            hintAlpha = HintAlpha(exit.Time) * (1 - EasterEgg3d.Smooth(progress / 0.2));
            starAlpha = StarAlpha(exit.Time) * (1 - EasterEgg3d.Smooth(progress / 0.3));
            // Blocks are home well before the logo lands.
            var gather = 1 - EasterEgg3d.Smooth(progress / 0.5);
            offsets = exit.Offsets.Select(offset => (offset.X * gather, offset.Y * gather, offset.Z * gather)).ToArray();
        }
        else
        {
            var time = Elapsed();
            dissolveTime = time;
            pose = Pose(width, height, FlyProgress(time), time);
            hintAlpha = HintAlpha(time);
            starAlpha = StarAlpha(time);
            offsets = BlockOffsets(time);
        }

        var columns = model.Columns;
        var rows = model.Rows;
        var boxes = model.Blocks.Select((block, index) =>
        {
            var (dx, dy, dz) = offsets[index];
            var x = block.Home.X - columns / 2.0 + dx;
            var y = block.Home.Y - rows / 2.0 + dy;
            return new EasterEgg3d.Box
            {
                Min = [x, y, dz - EasterEgg3d.DEPTH / 2],
                Max = [x + 1, y + 1, dz + EasterEgg3d.DEPTH / 2],
                Color = block.Color,
            };
        }).ToList();
        var raster = this.raster;
        raster.Render(width, height, pose, boxes, background, EasterEgg3d.IsLight(background) ? EasterEgg3d.LIGHT_HALO_STRENGTH : 0);
        EasterEgg3d.Rgb? Halo(int index, EasterEgg3d.Rgb? baseColor)
        {
            var amount = raster.HaloAmount[index];
            if (amount <= 0) return baseColor;
            var color = raster.HaloRgb;
            return EasterEgg3d.Mix(baseColor ?? background, new(color[index * 3], color[index * 3 + 1], color[index * 3 + 2]), amount);
        }
        var backgroundFade = EasterEgg3d.Smooth(dissolveTime / EasterEgg3d.BACKGROUND_FADE);
        var textFade = EasterEgg3d.Smooth(dissolveTime / 0.6) * 0.3;
        // Once the screen has fully dissolved, the text layer is empty and can be skipped.
        var textActive = dissolveTime < EasterEgg3d.DISSOLVE_END || backgroundFade < 1;
        var hint = this.HintFor(width, height, hintAlpha, background);
        var frame = Math.Floor(dissolveTime * 14);
        var nowSeconds = Elapsed();
        // Star brightness is quantized, so a row only changes when one of its stars steps to another level instead of on every
        // frame.
        var starLevel = EasterEgg3d.JsRound(starAlpha * EasterEgg3d.STAR_ALPHA_LEVELS) / EasterEgg3d.STAR_ALPHA_LEVELS;
        (string Text, EasterEgg3d.Rgb Fg)? Starfield(int index)
        {
            if (starLevel <= 0) return null;
            var star = index < stars.Length ? stars[index] : null;
            if (star is null) return null;
            var flicker = EasterEgg3d.JsRound((0.5 + 0.5 * Math.Sin(nowSeconds * star.Speed + star.Phase)) * EasterEgg3d.STAR_FLICKER_LEVELS) /
                EasterEgg3d.STAR_FLICKER_LEVELS;
            return (star.Glyph, EasterEgg3d.Mix(background, EasterEgg3d.Mix(foreground, star.Tint, 0.5), star.Strength * (0.7 + 0.3 * flicker) * starLevel));
        }
        var lines = new List<string>();
        for (var row = 0; row < height; row++)
        {
            var rowCells = row < cells.Count ? cells[row] : [];
            var line = new StringBuilder();
            var currentFg = -1;
            var currentBg = -1;
            void Emit(string text, EasterEgg3d.Rgb? fg, EasterEgg3d.Rgb? bg)
            {
                var fgKey = fg is { } f ? Pack(f) : -1;
                var bgKey = bg is { } b ? Pack(b) : -1;
                if (fgKey != currentFg && text != " ")
                {
                    line.Append(fg is not null ? Ansi(fgKey, false) : "\u001b[39m");
                    currentFg = fgKey;
                }
                if (bgKey != currentBg)
                {
                    line.Append(bg is not null ? Ansi(bgKey, true) : "\u001b[49m");
                    currentBg = bgKey;
                }
                line.Append(text);
            }
            for (var column = 0; column < width; column++)
            {
                var index = row * width + column;
                var cell = textActive && column < rowCells.Count ? rowCells[column] : null;
                var cellBg = Halo(index,
                    cell?.Bg is { } ownBg && backgroundFade < 1 ? EasterEgg3d.Mix(ownBg, background, backgroundFade) : null);
                if (hint is not null && row == hint.Row && column >= hint.Start && column < hint.Start + hint.Text.Length)
                {
                    var offset = column - hint.Start;
                    Emit(hint.Text[offset].ToString(), offset < hint.KeyLength ? hint.KeyColor : hint.Color, cellBg);
                    continue;
                }
                int dots = raster.Bits[index];
                if (dots != 0)
                {
                    // Quantized so neighboring cells on one face usually share a color and its escape sequence.
                    var step = EasterEgg3d.LOGO_COLOR_STEP * raster.Counts[index];
                    var rgb = raster.RgbSum;
                    var color = new EasterEgg3d.Rgb(
                        EasterEgg3d.JsRound(rgb[index * 3] / (double)step) * EasterEgg3d.LOGO_COLOR_STEP,
                        EasterEgg3d.JsRound(rgb[index * 3 + 1] / (double)step) * EasterEgg3d.LOGO_COLOR_STEP,
                        EasterEgg3d.JsRound(rgb[index * 3 + 2] / (double)step) * EasterEgg3d.LOGO_COLOR_STEP);
                    Emit(EasterEgg3d.BRAILLE[dots], color, cellBg);
                    continue;
                }
                if (cell is null)
                {
                    var star = Starfield(index);
                    Emit(star?.Text ?? " ", star?.Fg, cellBg);
                    continue;
                }
                var dust = (dissolveTime - cell.Delay) / EasterEgg3d.DUST_DURATION;
                if (dust < 0 && cell.Width == 2 && !(index + 1 < raster.Bits.Length && raster.Bits[index + 1] != 0))
                {
                    Emit(cell.Text, EasterEgg3d.Mix(cell.Fg, background, textFade), cellBg);
                    column++;
                    continue;
                }
                if (dust < 0 && cell.Width == 1)
                {
                    Emit(cell.Text, EasterEgg3d.Mix(cell.Fg, background, textFade), cellBg);
                    continue;
                }
                var dotCount = dust < 0 ? cell.Ink : (int)EasterEgg3d.JsRound(cell.Ink * (1 - EasterEgg3d.Clamp01(dust)));
                if (dotCount <= 0)
                {
                    var star = Starfield(index);
                    Emit(star?.Text ?? " ", star?.Fg, cellBg);
                    continue;
                }
                // Pick `dotCount` distinct dots; the choice changes a few times per second so the dust shimmers.
                var bits = 0;
                var placed = 0;
                for (var attempt = 0; placed < dotCount && attempt < 32; attempt++)
                {
                    var bit = EasterEgg3d.DOT_BITS[(int)Math.Floor(EasterEgg3d.Hash(cell.Seed * 7919 + frame * 131 + attempt) * 8)];
                    if ((bits & bit) != 0) continue;
                    bits |= bit;
                    placed++;
                }
                var fade = textFade + (1 - textFade) * Math.Pow(EasterEgg3d.Clamp01(dust), 0.8);
                Emit(EasterEgg3d.BRAILLE[bits], EasterEgg3d.Mix(cell.Fg, background, fade), cellBg);
            }
            lines.Add(line + "\u001b[0m");
        }
        return lines;
    }

    private double Elapsed() => (now() - startTime) / 1000;

    private double ExitProgress() => exit is not null ? EasterEgg3d.Clamp01((now() - exit.Start) / 1000 / EasterEgg3d.EXIT_DURATION) : 0;

    private void Finish()
    {
        if (timer is null) return;
        timer.Dispose();
        timer = null;
        onDone();
    }

    /// <summary>Each block's displacement from its place in the model at <paramref name="time"/>, in grid units.</summary>
    internal (double X, double Y, double Z)[] BlockOffsets(double time)
    {
        var blocks = model.Blocks;
        (double X, double Y, double Z)[] Home() => blocks.Select(_ => (0.0, 0.0, 0.0)).ToArray();
        if (time < EasterEgg3d.PUZZLE_START) return Home();
        var cycle = (int)Math.Floor((time - EasterEgg3d.PUZZLE_START) / EasterEgg3d.PUZZLE_CYCLE);
        var local = time - EasterEgg3d.PUZZLE_START - cycle * EasterEgg3d.PUZZLE_CYCLE;
        if (shuffle?.Cycle != cycle) shuffle = (cycle, EasterEgg3d.ShuffleSteps(model, cycle));
        var steps = shuffle.Value.Steps;
        (double X, double Y, double Z) Offset(EasterEgg3d.Cell3 position, int index)
        {
            var home = blocks[index].Home;
            return (position.X - home.X, position.Y - home.Y, position.Z - home.Z);
        }
        const double shuffleEnd = EasterEgg3d.PUZZLE_STEP * EasterEgg3d.PUZZLE_STEPS;
        if (local < shuffleEnd)
        {
            var step = (int)Math.Floor(local / EasterEgg3d.PUZZLE_STEP);
            var progress = EasterEgg3d.Smooth((local - step * EasterEgg3d.PUZZLE_STEP) / EasterEgg3d.PUZZLE_STEP);
            return steps[step].Select((from, index) =>
            {
                var a = Offset(from, index);
                var b = Offset(steps[step + 1][index], index);
                return (a.X + (b.X - a.X) * progress, a.Y + (b.Y - a.Y) * progress, a.Z + (b.Z - a.Z) * progress);
            }).ToArray();
        }
        if (local < shuffleEnd + EasterEgg3d.PUZZLE_RETURN)
        {
            // All blocks fly home at once. Alternating arcs in depth keep them from passing through each other.
            var u = (local - shuffleEnd) / EasterEgg3d.PUZZLE_RETURN;
            var remaining = 1 - EasterEgg3d.Smooth(u);
            var arc = Math.Sin(Math.PI * EasterEgg3d.Clamp01(u)) * 0.8;
            return steps[EasterEgg3d.PUZZLE_STEPS].Select((position, index) =>
            {
                var (x, y, z) = Offset(position, index);
                return (x * remaining, y * remaining, z * remaining + (index % 2 == 0 ? arc : -arc));
            }).ToArray();
        }
        return Home();
    }

    private static double FlyProgress(double time) => EasterEgg3d.Smooth((time - EasterEgg3d.FLY_START) / EasterEgg3d.FLY_DURATION);

    private static double StarAlpha(double time) => EasterEgg3d.Smooth((time - EasterEgg3d.STARS_START) / EasterEgg3d.STARS_FADE);

    private static double HintAlpha(double time) => EasterEgg3d.Smooth((time - EasterEgg3d.FLY_START - EasterEgg3d.FLY_DURATION) / EasterEgg3d.HINT_FADE);

    internal EasterEgg3d.Pose Pose(int width, int height, double progress, double time)
    {
        var columns = model.Columns;
        var rows = model.Rows;
        var cameraDistance = model.CameraDistance;
        var radius = model.Radius;
        var origin = model.Origin;
        var reach = radius * (cameraDistance / (cameraDistance - radius));
        // Lifting off, the front face must cover exactly the half-block cells (2x2 dots per pixel) despite the perspective.
        var startScale = origin is not null ? 2 * (cameraDistance - EasterEgg3d.DEPTH / 2) / cameraDistance : EasterEgg3d.START_SCALE;
        var endScale = Math.Max(startScale, Math.Min(width * 2 * model.WidthShare, (height * 4 - 8) * 0.48) / reach);
        double endX = width;
        double endY = height * 2 - 2;
        double startX = origin is { } o ? o.Column * 2 + columns : endX;
        double startY = origin is { } p ? p.Row * 4 + rows : endY;
        return new EasterEgg3d.Pose
        {
            CenterX = startX + (endX - startX) * progress,
            CenterY = startY + (endY - startY) * progress,
            // Interpolate the zoom geometrically so it feels uniform.
            Scale = startScale * Math.Pow(endScale / startScale, progress),
            Yaw = EasterEgg3d.SpinAngle(time),
            // Tilts are zero at whole turns, so the camera looks straight at the front of the reassembled logo.
            Pitch = 0.3 * Math.Sin(EasterEgg3d.SpinPhase(time)) * progress,
            Roll = 0.06 * Math.Sin(2 * EasterEgg3d.SpinPhase(time)) * progress,
        };
    }

    private Hint? HintFor(int width, int height, double alpha, EasterEgg3d.Rgb background)
    {
        if (alpha <= 0) return null;
        var keys = KeybindingsManager.Global.GetKeys("tui.select.cancel");
        var key = KeybindingHints.FormatKeyText(keys.Count > 0 ? keys[0] : "escape");
        var text = key + " to return";
        if (text.Length > width) return null;
        return new Hint(
            height - 2,
            (int)Math.Floor((width - text.Length) / 2.0),
            text,
            key.Length,
            EasterEgg3d.Mix(background, EasterEgg3d.ToRgb(theme.TokenColors["muted"]), alpha),
            EasterEgg3d.Mix(background, EasterEgg3d.ToRgb(theme.TokenColors["dim"]), alpha));
    }

    /// <summary>A sparse, deterministic starfield: one braille dot in about STAR_DENSITY of all cells.</summary>
    internal static EasterEgg3d.Star?[] PrepareStars(int width, int height)
    {
        var stars = new EasterEgg3d.Star?[width * height];
        for (var index = 0; index < width * height; index++)
        {
            if (EasterEgg3d.Hash(index * 3 + 0x51ed) >= EasterEgg3d.STAR_DENSITY) continue;
            double Random(int salt) => EasterEgg3d.Hash(index * 7 + salt * 0x9e37);
            stars[index] = new EasterEgg3d.Star
            {
                Glyph = EasterEgg3d.BRAILLE[EasterEgg3d.DOT_BITS[(int)Math.Floor(Random(1) * 8)]],
                Tint = EasterEgg3d.STAR_TINTS[(int)Math.Floor(Random(2) * EasterEgg3d.STAR_TINTS.Length)],
                Strength = 0.1 + Random(3) * 0.12,
                Phase = Random(4) * Math.PI * 2,
                Speed = 0.4 + Random(5) * 0.8,
            };
        }
        return stars;
    }

    internal List<List<EasterEgg3d.ScreenCell>> PrepareCells(int width, EasterEgg3d.Rgb foreground)
    {
        var cells = EasterEgg3d.ParseScreen(screen, width, foreground, background);
        var height = Math.Max(1, tui.Terminal.Rows);
        var columns = model.Columns;
        var rows = model.Rows;
        var origin = model.Origin;
        // The dust spreads out from where the model starts.
        var centerX = origin is { } o ? o.Column + columns / 2.0 : width / 2.0;
        var centerY = origin is { } p ? p.Row + rows / 4.0 : height / 2.0;
        var farthest = EasterEgg3d.Hypot(Math.Max(centerX, width - centerX), Math.Max(centerY, height - centerY) * 2);
        for (var row = 0; row < cells.Count; row++)
        {
            var line = cells[row];
            for (var column = 0; column < line.Count; column++)
            {
                var cell = line[column];
                // Wide graphemes share their first column's timing so both halves change together.
                var owner = cell.Width == 0 ? line[column - 1] : cell;
                var seed = EasterEgg3d.Hash(row * 65_537 + column);
                if (cell.Width != 0)
                {
                    var distance = EasterEgg3d.Hypot(column - centerX, (row - centerY) * 2) / farthest;
                    cell.Delay = EasterEgg3d.FLY_START + distance * EasterEgg3d.WAVE_SPREAD + seed * EasterEgg3d.WAVE_JITTER;
                }
                else
                {
                    cell.Delay = owner.Delay;
                }
                cell.Seed = row * 65_537 + column;
                cell.Ink = EasterEgg3d.GlyphInk(owner.Text, seed);
            }
        }
        // The 3D model replaces its half-block rendering on screen.
        if (origin is { } start)
        {
            for (var row = start.Row; row < start.Row + (int)Math.Ceiling(rows / 2.0); row++)
            {
                if (row < 0 || row >= cells.Count) continue;
                var line = cells[row];
                for (var column = start.Column; column < start.Column + columns; column++)
                {
                    if (column < 0 || column >= line.Count) continue;
                    var cell = line[column];
                    cell.Text = " ";
                    cell.Width = 1;
                    cell.Ink = 0;
                    cell.Bg = null;
                }
            }
        }
        return cells;
    }

    private static int Pack(EasterEgg3d.Rgb color) =>
        ((int)EasterEgg3d.JsRound(color.R) << 16) | ((int)EasterEgg3d.JsRound(color.G) << 8) | (int)EasterEgg3d.JsRound(color.B);

    private string Ansi(int key, bool isBackground)
    {
        var cacheKey = key * 2 + (isBackground ? 1 : 0);
        if (!ansiCache.TryGetValue(cacheKey, out var value))
        {
            var color = Color.Rgb((key >> 16) & 255, (key >> 8) & 255, key & 255);
            var mode = theme.GetColorMode();
            value = isBackground ? Colors.BackgroundAnsi(color, mode) : Colors.ForegroundAnsi(color, mode);
            ansiCache[cacheKey] = value;
        }
        return value;
    }
}
