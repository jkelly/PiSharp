using PiSharp.Cli.Interactive.Mode;
using PiSharp.Cli.Interactive.Mode.Components;
using PiSharp.Tui.Pi;
using static Expect;
using Egg = PiSharp.Cli.Interactive.Mode.Components.EasterEgg3d;

/// <summary>armin.ts, easter-egg-3d.ts and easter-egg-3d.lazy.ts. Upstream has no tests for these; expectations are authored from
/// the sources (the bitmap snapshot, hash values and puzzle steps were worked out from the source constants by hand-written
/// re-implementations, not captured from an upstream run).</summary>
internal static class EasterEggCases
{
    /// <summary>The Armin bitmap packed into half blocks (18 rows of 31 cells), decoded from the XBM bits.</summary>
    private static readonly string[] ArminArt =
    [
        "        ▄▄▄▄                   ",
        "         ▀▄ ▀▄                 ",
        "           █  ▀▄▄              ",
        "     ▄▀▀▀▀▀     █              ",
        "     █▄   ▄▄▄▄▀▀▀▀▄            ",
        "   ▄▀  ▀▀▀  ▄▄▄▄▀▀ █           ",
        "   █              ▄█▄          ",
        "   ▀▄   ▄▄▄▀▀▀▀▀▀▀▀▄█  ▄▄▄▄▄   ",
        " ▄▄▄▀▀▀▀▄▄▄▄▄▄▄▄▄▄█▄ ▄▀   ▄ ▀▄ ",
        " █▄▄▄▄        ▀▀▀▀  █     ▀█ █ ",
        "     ██ ▄█      █   █ ▄      █ ",
        "     ▀█████▄▄▄▄███▄▄▀▄▀▄▄▄  ▄▀ ",
        "       ▀██████▀▀▀▀   ▀▄▄▄▄▄▀   ",
        "         ▀████████▀  ▄▄ ▄▄▄    ",
        "           ▀█████▀  ██▀ ██▄    ",
        "                    ██  ▄▄▄    ",
        "                     █  ▀█▀    ",
        "                      ▀▀▀      ",
    ];

    private static List<string> ArminFrame(IEnumerable<string> art) => [.. art.Select(row => " " + row), " ARMIN SAYS HI"];

    private sealed class FakeTerminal(int columns, int rows) : ITerminal
    {
        public List<string> Writes { get; } = [];
        public void Start(Action<string> onInput, Action onResize) { }
        public void Stop() { }
        public Task DrainInputAsync(int maxMs = 1000, int idleMs = 50) => Task.CompletedTask;
        public void Write(string data) => Writes.Add(data);
        public int Columns => columns;
        public int Rows => rows;
        public bool KittyProtocolActive => false;
        public void MoveBy(int lines) { }
        public void HideCursor() { }
        public void ShowCursor() { }
        public void ClearLine() { }
        public void ClearFromCursor() { }
        public void ClearScreen() { }
        public void SetTitle(string title) { }
        public void SetProgress(bool active) { }
        public void SetProgramStatus(ProgramStatus status) { }
    }

    private static TuiAltScreen AltScreen(int columns = 20, int rows = 6) => new(new FakeTerminal(columns, rows), UiLoop.CreateManual());

    private static ArminComponent Armin(string effect, Func<double>? random = null) =>
        new(AltScreen(), random ?? (() => 0.5), effect, startAnimation: false);

    private static void Near(double expected, double actual, string what, double tolerance = 1e-9) =>
        Check(Math.Abs(expected - actual) <= tolerance, $"{what}: expected <{expected}>, actual <{actual}>.");

    private static EasterEgg3dAnimation Animation(TuiAltScreen tui, Egg.Model model, IReadOnlyList<string> screen, Func<double> now, Action? onDone = null) =>
        new(tui, screen, model, (new Egg.Rgb(200, 200, 200), new Egg.Rgb(0, 0, 0)), onDone ?? (() => { }), now);

    private static void Stop(EasterEgg3dAnimation animation) { animation.Close(); animation.Close(); }

    public static IEnumerable<(string Id, Func<Task> Run)> All()
    {
        // armin.ts
        yield return ("egg.armin.bitmap-dimensions", Sync(() =>
        {
            Equal(31, PiSharp.Cli.Interactive.Mode.Components.Armin.ArminWidth, "ARMIN_WIDTH");
            Equal(36, PiSharp.Cli.Interactive.Mode.Components.Armin.ArminHeight, "ARMIN_HEIGHT");
            var count = 0;
            for (var y = 0; y < 36; y++)
                for (var x = 0; x < 31; x++)
                    if (PiSharp.Cli.Interactive.Mode.Components.Armin.IsArminPixel(x, y)) count++;
            Equal(245, count, "foreground pixels");
            // Row 0 is empty; row 1 has pixels 8..11 (byte 0xf0); row 2 has 9 and 12 (byte 0xed).
            for (var x = 0; x < 31; x++) Check(!PiSharp.Cli.Interactive.Mode.Components.Armin.IsArminPixel(x, 0), $"row 0 pixel {x}");
            Check(!PiSharp.Cli.Interactive.Mode.Components.Armin.IsArminPixel(7, 1), "(7, 1)");
            for (var x = 8; x <= 11; x++) Check(PiSharp.Cli.Interactive.Mode.Components.Armin.IsArminPixel(x, 1), $"({x}, 1)");
            Check(!PiSharp.Cli.Interactive.Mode.Components.Armin.IsArminPixel(12, 1), "(12, 1)");
            Check(PiSharp.Cli.Interactive.Mode.Components.Armin.IsArminPixel(9, 2) && PiSharp.Cli.Interactive.Mode.Components.Armin.IsArminPixel(12, 2), "row 2");
            Check(!PiSharp.Cli.Interactive.Mode.Components.Armin.IsArminPixel(10, 2), "(10, 2)");
            // Rows past the bitmap are background.
            Check(!PiSharp.Cli.Interactive.Mode.Components.Armin.IsArminPixel(10, 36) && !PiSharp.Cli.Interactive.Mode.Components.Armin.IsArminPixel(0, 100), "past the bottom");
        }));
        yield return ("egg.armin.scanline-frames", Sync(() =>
        {
            var armin = Armin("scanline");
            Lines(ArminFrame(Enumerable.Repeat("", 18)), armin.Render(40), "empty grid before the first tick");
            Check(!armin.Step(), "tick 1");
            Lines(ArminFrame(ArminArt.Take(1).Concat(Enumerable.Repeat("", 17))), armin.Render(40), "after tick 1");
            for (var tick = 2; tick <= 18; tick++) Check(!armin.Step(), $"tick {tick}");
            Lines(ArminFrame(ArminArt), armin.Render(40), "after tick 18");
            Check(armin.Step(), "tick 19 finishes");
            var line = armin.Render(40)[0];
            Equal(" " + Themes.Current.Fg("accent", ArminArt[0]) + new string(' ', 8), line, "row styling and right padding");
            Equal(" " + Themes.Current.Fg("accent", "ARMIN SAYS HI") + new string(' ', 26), armin.Render(40)[18], "message line");
        }));
        yield return ("egg.armin.typewriter-frames", Sync(() =>
        {
            var armin = Armin("typewriter");
            for (var tick = 1; tick <= 3; tick++) Check(!armin.Step(), $"tick {tick}");
            // Three cells per tick: nine cells of row 0.
            Lines(ArminFrame(new[] { ArminArt[0][..9] }.Concat(Enumerable.Repeat("", 17))), armin.Render(40), "after 3 ticks");
            for (var tick = 4; tick <= 11; tick++) armin.Step();
            // 33 cells: all of row 0 and two of row 1.
            Lines(ArminFrame(new[] { ArminArt[0], ArminArt[1][..2] }.Concat(Enumerable.Repeat("", 16))), armin.Render(40), "after 11 ticks");
            var ticks = 11;
            while (!armin.Step()) ticks++;
            Equal(187, ticks + 1, "the 187th tick finishes (558 cells, 3 per tick)");
            Lines(ArminFrame(ArminArt), armin.Render(40), "final frame");
        }));
        yield return ("egg.armin.crt-frames", Sync(() =>
        {
            var armin = Armin("crt");
            armin.Step();
            var expected = Enumerable.Repeat("", 18).ToArray();
            expected[9] = ArminArt[9];
            Lines(ArminFrame(expected), armin.Render(40), "tick 1 shows the middle row");
            armin.Step();
            expected[8] = ArminArt[8]; expected[10] = ArminArt[10];
            Lines(ArminFrame(expected), armin.Render(40), "tick 2 expands by one row");
            var ticks = 2;
            while (!armin.Step()) ticks++;
            Equal(19, ticks + 1, "finishes once the expansion passes the height");
            Lines(ArminFrame(ArminArt), armin.Render(40), "final frame");
        }));
        yield return ("egg.armin.random-effects-resolve", Sync(() =>
        {
            foreach (var (effect, random, maxTicks) in new[] { ("fade", 0.5, 38), ("dissolve", 0.0, 28), ("glitch", 0.9, 9) })
            {
                var armin = Armin(effect, () => random);
                var ticks = 1;
                while (!armin.Step()) { ticks++; Check(ticks <= maxTicks, effect + " finishes"); }
                Equal(maxTicks, ticks, effect + " ticks");
                Lines(ArminFrame(ArminArt), armin.Render(40), effect + " final frame");
            }
            // Rain settles each column's pixels bottom-up, but a column only counts as done once a drop settles on row 0, which
            // only columns 8..11 have: as upstream, the image completes while the effect never reports done.
            var rain = Armin("rain", () => 0);
            for (var tick = 0; tick < 200; tick++) Check(!rain.Step(), "rain keeps ticking");
            Lines(ArminFrame(ArminArt), rain.Render(40), "rain final frame");
            // Dissolve starts from noise picked by Math.random: index floor(0.99 * 7) = 6 is "▄".
            Lines(ArminFrame(Enumerable.Repeat(new string('▄', 31), 18)), Armin("dissolve", () => 0.99).Render(40), "dissolve noise");
        }));
        yield return ("egg.armin.glitch-shift", Sync(() =>
        {
            // random 0.1: offset floor(0.7) - 3 = -3 and 0.1 < 0.3, so every row rotates right by three cells.
            var armin = Armin("glitch", () => 0.1);
            armin.Step();
            Lines(ArminFrame(ArminArt.Select(row => row[^3..] + row[..^3])), armin.Render(40), "rotated rows");
            // random 0.35: offset -1, no shift (0.35 >= 0.3), no vertical swap (0.35 >= 0.2): rows unchanged.
            var clean = Armin("glitch", () => 0.35);
            clean.Step();
            Lines(ArminFrame(ArminArt), clean.Render(40), "unchanged rows");
            // random 0.15: offset -2 and shift.
            var two = Armin("glitch", () => 0.15);
            two.Step();
            Lines(ArminFrame(ArminArt.Select(row => row[^2..] + row[..^2])), two.Render(40), "rotated by two");
        }));
        yield return ("egg.armin.effect-pick-and-cache", Sync(() =>
        {
            Equal("typewriter,scanline,rain,fade,crt,glitch,dissolve", string.Join(",", ArminComponent.EFFECTS), "effects");
            Equal("fade", new ArminComponent(AltScreen(), () => 0.5, null, false).Effect, "floor(0.5 * 7)");
            Equal("dissolve", new ArminComponent(AltScreen(), () => 0.999, null, false).Effect, "floor(0.999 * 7)");
            var armin = Armin("scanline");
            for (var tick = 0; tick < 18; tick++) armin.Step();
            var first = armin.Render(12);
            Check(ReferenceEquals(first, armin.Render(12)), "cached for the same width and grid version");
            Lines(ArminFrame(ArminArt.Select(row => row[..11])), first, "clipped to width - 1");
            Equal(" " + Themes.Current.Fg("accent", "ARMIN SAYS HI"), first[18], "message is not clipped");
            armin.Invalidate();
            Check(!ReferenceEquals(first, armin.Render(12)), "invalidate drops the cache");
            // width 0: slice(0, -1) keeps all but the last cell, as JS does.
            Lines(ArminFrame(ArminArt.Select(row => row[..30])), armin.Render(0), "width 0");
            var animated = new ArminComponent(AltScreen(), () => 0.5, "crt", true);
            Check(animated.IsAnimating, "interval started");
            animated.Dispose();
            Check(!animated.IsAnimating, "dispose stops the interval");
        }));

        // easter-egg-3d.ts pure functions
        yield return ("egg.3d.hash", Sync(() =>
        {
            Equal(0.11478774505667388, Egg.Hash(0), "hash(0)");
            Equal(0.24678996880538762, Egg.Hash(1), "hash(1)");
            Equal(0.02330568921752274, Egg.Hash(2), "hash(2)");
            Equal(0.8377483149524778, Egg.Hash(7920), "hash(7920)");
            Equal(0.8311750083230436, Egg.Hash(0x51ed), "hash(0x51ed)");
            Equal(0.23941869544796646, Egg.Hash(123456789), "hash(123456789)");
            // ToInt32 wraps modulo 2^32.
            Equal(0.6074910450261086, Egg.Hash(26_000_000_000), "hash(26e9)");
            Equal(Egg.Hash(26_000_000_000 - 4294967296.0 * 6), Egg.Hash(26_000_000_000), "wraps");
        }));
        yield return ("egg.3d.easing", Sync(() =>
        {
            Equal(0.0, Egg.Smooth(-1), "smooth below 0");
            Equal(1.0, Egg.Smooth(2), "smooth above 1");
            Equal(0.5, Egg.Smooth(0.5), "smooth midpoint");
            Near(0.103515625, Egg.Smooth(0.25), "smooth(0.25)");
            Equal(0.875, Egg.EaseOut(0.5), "easeOut(0.5)");
            Equal(0.0, Egg.EaseOut(-3), "easeOut below 0");
            Equal(0.25, Egg.Clamp01(0.25), "clamp01");
            Equal(new Egg.Rgb(50, 100, 150), Egg.Mix(new Egg.Rgb(0, 0, 0), new Egg.Rgb(100, 200, 300), 0.5), "mix");
            Check(Egg.IsLight(new Egg.Rgb(255, 255, 255)) && !Egg.IsLight(new Egg.Rgb(0, 0, 0)) && !Egg.IsLight(new Egg.Rgb(128, 128, 128)), "isLight");
            Equal(5.0, Egg.Hypot(3, 4), "hypot");
            Equal(0.0, Egg.Hypot(0, 0, 0), "hypot of zeros");
            Near(0.75 / Math.Sqrt(1.125), Egg.LIGHT[2], "LIGHT z");
        }));
        yield return ("egg.3d.rotation", Sync(() =>
        {
            void Matrix(double[] expected, double[] actual, string what)
            { for (var i = 0; i < 9; i++) Near(expected[i], actual[i], $"{what}[{i}]", 1e-12); }
            Matrix([1, 0, 0, 0, 1, 0, 0, 0, 1], Egg.Rotation(0, 0, 0), "identity");
            Matrix([0, 0, 1, 0, 1, 0, -1, 0, 0], Egg.Rotation(Math.PI / 2, 0, 0), "yaw");
            Matrix([1, 0, 0, 0, 0, -1, 0, 1, 0], Egg.Rotation(0, Math.PI / 2, 0), "pitch");
            Matrix([0, -1, 0, 1, 0, 0, 0, 0, 1], Egg.Rotation(0, 0, Math.PI / 2), "roll");
            // Rz * Rx * Ry: yaw then pitch then roll.
            Matrix(Egg.Multiply(Egg.Rotation(0, 0, 0.3), Egg.Multiply(Egg.Rotation(0, 0.2, 0), Egg.Rotation(0.1, 0, 0))), Egg.Rotation(0.1, 0.2, 0.3), "composition");
        }));
        yield return ("egg.3d.spin", Sync(() =>
        {
            Near(4.4, Egg.PUZZLE_START, "PUZZLE_START");
            Near(5.2, Egg.PUZZLE_CYCLE, "PUZZLE_CYCLE");
            Near(9.3, Egg.FIRST_FRONT_VIEW, "FIRST_FRONT_VIEW");
            Near(1.12, Egg.DISSOLVE_END, "DISSOLVE_END");
            Equal(0.0, Egg.BaseSpinPhase(Egg.FLY_START), "no spin before the flight");
            Equal(0.0, Egg.SpinPhase(0), "phase at 0");
            // The ramp integral meets the uniform spin at the end of the ramp.
            Near(0.7 * Egg.SPIN_SPEED, Egg.BaseSpinPhase(1.5), "ramp end");
            Near(Egg.BaseSpinPhase(1.5), Egg.BaseSpinPhase(1.5 - 1e-9), "continuous", 1e-7);
            var turn = Math.PI * 2;
            var front = Egg.SpinPhase(Egg.FIRST_FRONT_VIEW);
            Near(Math.Round(front / turn) * turn, front, "whole turns at the first front view");
            Near(front + turn, Egg.SpinPhase(Egg.FIRST_FRONT_VIEW + Egg.PUZZLE_CYCLE), "one turn per cycle", 1e-9);
            Near(front, Egg.SpinAngle(Egg.FIRST_FRONT_VIEW), "angle equals phase at whole turns");
            var t = 7.0;
            Near(Egg.SpinPhase(t) - 0.6 * Math.Sin(Egg.SpinPhase(t)), Egg.SpinAngle(t), "linger");
        }));
        yield return ("egg.3d.glyph-ink", Sync(() =>
        {
            Equal(0, Egg.GlyphInk(" ", 0.5), "space");
            Equal(0, Egg.GlyphInk("", 0.5), "empty");
            Equal(2, Egg.GlyphInk(".", 0.9), "period");
            Equal(2, Egg.GlyphInk("·", 0.9), "middle dot");
            Equal(2, Egg.GlyphInk("-", 0.9), "hyphen");
            Equal(3, Egg.GlyphInk("─", 0.9), "box drawing");
            Equal(3, Egg.GlyphInk("╿", 0.9), "box drawing end");
            Equal(4, Egg.GlyphInk("a", 0.2), "letter, low seed");
            Equal(5, Egg.GlyphInk("a", 0.5), "letter, mid seed");
            Equal(6, Egg.GlyphInk("中", 0.99), "wide, high seed");
        }));
        yield return ("egg.3d.parse-screen", Sync(() =>
        {
            var fg = new Egg.Rgb(200, 200, 200);
            var bg = new Egg.Rgb(10, 10, 10);
            var rows = Egg.ParseScreen(
            [
                "\u001b[31mab\u001b[0m c",
                "中x",
                "\u001b[7mz\u001b[27m\u001b[2mq",
                "\u001b[38;2;10;20;30;48;5;196mk\u001b[38:2::1:2:3m\u001b[94ml\u001b[39;49mm",
                "\u001b]8;;http://x\u0007L\u001b]8;;\u0007\u001b_pi:c\u0007!",
                "abcdef",
                "\u001b[38:2::1:2:3mn",
            ], 4, fg, bg);
            Equal("a,b, ,c", string.Join(",", rows[0].Select(cell => cell.Text)), "row 0 text");
            Equal(new Egg.Rgb(128, 0, 0), rows[0][0].Fg, "SGR 31 is palette 1");
            Equal(fg, rows[0][2].Fg, "reset");
            Check(rows[0][0].Bg is null, "default background");
            Equal("中,,x", string.Join(",", rows[1].Select(cell => cell.Text)), "wide glyph");
            Equal("2,0,1", string.Join(",", rows[1].Select(cell => cell.Width)), "wide widths");
            Equal(bg, rows[2][0].Fg, "inverse foreground is the background");
            Equal(fg, rows[2][0].Bg, "inverse background is the foreground");
            Equal(Egg.Mix(fg, bg, 0.4), rows[2][1].Fg, "dim mixes toward the background");
            Equal(new Egg.Rgb(10, 20, 30), rows[3][0].Fg, "truecolor foreground");
            Equal(new Egg.Rgb(255, 0, 0), rows[3][0].Bg, "256-color background 196");
            Equal(new Egg.Rgb(0, 0, 255), rows[3][1].Fg, "bright blue overrides the colon color");
            Equal(fg, rows[3][2].Fg, "39 resets the foreground");
            Check(rows[3][2].Bg is null, "49 resets the background");
            Equal("L,!", string.Join(",", rows[4].Select(cell => cell.Text)), "OSC and APC skipped");
            Equal("a,b,c,d", string.Join(",", rows[5].Select(cell => cell.Text)), "clipped to the width");
            Equal(new Egg.Rgb(1, 2, 3), rows[6][0].Fg, "colon sub-parameters with an empty color space");
            Throws<ArgumentException>(() => Egg.ParseScreen(["\u001b[38;5;mx"], 4, fg, bg), "38;5 without an index fails like indexedColor(NaN)");
        }));
        yield return ("egg.3d.models", Sync(() =>
        {
            var pi = Egg.PiLogoModel((2, 1));
            Equal(10, pi.Blocks.Count, "pi logo blocks");
            Equal(Egg.CORAL, pi.Blocks[0].Color, "c is coral");
            Equal(Egg.BLUE, pi.Blocks[3].Color, "b is blue");
            Equal(new Egg.Cell3(3, 2, 0), pi.Blocks[7].Home, "first yellow block home");
            Equal(Egg.YELLOW, pi.Blocks[7].Color, "y is yellow");
            Equal(10.0, pi.CameraDistance, "pi camera");
            Near(Math.Sqrt(4 + 4 + 1.35 * 1.35), pi.Radius, "pi radius", 1e-12);
            Equal(2, pi.PuzzleMoves(0.39), "pi moves below 0.4");
            Equal(1, pi.PuzzleMoves(0.4), "pi moves at 0.4");
            var armin = Egg.ArminModel(new Egg.Rgb(1, 2, 3));
            Equal(245, armin.Blocks.Count, "armin blocks");
            Equal(new Egg.Cell3(8, 1, 0), armin.Blocks[0].Home, "first armin block");
            Check(armin.Origin is null, "armin grows out of the center");
            Equal(7, armin.PuzzleMoves(0), "round(245 * 0.03)");
            Equal(12, armin.PuzzleMoves(0.5), "round(245 * 0.05)");
            Equal(17, armin.PuzzleMoves(1), "round(245 * 0.07)");
        }));
        yield return ("egg.3d.shuffle-steps", Sync(() =>
        {
            var pi = Egg.PiLogoModel((0, 0));
            var steps = Egg.ShuffleSteps(pi, 0);
            Equal(13, steps.Count, "home plus 12 steps");
            string Show(Egg.Cell3[] cells) => string.Join(" ", cells.Select(cell => $"{cell.X},{cell.Y},{cell.Z}"));
            Equal("0,0,0 1,0,0 2,0,0 0,1,0 2,1,0 0,2,0 1,2,0 3,2,0 0,3,0 3,3,0", Show(steps[0]), "home");
            Equal("0,0,-1 1,0,0 2,0,0 0,1,1 2,1,0 0,2,0 1,2,0 3,2,0 0,3,0 3,3,0", Show(steps[1]), "step 1 moves two blocks in depth");
            Equal("0,0,-1 1,0,0 2,0,0 0,1,1 2,1,0 0,2,1 1,2,0 3,2,0 0,3,0 3,3,0", Show(steps[2]), "step 2");
            Equal("0,0,-1 1,0,0 2,0,0 0,1,1 3,1,0 0,2,1 1,2,0 3,2,0 0,3,0 3,3,0", Show(steps[3]), "step 3");
            Equal("1,0,-1 1,0,0 2,0,0 2,0,1 3,0,-1 0,3,1 1,3,-1 3,2,0 0,3,0 1,3,0", Show(steps[12]), "step 12");
            Equal("0,0,0 1,0,0 2,0,0 0,1,0 2,1,0 0,2,0 1,2,0 2,2,0 0,3,0 3,3,0", Show(Egg.ShuffleSteps(pi, 1)[1]), "cycle 1 step 1");
            Equal(Show(steps[12]), Show(Egg.ShuffleSteps(pi, 0)[12]), "deterministic per cycle");
            foreach (var step in steps)
                Equal(step.Length, step.Distinct().Count(), "no two blocks share a cell");
        }));
        yield return ("egg.3d.raster-front-face", Sync(() =>
        {
            var raster = new Egg.BlockRaster(10);
            var box = new Egg.Box { Min = [-0.5, -0.5, -0.35], Max = [0.5, 0.5, 0.35], Color = new Egg.Rgb(100, 100, 100) };
            var pose = new Egg.Pose { CenterX = 4, CenterY = 4, Scale = 2 };
            raster.Render(4, 2, pose, [box], new Egg.Rgb(0, 0, 0), 0);
            // Only the front face (z = 0.35) is visible; it spans dots 3..4 on both axes (t = 0.965 shrinks it to +-1.036 dots).
            Equal("0,128,64,0,0,8,1,0", string.Join(",", raster.Bits), "braille bits");
            Equal("0,1,1,0,0,1,1,0", string.Join(",", raster.Counts), "dot counts");
            var light = 0.45 + 0.75 / Math.Sqrt(1.125) * 0.78;
            var halfLength = Math.Sqrt(0.45 * 0.45 / 1.125 + 0.6 * 0.6 / 1.125 + Math.Pow(0.75 / Math.Sqrt(1.125) + 1, 2));
            var specular = Math.Pow((0.75 / Math.Sqrt(1.125) + 1) / halfLength, 24) * 0.6 * 255;
            var face = Math.Min(255, 100 * light + specular);
            var fog = 0.15 - 10 * (1 - (double)(float)0.965) * 0.12;
            Near(face * (1 - fog), raster.RgbSum[3], "shaded red with fog", 1e-3);
            Equal(raster.RgbSum[3], raster.RgbSum[5], "gray stays gray");
            Equal(raster.RgbSum[3], raster.RgbSum[18], "one flat shade per face");
            // Buffers are cleared before the next frame.
            raster.Render(4, 2, new Egg.Pose { CenterX = 100, CenterY = 100, Scale = 2 }, [box], new Egg.Rgb(0, 0, 0), 0);
            Equal("0,0,0,0,0,0,0,0", string.Join(",", raster.Bits), "cleared");
            // A light background tints the cells with a quantized halo.
            raster.Render(4, 2, pose, [box], new Egg.Rgb(255, 255, 255), Egg.LIGHT_HALO_STRENGTH);
            // Cell 1 keeps its own coverage 1/8: round(0.3 * 0.125^0.7 * 32) / 32. Cell 0 only gets the blurred 0.0123, which
            // quantizes to nothing.
            Equal(2f / 32, raster.HaloAmount[1], "halo under the logo");
            Equal(0f, raster.HaloAmount[0], "faint halo quantized away");
            foreach (var amount in raster.HaloAmount) Near(Math.Round(amount * 32) / 32, amount, "halo quantized", 1e-6);
            Check(raster.HaloRgb[3] % 5 == 0, "halo color quantized to steps of 5");
        }));
        yield return ("egg.3d.covered-faces", Sync(() =>
        {
            var raster = new Egg.BlockRaster(10);
            var m = Egg.Rotation(-0.5, 0, 0);
            double[] origin = [m[6] * 10, m[7] * 10, m[8] * 10];
            var pose = new Egg.Pose { CenterX = 20, CenterY = 20, Scale = 2, Yaw = -0.5 };
            static string Describe(Egg.Face face) => face.Axis + ":" + face.Plane.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Egg.Box Block(double x) => new() { Min = [x, -0.5, -0.35], Max = [x + 1, 0.5, 0.35], Color = new Egg.Rgb(9, 9, 9) };
            var single = raster.VisibleFaces(m, origin, pose, [Block(-1)], 80, 80, false);
            Equal("0:0,2:0.35", string.Join(",", single.Select(Describe)), "right and front faces");
            var pair = raster.VisibleFaces(m, origin, pose, [Block(-1), Block(0)], 80, 80, false);
            Equal("2:0.35,0:1,2:0.35", string.Join(",", pair.Select(Describe)), "the touching face is hidden");
        }));
        yield return ("egg.3d.pose", Sync(() =>
        {
            var tui = AltScreen(80, 24);
            var animation = Animation(tui, Egg.ArminModel(new Egg.Rgb(1, 2, 3)), [], () => 0);
            var start = animation.Pose(80, 24, 0, 0);
            Equal(80.0, start.CenterX, "armin starts at the center x");
            Equal(46.0, start.CenterY, "armin starts at the center y");
            Equal(0.1, start.Scale, "START_SCALE");
            Equal(0.0, start.Pitch, "no tilt at progress 0");
            var radius = Math.Sqrt(15.5 * 15.5 + 18 * 18 + 1.35 * 1.35);
            var reach = radius * (80 / (80 - radius));
            Near(Math.Min(80 * 2 * 0.45, (24 * 4 - 8) * 0.48) / reach, animation.Pose(80, 24, 1, 0).Scale, "end scale", 1e-12);
            Near(Math.Sqrt(0.1 * (Math.Min(72, 42.24) / reach)), animation.Pose(80, 24, 0.5, 0).Scale, "geometric zoom", 1e-12);
            Stop(animation);
            var logo = Animation(tui, Egg.PiLogoModel((2, 1)), [], () => 0);
            var lift = logo.Pose(80, 24, 0, 0);
            Equal(8.0, lift.CenterX, "column * 2 + columns");
            Equal(8.0, lift.CenterY, "row * 4 + rows");
            Near(2 * (10 - 0.35) / 10, lift.Scale, "front face covers 2x2 dots per pixel", 1e-12);
            var mid = logo.Pose(80, 24, 0.5, 9.3);
            Equal(44.0, mid.CenterX, "halfway x");
            Equal(27.0, mid.CenterY, "halfway y");
            Stop(logo);
        }));
        yield return ("egg.3d.block-offsets", Sync(() =>
        {
            var model = Egg.PiLogoModel((0, 0));
            var animation = Animation(AltScreen(), model, [], () => 0);
            Check(animation.BlockOffsets(4.39).All(offset => offset == (0, 0, 0)), "home before the puzzle");
            var steps = Egg.ShuffleSteps(model, 0);
            // Halfway through step 2 (from steps[2] to steps[3]): block 4 slides half a column to the right.
            var offsets = animation.BlockOffsets(Egg.PUZZLE_START + 2.5 * Egg.PUZZLE_STEP);
            Near(0.5, offsets[4].X, "smooth(0.5) of one column");
            Near(-1, offsets[0].Z, "block 0 rests one layer back");
            Near(1, offsets[5].Z, "block 5 rests one layer forward");
            // Returning: all blocks fly home with alternating depth arcs.
            var returning = animation.BlockOffsets(Egg.PUZZLE_START + Egg.PUZZLE_STEP * Egg.PUZZLE_STEPS + 0.5);
            var last = steps[12];
            Near((last[0].X - 0) * 0.5, returning[0].X, "half way home");
            Near((last[0].Z - 0) * 0.5 + 0.8, returning[0].Z, "even blocks arc forward");
            Near((last[1].Z - 0) * 0.5 - 0.8, returning[1].Z, "odd blocks arc back");
            Check(animation.BlockOffsets(Egg.PUZZLE_START + 4.9).All(offset => offset == (0, 0, 0)), "home during the hold");
            Stop(animation);
        }));
        yield return ("egg.3d.prepare-cells-and-stars", Sync(() =>
        {
            var animation = Animation(AltScreen(20, 6), Egg.ArminModel(new Egg.Rgb(1, 2, 3)), ["hello", "中x"], () => 0);
            var cells = animation.PrepareCells(20, new Egg.Rgb(200, 200, 200));
            // The dust spreads from the center (10, 3); the corner is the farthest cell.
            Near(0.1 + 0.55 + Egg.Hash(0) * 0.12, cells[0][0].Delay, "corner delay", 1e-12);
            Equal(0.0, cells[0][0].Seed, "seed");
            Equal(4, cells[0][0].Ink, "ink of h");
            Equal(cells[1][0].Delay, cells[1][1].Delay, "wide glyph halves share the delay");
            Equal(Egg.GlyphInk("中", Egg.Hash(65_537 + 1)), cells[1][1].Ink, "second half ink uses its own seed");
            Stop(animation);
            var logo = Animation(AltScreen(20, 6), Egg.PiLogoModel((1, 0)), ["x▀▀▀ y", " ▀ ▀ \u001b[41mz"], () => 0);
            var replaced = logo.PrepareCells(20, new Egg.Rgb(200, 200, 200));
            Equal("x,  ,  ,  ,  ,y", string.Join(",", replaced[0].Select(cell => cell.Text == " " ? "  " : cell.Text)), "header logo cells blanked");
            Check(replaced[0].Skip(1).Take(4).All(cell => cell.Ink == 0 && cell.Bg is null), "blanked cells have no ink");
            Equal("z", replaced[1][5].Text, "outside the logo");
            Check(replaced[1][5].Bg is not null, "background kept outside the logo");
            Stop(logo);
            var stars = EasterEgg3dAnimation.PrepareStars(80, 24);
            var count = stars.Count(star => star is not null);
            Check(count > 80 * 24 * 0.005 && count < 80 * 24 * 0.04, $"sparse starfield ({count})");
            for (var index = 0; index < stars.Length; index++)
            {
                Equal(Egg.Hash(index * 3 + 0x51ed) < Egg.STAR_DENSITY, stars[index] is not null, "star placement");
                if (stars[index] is { } star)
                {
                    Check(Egg.DOT_BITS.Any(bit => star.Glyph == ((char)(0x2800 + bit)).ToString()), "single-dot braille");
                    Check(star.Strength is >= 0.1 and < 0.22 && star.Speed is >= 0.4 and < 1.2, "star ranges");
                }
            }
        }));
        yield return ("egg.3d.render-timeline", Sync(() =>
        {
            var clock = 1000.0;
            var finished = 0;
            var tui = AltScreen(20, 6);
            var animation = Animation(tui, Egg.ArminModel(new Egg.Rgb(150, 100, 250)), ["hello", "\u001b[44mworld\u001b[0m"], () => clock, () => finished++);
            var first = animation.Render(20);
            Equal(6, first.Count, "one line per terminal row");
            Equal("\u001b[38;2;200;200;200mhello" + new string(' ', 15) + "\u001b[0m", first[0], "row 0 untouched at time 0");
            Equal("\u001b[38;2;200;200;200m\u001b[48;2;0;0;128mworld\u001b[49m" + new string(' ', 15) + "\u001b[0m", first[1], "row 1 keeps its background");
            var speck = Strip(first[2]);
            Check(speck.Any(ch => ch is >= '⠁' and <= '⣿'), "Armin starts as a speck of braille at the center: " + speck);
            Equal(new string(' ', 20), Strip(first[4]), "no hint yet");
            // t = 0.8: every cell started turning to dust by 0.77 s, so no letter is left; the corner cell (delay 0.664) still
            // shows round(4 * (1 - 0.39)) = 2 dots.
            clock = 1800;
            var dusty = Strip(animation.Render(20));
            Check(!dusty[0].Any(char.IsAsciiLetter) && !dusty[1].Any(char.IsAsciiLetter), "text dissolved into dust: " + dusty[0] + "|" + dusty[1]);
            Check(dusty[0].Any(ch => ch is >= '⠁' and <= '⣿'), "braille dust");
            // t = 5: the screen has dissolved, the hint shows two rows from the bottom.
            clock = 6000;
            var late = animation.Render(20);
            Equal("escape to return", Strip(late[4]).Substring(2, 16), "hint centered on row height - 2");
            var muted = Egg.ToRgb(Themes.Current.TokenColors["muted"]);
            Contains(late[4], $"\u001b[38;2;{(int)Egg.JsRound(muted.R)};{(int)Egg.JsRound(muted.G)};{(int)Egg.JsRound(muted.B)}me", "key in the muted color");
            Check(!Strip(late[0]).Contains('h') && !Strip(late[1]).Contains('w'), "text layer gone");
            // Exit plays the timeline backwards; at the end the screen is back.
            animation.HandleInput("\u001b");
            Check(animation.IsExiting, "escape starts the exit");
            clock = 6000 + 1100;
            var back = animation.Render(20);
            Equal(first[0], back[0], "row 0 reassembled");
            Equal(first[1], back[1], "row 1 reassembled");
            Equal(new string(' ', 20), Strip(back[4]), "hint faded out");
            Equal(0, finished, "still running until the timer or a second close");
            animation.HandleMouse(new TuiMouseEvent(TuiMouseEventType.Click, TuiMouseButton.Left, 0, 0, 0, 0, 20, 6));
            Equal(1, finished, "a second close finishes");
            Check(!animation.IsRunning, "timer stopped");
            animation.Close();
            Equal(1, finished, "finishes once");
            var mouse = Animation(tui, Egg.PiLogoModel((0, 0)), [], () => clock);
            var result = mouse.HandleMouse(new TuiMouseEvent(TuiMouseEventType.Move, TuiMouseButton.None, 0, 0, 0, 0, 20, 6));
            Check(result is { Handled: true, Render: false } && !mouse.IsExiting, "mouse is captured; only clicks close");
            mouse.HandleInput("x");
            Check(!mouse.IsExiting, "other keys are ignored");
            mouse.HandleInput("\u0003");
            Check(mouse.IsExiting, "ctrl+c (app.clear) closes");
            Stop(mouse);
        }));

        // easter-egg-3d.lazy.ts
        yield return ("egg.lazy.fullscreen-only", Sync(() =>
        {
            Egg.ResetForTests();
            var loop = UiLoop.CreateManual();
            var main = new TuiMainScreen(new FakeTerminal(40, 10), loop);
            Check(!EasterEgg3dLazy.PlayArmin3d(main), "the main-screen renderer cannot play it");
            var terminal = new FakeTerminal(40, 10);
            var alt = new TuiAltScreen(terminal, loop);
            Check(EasterEgg3dLazy.PlayArmin3d(alt), "the fullscreen renderer plays it");
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!alt.HasOverlay() && DateTime.UtcNow < deadline)
            {
                loop.RunPending();
                Thread.Sleep(20);
            }
            Check(alt.HasOverlay(), "the animation is shown as an overlay after the terminal color query");
            Check(terminal.Writes.Any(write => write.Contains("\u001b]11;?\u0007", StringComparison.Ordinal)), "terminal colors were queried");
            var animation = alt.FocusedComponent as EasterEgg3dAnimation;
            Check(animation is not null, "the overlay takes focus");
            Check(EasterEgg3dLazy.PlayArmin3d(alt), "already showing an overlay: reported as played");
            animation!.Close();
            animation.Close();
            Check(!alt.HasOverlay(), "finishing hides the overlay");
            Egg.ResetForTests();
        }));
    }
}
