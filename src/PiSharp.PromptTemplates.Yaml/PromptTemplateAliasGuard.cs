using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using System.Diagnostics.CodeAnalysis;

namespace PiSharp.PromptTemplates.Yaml;

/// <summary>Alias accounting over standard parser events, before object conversion.
/// No YAML scanning/grammar and no recursive expansion of referenced collections.</summary>
internal static class PromptTemplateAliasGuard
{
    private const int Limit = 100;
    private sealed class Node(bool collection, ParsingEvent source)
    {
        internal bool Collection { get; } = collection;
        internal ParsingEvent Source { get; } = source;
        internal List<Node> Children { get; } = [];
        internal List<Node> References { get; } = [];
        internal Node? Parent;
        internal Node? Target;
        internal bool Anchored;
        internal int Count, Weight, SubtreeWeight;
    }

    internal static (int SyntaxNodes, long WeightVisits, long ReferenceVisits) Validate(string yaml)
    {
        using var reader = new StringReader(yaml);
        var parser = new Parser(reader);
        var anchors = new Dictionary<AnchorName, Node>();
        var parents = new Stack<Node>();
        var open = new HashSet<Node>();
        var order = new List<Node>();
        var documents = 0;
        while (parser.MoveNext())
        {
            var current = parser.Current!;
            if (current is DocumentStart)
            {
                if (++documents > 1) Reject(current, "Prompt metadata requires a single YAML document.");
                continue;
            }
            if (current is MappingEnd or SequenceEnd)
            { open.Remove(parents.Pop()); continue; }
            if (current is not (NodeEvent or AnchorAlias)) continue;
            var node = new Node(current is MappingStart or SequenceStart, current);
            if (parents.TryPeek(out var parent)) { parent.Children.Add(node); node.Parent = parent; }
            order.Add(node);
            if (current is AnchorAlias alias)
            {
                if (!anchors.TryGetValue(alias.Value, out var target))
                    Reject(current, "Prompt metadata alias must follow its anchor.");
                if (open.Contains(target)) Reject(current, "Cyclic prompt metadata aliases are unsupported.");
                node.Target = target;
            }
            else if (current is NodeEvent syntax && !syntax.Anchor.IsEmpty)
            { node.Anchored = true; anchors[syntax.Anchor] = node; }
            if (node.Collection) { parents.Push(node); open.Add(node); }
        }

        long weightVisits = 0, referenceVisits = 0;
        // Initial maxima over physical syntax edges, once bottom-up. Alias leaves start at
        // zero until conversion visits them. Never freeze a collection at parse completion:
        // its alias descendants may increase before this anchor's first reference.
        for (var index = order.Count - 1; index >= 0; index--)
        {
            var node = order[index]; weightVisits++;
            node.SubtreeWeight = !node.Collection && node.Target is null ? 1 : 0;
            foreach (var child in node.Children)
            { weightVisits++; node.SubtreeWeight = Math.Max(node.SubtreeWeight, child.SubtreeWeight); }
        }
        // Node yaml 2.9 initializes each anchor count to one, increments it for each alias,
        // and lazily caches a weight: scalar=1, collection=max(child weights),
        // alias=target count * cached weight. A mapping pair's max is the same as
        // flattening its key/value children. Saturating at Limit+1 avoids overflow.
        foreach (var node in order)
        {
            if (node.Anchored) node.Count = 1;
            if (node.Target is not { } target) continue;
            target.Count = Math.Min(Limit + 1, target.Count + 1);
            if (target.Weight == 0) target.Weight = Math.Max(1, target.SubtreeWeight);
            if (Product(target.Count, target.Weight) > Limit)
                Reject(node.Source, "Prompt metadata exceeds the alias conversion limit of 100.");
            target.References.Add(node);
            // Each accepted anchor has at most99 references (minimum weight one).
            // Refresh its already converted alias leaves as its count increases; never
            // follow alias targets or walk a target's nested subtree again.
            foreach (var reference in target.References)
            { referenceVisits++; Increase(reference, Product(target.Count, target.Weight)); }
        }
        return (order.Count, weightVisits, referenceVisits);

        void Increase(Node node, int weight)
        {
            // Cached syntax maxima only increase and saturate at101. Each parent edge
            // can therefore propagate at most101 increases across the complete pass.
            while (true)
            {
                weightVisits++;
                if (weight <= node.SubtreeWeight) return;
                node.SubtreeWeight = weight;
                if (node.Parent is not { } parent) return;
                node = parent;
            }
        }
    }
    private static int Product(int count, int weight) => Math.Min(Limit + 1, count * weight);
    [DoesNotReturn]
    private static void Reject(ParsingEvent source, string message) =>
        throw new YamlException(source.Start, source.End, message);
}
