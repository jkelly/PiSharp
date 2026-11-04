using PiSharp.Chord.Documents;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/documents/fence.test.ts
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> DocumentsFenceCases() =>
    [
        Case("documents.fence.seal-tokens-start-open-and-stay-sealed-once-sealed", () =>
        {
            var token = new SealToken();
            Equal(false, token.Sealed, "a new token");
            token.Seal();
            token.Seal();
            Equal(true, token.Sealed, "a sealed token");
        }),
    ];
}
