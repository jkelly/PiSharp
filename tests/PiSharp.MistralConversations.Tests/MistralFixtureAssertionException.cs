using System.Text.Json;
using PiSharp.AI.Protocols.MistralConversations;
using PiSharp.Contracts;

// Report only fixed categories and owned assertion locations. Never include a
// callback/HTTP exception message, stack, raw payload, header or credential.
internal sealed class MistralFixtureAssertionException(string fixture, int line, string? subcase = null) : Exception("Authored assertion failed.")
{
    internal string Fixture { get; } = fixture;
    internal int Line { get; } = line;
    internal string? Subcase { get; } = subcase is { Length: > 64 } ? "unrecognized-subcase" : subcase;
    internal static string Diagnostic(Exception error) => error switch
    {
        MistralFixtureAssertionException => "Authored assertion failed at owned fixture location.",
        TimeoutException => "Authored fixture deadline elapsed.",
        MistralTextException native => "Mistral fixture failure code: " + native.Code,
        StreamProtocolException => "Authored fixture stream protocol exception.",
        JsonException => "Authored fixture JSON exception.",
        InvalidCastException => "Authored fixture unexpected event shape.",
        InvalidOperationException => "Authored fixture invalid operation.",
        _ => "Authored fixture unexpected exception; details withheld."
    };
}
