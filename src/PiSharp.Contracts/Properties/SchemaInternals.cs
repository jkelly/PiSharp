using System.Runtime.CompilerServices;

// The JavaScript value model, regular expressions and pi-schema port under Schema/ serve PiSharp.Agent (tool argument validation
// and the TypeBox port behind TypeBoxJsonSchema) and the pi-schema suite, which checks the engine on the JavaScript value model.
[assembly: InternalsVisibleTo("PiSharp.Agent")]
[assembly: InternalsVisibleTo("PiSharp.Schema.Tests")]
// Chord v2's schema tests (decision 0013, phase 4) check bytes() schemas against byte arrays, which only the JavaScript value model holds.
[assembly: InternalsVisibleTo("PiSharp.Chord.Tests")]
