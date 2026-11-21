// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/durable/test/codemode-describe.test.ts, the cases v1.6.0 added when code-mode
// namespaces moved from name prefixes to extensions (Extension.namespace), as sync160.codemode.<case>. The cases v1.6.0 rewrote are in
// CodemodeDescribeCases.cs and CodemodeCases.cs under their new upstream names. Verbatim apart from the adaptations named there, and these:
// - namespacesOf is the public DurableCodemode.NamespacesOf (upstream exports it from `@earendil-works/pi-durable/codemode`); `toBe` of a
//   namespace is reference equality, and `map.has(name)` is ContainsKey.
// - Authored (no upstream case): the-removed-prefix-api-has-no-fallback pins the migration the changelog describes, that a tool whose name
//   starts with a server prefix belongs to no namespace unless its extension declares one; a-namespace-two-extensions-declare-is-listed-once
//   pins declaredNamespaces' "first declaration of a name wins"; namespaces-are-sorted-by-name pins the order of the headings.
using PiSharp.Durable;
using PiSharp.Durable.Tools;
using static CodemodeDescribeTests;
using static Program;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> Sync160CodemodeCases() => Sync160CodemodeTests.Cases();
}

/// <summary>The sync160.codemode.* cases (upstream codemode-describe.test.ts, v1.6.0 additions).</summary>
internal static class Sync160CodemodeTests
{
    internal static IEnumerable<(string, Func<Task>)> Cases()
    {
        const string prefix = "sync160.codemode.";

        yield return Case(prefix + "puts-a-tool-in-its-extension-s-namespace-the-first-extension-naming-a-tool-wins", () =>
        {
            var issues = Tool("list_issues", "List issues.");
            var shared = Tool("read", "Read a file.");
            var map = DurableCodemode.NamespacesOf([Ext("coding", [shared]), Ext("github", [issues, shared], GitHub)]);
            Check(ReferenceEquals(GitHub, map["list_issues"]), "list_issues is github's");
            // `read` came first from an extension without a namespace: ungrouped.
            Check(!map.ContainsKey("read"), "read is ungrouped");
            Check(ReferenceEquals(GitHub, DurableCodemode.NamespacesOf([Ext("github", [issues, shared], GitHub), Ext("coding", [shared])])["read"]),
                "read is github's when github names it first");
        });

        yield return Case(prefix + "lists-a-namespace-without-tools-and-nothing-without-tools-and-namespaces", () =>
        {
            var section = CodemodeDescribe.Section([], [Ext("github", [], GitHub)], 3000)!;
            Contains(section, "## mcp__github\nGitHub MCP server", "section");
            Equal<string?>(null, CodemodeDescribe.Section([], [Ext("coding", [])], 3000), "no tools and no namespaces");
        });

        yield return Case(prefix + "keeps-the-section-identical-before-and-after-a-namespace-s-deferred-tools-appear", () =>
        {
            var read = Tool("read", "Read a file.");
            var before = CodemodeDescribe.Section([read], [Ext("coding", [read]), Ext("github", [], GitHub)], 3000);
            // The server connected: its tools are installed, deferred, and callable by scripts.
            var issues = Tool("list_issues", "List issues.", tool => tool with { Deferred = true });
            Extension[] extensions = [Ext("coding", [read]), Ext("github", [issues], GitHub)];
            // The section's caller passes the non-deferred callable tools (deferred ones are found with searchTools()).
            var after = CodemodeDescribe.Section([read], extensions, 3000);
            Equal(before, after, "section");
        });

        yield return Case(prefix + "the-removed-prefix-api-has-no-fallback", () =>
        {
            // v1.5.2 put `mcp__github__list_issues` in a namespace by its prefix. Now only the extension decides.
            var named = Tool("mcp__github__list_issues", "List issues.");
            var section = CodemodeDescribe.Section([named], [Ext("tools", [named])], 3000)!;
            Lacks(section, "\n## ", "section without a namespace");
            Contains(section, "### `mcp__github__list_issues`", "section");
            Equal(0, DurableCodemode.NamespacesOf([Ext("tools", [named])]).Count, "namespaces");
        });

        yield return Case(prefix + "a-namespace-two-extensions-declare-is-listed-once", () =>
        {
            var first = Tool("list_issues", "List issues.");
            var second = Tool("get_issue", "Get an issue.");
            var other = new ToolNamespace("mcp__github", "Another description");
            var section = CodemodeDescribe.Section([first, second], [Ext("github-a", [first], GitHub), Ext("github-b", [second], other)], 3000)!;
            Equal(1, section.Split("## mcp__github").Length - 1, "headings");
            Contains(section, "## mcp__github\nGitHub MCP server", "the first declaration's description");
            Lacks(section, "Another description", "section");
            // Both extensions' tools are under the one heading.
            Check(section.IndexOf("## mcp__github", StringComparison.Ordinal) < section.IndexOf("### `list_issues`", StringComparison.Ordinal), "first tool");
            Check(section.IndexOf("## mcp__github", StringComparison.Ordinal) < section.IndexOf("### `get_issue`", StringComparison.Ordinal), "second tool");
        });

        yield return Case(prefix + "namespaces-are-sorted-by-name", () =>
        {
            var section = CodemodeDescribe.Section([],
                [Ext("z", [], new ToolNamespace("zeta", "Last.")), Ext("a", [], new ToolNamespace("alpha", "First."))], 3000)!;
            Equal("Tools scripts can call (`await tools.<name>(args)`):\n\n## alpha\nFirst.\n\n## zeta\nLast.", section, "section");
        });
    }
}
