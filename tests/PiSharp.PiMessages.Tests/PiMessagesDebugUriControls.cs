using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Protocols.PiMessages;
using PiSharp.Contracts;

// Additional authored native controls; frozen source-informed fixtures remain unchanged.
internal static class PiMessagesDebugUriControls
{
    internal sealed record Outcome(string Id, string Status, string? Error);
    private static readonly ModelDescriptor Model = new("uri-control", "pi-messages", "authored-provider");
    internal static async Task<IReadOnlyList<Outcome>> RunAsync()
    {
        var outcomes = new List<Outcome>();
        foreach (var (id, run) in new (string, Func<Task>)[]
        {
            ("pi-debug-uri.exact-form-escapes-through-owned-request", ExactEscapes),
            ("pi-debug-uri.fragment-separation-remains-ordinary", FragmentSeparation),
            ("pi-debug-uri.bounded-validated-url-admission", BoundsAndAdmission)
        })
        {
            try { await run(); outcomes.Add(new(id, "PASS_AUTHORED_NATIVE_ONLY", null)); }
            catch (Exception error) { outcomes.Add(new(id, "FAIL", error.Message)); }
        }
        return outcomes;
    }
    private static JsonData Metadata(string url) => JsonData.Parse(JsonSerializer.Serialize(new
    { id = Model.Id, api = Model.Api, provider = Model.Provider, baseUrl = url }));
    private static PiMessagesOptions Options(string url) => new(Metadata(url), "authored-inert-noncredential")
    { Debug = true, EnvironmentLookup = _ => null };
    private static ChatRequest Request() => new(Model, [], 123);
    private static void Equal(string expected, string? actual)
    { if (expected != actual) throw new InvalidOperationException($"Expected {expected}, got {actual}."); }
    private static async Task ExactEscapes()
    {
        foreach (var (url, expected) in new[]
        {
            ("https://pi-messages.invalid/base?d%65bug=old&q=a%20b~%2B&empty=&flag",
                "https://pi-messages.invalid/base?debug=1&q=a+b%7E%2B&empty=&flag%2Fmessages="),
            ("https://pi-messages.invalid/base?q=%23part~%2B",
                "https://pi-messages.invalid/base?q=%23part%7E%2B%2Fmessages&debug=1"),
            ("https://pi-messages.invalid/base?debug=old&x=%2B&debug=more",
                "https://pi-messages.invalid/base?debug=1&x=%2B"),
            ("https://pi-messages.invalid/base?q=%E2%82%AC",
                "https://pi-messages.invalid/base?q=%E2%82%AC%2Fmessages&debug=1"),
            ("https://PI-MESSAGES.INVALID/a/../base?q=~",
                "https://pi-messages.invalid/base?q=%7E%2Fmessages&debug=1"),
            ("https://pi-messages.invalid/base?q=%0D%0A~",
                "https://pi-messages.invalid/base?q=%0D%0A%7E%2Fmessages&debug=1")
        })
        {
            var factory = new PiMessagesKeyAuthRequestFactory(Model, Options(url));
            Equal(expected, factory.Endpoint.AbsoluteUri);
            using var owned = await factory.CreateAsync(Request());
            Equal(expected, owned.RequestUri!.AbsoluteUri);
            Equal(expected["https://pi-messages.invalid".Length..], owned.RequestUri.PathAndQuery);
            Equal("", owned.RequestUri.Fragment); Equal("https", owned.RequestUri.Scheme);
            Equal("pi-messages.invalid", owned.RequestUri.Host); Equal("", owned.RequestUri.UserInfo);
        }
    }
    private static async Task FragmentSeparation()
    {
        var factory = new PiMessagesKeyAuthRequestFactory(Model, Options("https://pi-messages.invalid/base?debug=0&debug=2#frag"));
        Equal("https://pi-messages.invalid/base?debug=1#frag/messages", factory.Endpoint.AbsoluteUri);
        using var owned = await factory.CreateAsync(Request());
        Equal("/base?debug=1", owned.RequestUri!.PathAndQuery);
        Equal("#frag/messages", owned.RequestUri.Fragment);
    }
    private static Task BoundsAndAdmission()
    {
        foreach (var url in new[] { "ftp://pi-messages.invalid/base", "https://user:password@pi-messages.invalid/base", "relative/path" })
            Reject(PiMessagesFailure.InvalidConfiguration, () => new PiMessagesKeyAuthRequestFactory(Model, Options(url)));
        var options = Options("https://pi-messages.invalid/base?q=" + new string('\u20AC', 256));
        // Model JSON is admitted at its exact raw UTF-8 budget; form-encoded URL
        // expansion exceeds it and must fail before any request or handler effect.
        var rawBytes = Encoding.UTF8.GetByteCount(options.ModelMetadata.ToString());
        Reject(PiMessagesFailure.ResourceLimit, () => new PiMessagesKeyAuthRequestFactory(Model, options with { MaximumPayloadBytes = rawBytes }));
        return Task.CompletedTask;
    }
    private static void Reject(PiMessagesFailure expected, Action action)
    {
        try { action(); }
        catch (PiMessagesException error) when (error.Failure == expected) { return; }
        throw new InvalidOperationException("Expected Pi Messages admission failure " + expected + ".");
    }
}
