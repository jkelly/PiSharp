using PiSharp.Durable.Chord.Examples;

var examples = args.Length == 0 ? new[] { "35", "36", "37" } : args;
foreach (var example in examples)
{
    switch (example)
    {
        case "35": await ChordExamples.StoreAsync(Console.Out); break;
        case "36": await ChordExamples.ViewsAsync(Console.Out); break;
        case "37": await ChordExamples.RegistryAsync(Console.Out); break;
        default: throw new ArgumentException("Choose example 35, 36 or 37 (no arguments runs all three).");
    }
}
