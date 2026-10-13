using PiSharp.Cli.Durable;
using PiSharp.Durable;

internal static partial class Program
{
    // The runtime/TUI contract compiles and its value types behave as plain records.
    private static IEnumerable<(string, Func<Task>)> SetupCases() =>
    [
        Case("setup.contract.model-summary-ref", () =>
        {
            var model = new DurableModelSummary("anthropic", "claude-sonnet-4-5", "Claude Sonnet 4.5", 200_000);
            Equal(new ModelRef("anthropic", "claude-sonnet-4-5"), model.Ref, "ref");
        }),
    ];
}
