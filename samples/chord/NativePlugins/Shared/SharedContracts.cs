using PiSharp.Chord.Contracts;
using PiSharp.Chord.Registries;

namespace NativePluginSample;

public static class SharedContracts
{
    public static Registry<string, string[]> Greetings { get; } = Registry.Define<string, string[]>("sample.greetings", new() { Version = 1 });
    public static Contract Counter { get; } = Contract.Local("sample.counter", new() { Version = 1, Params = [], Members = new() { ["greet"] = Schemas.Method(null, Schemas.Str()) } });
}
