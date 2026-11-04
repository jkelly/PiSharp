// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/state/index.ts, which only re-exports: state for apps, the
// public types of PiSharp.Chord.State. Watchable (from src/watch.ts) is PiSharp.Chord's IWatchable<T> (Watch.cs, Chord v2's port of
// watch.ts); mapShared and constant are Watchables.MapShared (derive.ts) and Watchables.Constant (watchable.ts); NativeObservation and
// NativeObserver are NativeObservation<T> and INativeObserver<T> (native-source.ts); Writable is IWritable<T> (writable.ts). A JSON value
// is a JsonNode (Json.cs). The README's rules hold: a derive's compute is synchronous and pure and reads only its declared sources; a
// watch ends only for good; a native source is compared by identity and never a state member.
namespace PiSharp.Chord.State;
