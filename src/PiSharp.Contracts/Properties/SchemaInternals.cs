using System.Runtime.CompilerServices;

// The JavaScript value model, regular expressions and pi-schema port under Schema/ serve PiSharp.Agent (tool argument validation
// and the TypeBox port behind TypeBoxJsonSchema) and the pi-schema suite, which checks the engine on the JavaScript value model.
[assembly: InternalsVisibleTo("PiSharp.Agent")]
[assembly: InternalsVisibleTo("PiSharp.Schema.Tests")]
