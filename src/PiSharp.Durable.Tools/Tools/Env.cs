// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/src/tools/env.ts
using PiSharp.Durable.Env;

namespace PiSharp.Durable.Tools;

/// <summary>Upstream <c>requireEnv</c>.</summary>
internal static class ToolEnv
{
    /// <summary>The call's execution environment; a tool without one fails with an ordinary error result.</summary>
    public static IExecutionEnv Require(IToolExecutionApi api) =>
        api.Env ?? throw new InvalidOperationException("No execution environment is configured");
}
