using System.Text;
using PiSharp.Chord.Store;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/store/text.test.ts. Every case is ported; JavaScript's
// \u{...} escapes are char.ConvertFromUtf32 here, and sort(compareText) a stable OrderBy with Tables.CompareText.
internal static partial class Program
{
    private static readonly Comparer<string> StoreCodePointOrder = Comparer<string>.Create(Tables.CompareText);

    private static string StoreU(int codePoint) => char.ConvertFromUtf32(codePoint);

    private static IEnumerable<(string, Func<Task>)> StoreTextCases() =>
    [
        Case("store.text.compare-text-orders-by-code-point-a-supplementary-character-after-every-bmp-character-u-e000-to-u-ffff-included", () =>
        {
            string[] texts = [StoreU(0x10000), "\uFFFF", "\uE000", "\uD7FF", "z", "a", "", "ab"];
            Sequence(["", "a", "ab", "z", "\uD7FF", "\uE000", "\uFFFF", StoreU(0x10000)], texts.OrderBy(text => text, StoreCodePointOrder), "sorted");
            Equal(0, Tables.CompareText("a", "a"), "a and a");
            Check(Tables.CompareText(StoreU(0x1F600), StoreU(0x1F601)) < 0, "U+1F600 before U+1F601");
        }),
        Case("store.text.compare-text-agrees-with-utf-8-byte-order", () =>
        {
            string[] texts = [StoreU(0x1F600), "\uFF5E", "\u00E9", "e", StoreU(0x10FFFF), "\uE000", "aa", "a"];
            var byBytes = texts.OrderBy(text => text, Comparer<string>.Create((a, b) =>
            {
                var (x, y) = (Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
                for (var i = 0; i < Math.Min(x.Length, y.Length); i++) if (x[i] != y[i]) return x[i] - y[i];
                return x.Length - y.Length;
            }));
            Sequence(byBytes, texts.OrderBy(text => text, StoreCodePointOrder), "by code point");
        }),
        Case("store.text.stored-text-keeps-well-formed-text-as-it-is-and-replaces-a-lone-surrogate-with-u-fffd", () =>
        {
            var text = "a" + StoreU(0x1F600) + "b";
            Equal(text, StoreInternal.StoredText(text, "v"), "well-formed text");
            Equal("a\uFFFDb", StoreInternal.StoredText("a\uD800b", "v"), "a lone surrogate");
        }),
        Case("store.text.stored-text-refuses-nul-with-invalid-args", () =>
        {
            StoreArgs(() => StoreInternal.StoredText("a\0b", "edits.path"));
        }),
        Case("store.text.prefix-upper-bound-is-the-next-code-point-after-the-last-one", () =>
        {
            Equal("a0", StoreInternal.PrefixUpperBound("a/"), "a/");
            Equal("a" + StoreU(0x1F601), StoreInternal.PrefixUpperBound("a" + StoreU(0x1F600)), "a U+1F600");
            Equal(StoreU(0x10000), StoreInternal.PrefixUpperBound("\uFFFF"), "U+FFFF");
        }),
        Case("store.text.prefix-upper-bound-skips-the-surrogates-and-carries-past-u-10ffff", () =>
        {
            Equal("\uE000", StoreInternal.PrefixUpperBound("\uD7FF"), "U+D7FF");
            Equal("b", StoreInternal.PrefixUpperBound("a" + StoreU(0x10FFFF)), "a U+10FFFF");
            Equal(null, StoreInternal.PrefixUpperBound(StoreU(0x10FFFF) + StoreU(0x10FFFF)), "U+10FFFF U+10FFFF");
            Equal(null, StoreInternal.PrefixUpperBound(""), "the empty prefix");
        }),
        Case("store.text.prefix-upper-bound-bounds-exactly-the-strings-starting-with-the-prefix-in-code-point-order", () =>
        {
            string[] texts = ["a", "a/", "a/x", "a/" + StoreU(0x1F600), "a/\uFFFF", "a0", "a.", "b"];
            foreach (var prefix in new[] { "a/", "a", "a/\uFFFF" })
            {
                var upper = StoreInternal.PrefixUpperBound(prefix)!;
                var inRange = texts.Where(text => Tables.CompareText(text, prefix) >= 0 && Tables.CompareText(text, upper) < 0);
                Sequence(texts.Where(text => text.StartsWith(prefix, StringComparison.Ordinal)), inRange, $"the range of \"{prefix}\"");
            }
        }),
    ];
}
