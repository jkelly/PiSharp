// Decision 0008: every public type of the durable device server is experimental, like the durable port it builds on (decision 0005).
// Consumers opt in by suppressing PISHARP_DURABLE001.
[assembly: System.Diagnostics.CodeAnalysis.Experimental("PISHARP_DURABLE001")]
