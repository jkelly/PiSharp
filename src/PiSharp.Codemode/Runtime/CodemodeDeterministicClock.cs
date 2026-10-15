// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/codemode/src/runtime/vm.ts (deterministic mode: deterministicWasi,
// VirtualClock, the UTC timezone offset and the intrinsics without WEAK_REF). QuickJS gets its determinism from WASI imports; Jint
// gets it from its time system, culture and Temporal time zone provider, a seeded Math.random and a shim evaluated before the prelude.
using System.Globalization;
using System.Numerics;
using Jint;
using Jint.Native;
using Jint.Native.Temporal;
using Jint.Runtime;
using Jint.Runtime.Interop;

namespace PiSharp.Codemode;

/// <summary>
/// The deterministic engine of <see cref="CodemodeSandbox.Start"/>: a script must make the same calls with the same arguments every
/// time it runs with the same results, so a host can replay it after a restart. The engine is otherwise deterministic, because it only
/// runs when the host delivers something and has no timers. What remains is fixed here:
/// <list type="bullet">
/// <item>Time: <c>Date</c>, <c>Temporal.Now</c> and <c>performance.now()</c> read virtual time, <c>StartedAt</c> and then the <c>now</c>
/// of the result being processed, never going backwards. As in QuickJS, <c>Date.now()</c> reads it rounded to a millisecond and
/// <c>performance.now()</c> reads the rounded milliseconds since <c>StartedAt</c>.</item>
/// <item>Randomness: <c>Math.random()</c> is QuickJS's xorshift64* generator in the state quickjs-wasi gives it for the seed
/// (<c>seed + 1</c>, with the one draw the context consumes while it is created), so a seed yields upstream's values. Jint has no other
/// source of randomness a script can reach once <c>ShadowRealm</c>, whose realm has its own <c>Math.random</c>, is removed.</item>
/// <item>Timezone and locale: UTC (Date, Temporal's default zone, and <c>Intl.DateTimeFormat</c>, which Jint otherwise bases on the
/// host's zone) and the invariant culture, not the host's.</item>
/// <item>Garbage collection: <c>WeakRef</c> and <c>FinalizationRegistry</c> are not available; collection timing is not.</item>
/// </list>
/// The runner applies results in <c>seq</c> order and reports <c>started</c> and <c>idle</c>.
/// </summary>
internal sealed class CodemodeDeterministicClock
{
    private readonly double startedAt;
    private ulong state;

    public CodemodeDeterministicClock(CodemodeDeterministicStart start)
    {
        startedAt = start.StartedAt;
        Now = start.StartedAt;
        // QuickJS seeds Math.random() from the realtime clock in microseconds, which quickjs-wasi's deterministic WASI shim makes read
        // seed + 1 while the context is created; creating the context then draws once.
        state = start.Seed + 1UL;
        NextRandom();
    }

    /// <summary>The clock in ms since the epoch: <c>StartedAt</c>, then the latest <c>now</c> of a processed result.</summary>
    public double Now { get; set; }

    /// <summary>quickjs.c js_math_random: xorshift64*, its top 52 bits as a double in [0, 1).</summary>
    private double NextRandom()
    {
        var x = state;
        x ^= x >> 12;
        x ^= x << 25;
        x ^= x >> 27;
        state = x;
        var value = x * 0x2545F4914F6CDD1DUL;
        return BitConverter.UInt64BitsToDouble((0x3FFUL << 52) | (value >> 12)) - 1.0;
    }

    /// <summary>JavaScript's Math.round, as vm.ts applies it to the clock.</summary>
    private static double Round(double value) => Math.Floor(value + 0.5);

    /// <summary>The range of <see cref="DateTimeOffset"/>, which Jint's time system returns; the clock saturates at its ends.</summary>
    private static readonly double Earliest = DateTimeOffset.MinValue.ToUnixTimeMilliseconds(), Latest = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();

    private sealed class VirtualTime(CodemodeDeterministicClock clock) : DefaultTimeSystem(TimeZoneInfo.Utc, CultureInfo.InvariantCulture)
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Clamp(Round(clock.Now), Earliest, Latest));
    }

    /// <summary>Temporal's default zone is the host's unless the provider says otherwise; every other zone resolves as usual.</summary>
    private sealed class UtcDefaultZone : ITimeZoneProvider
    {
        private static readonly ITimeZoneProvider Zones = DefaultTimeZoneProvider.Instance;
        public long GetOffsetNanosecondsFor(string timeZone, BigInteger epochNanoseconds) => Zones.GetOffsetNanosecondsFor(timeZone, epochNanoseconds);
        public BigInteger[] GetPossibleInstantsFor(string timeZone, int year, int month, int day, int hour, int minute, int second, int millisecond, int microsecond, int nanosecond) =>
            Zones.GetPossibleInstantsFor(timeZone, year, month, day, hour, minute, second, millisecond, microsecond, nanosecond);
        public BigInteger? GetNextTransition(string timeZone, BigInteger epochNanoseconds) => Zones.GetNextTransition(timeZone, epochNanoseconds);
        public BigInteger? GetPreviousTransition(string timeZone, BigInteger epochNanoseconds) => Zones.GetPreviousTransition(timeZone, epochNanoseconds);
        public bool IsValidTimeZone(string timeZone) => Zones.IsValidTimeZone(timeZone);
        public string? CanonicalizeTimeZone(string timeZone) => Zones.CanonicalizeTimeZone(timeZone);
        public IReadOnlyCollection<string> GetAvailableTimeZones() => Zones.GetAvailableTimeZones();
        public string GetDefaultTimeZone() => "UTC";
        public string? GetPrimaryTimeZoneIdentifier(string timeZone) => Zones.GetPrimaryTimeZoneIdentifier(timeZone);
    }

    /// <summary>Engine options of a deterministic engine.</summary>
    public void Configure(Options options)
    {
        options.TimeZone = TimeZoneInfo.Utc;
        options.TimeSystem = new VirtualTime(this);
        options.Culture = CultureInfo.InvariantCulture;
        options.Temporal.TimeZoneProvider = new UtcDefaultZone();
    }

    /// <summary>Evaluated after the engine shim and before the prelude, whose lockdown then freezes what it defines.</summary>
    internal const string Shim = """
        (function (random, now) {
          "use strict";
          const define = (object, name, value) => Object.defineProperty(object, name, { value, writable: true, enumerable: false, configurable: true });
          delete globalThis.WeakRef;
          delete globalThis.FinalizationRegistry;
          delete globalThis.ShadowRealm;
          define(Math, "random", random);
          const performance = {};
          Object.defineProperty(performance, "now", { value: now, writable: true, enumerable: true, configurable: true });
          define(globalThis, "performance", performance);
          const original = Intl.DateTimeFormat;
          const construct = Reflect.construct, create = Object.create, toObject = Object;
          const utc = (options) => {
            if (options === undefined) return { timeZone: "UTC" };
            if (options === null) return options;
            const object = toObject(options);
            return object.timeZone === undefined
              ? create(object, { timeZone: { value: "UTC", writable: true, enumerable: true, configurable: true } })
              : options;
          };
          const DateTimeFormat = function DateTimeFormat(locales, options) {
            return construct(original, [locales, utc(options)], new.target === undefined ? original : new.target);
          };
          Object.defineProperty(DateTimeFormat, "length", { value: original.length, configurable: true });
          DateTimeFormat.prototype = original.prototype;
          define(original.prototype, "constructor", DateTimeFormat);
          define(DateTimeFormat, "supportedLocalesOf", original.supportedLocalesOf);
          define(Intl, "DateTimeFormat", DateTimeFormat);
        })
        """;

    /// <summary>Installs <c>Math.random</c>, <c>performance.now</c> and the removals of <see cref="Shim"/>.</summary>
    public void Install(Engine engine)
    {
        var random = new ClrFunction(engine, "random", (_, _) => new JsNumber(NextRandom()));
        var now = new ClrFunction(engine, "now", (_, _) => new JsNumber(Round(Now - startedAt)));
        engine.Call(engine.Evaluate(Shim, "codemode-deterministic.js"), JsValue.Undefined, [random, now]);
    }
}
