using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Facade.Context;
using PiSharp.Extensions.Runtime.Facade.Context;

// 1.1.0.3 parity leftovers: an extension's setModel applies the settings' thinking level for the new model (agent-session.ts setModel,
// _getThinkingLevelForModelSwitch, setThinkingLevel/_clampThinkingLevel) and a native extension's setThinkingLevel clamps any name
// (models.ts clampThinkingLevel). Expectations are written from the pinned sources by reading, never captured from an upstream run.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> LeftoverCases() =>
    [
        ("leftovers.set-model-applies-the-settings-thinking-level", SetModelAppliesSettingsThinking),
        ("leftovers.native-set-thinking-level-clamps-unknown-names", NativeSetThinkingLevelClamps),
    ];

    // agent-session.ts bindExtensions setModel -> setModel: the level is _getThinkingLevelForModelSwitch(model) (the per-model setting,
    // else defaultThinkingLevel, else the current level), clamped to the model by setThinkingLevel; anthropic/claude-haiku-4-5 supports
    // off..high and anthropic/claude-haiku-5-5 low..max.
    private static async Task SetModelAppliesSettingsThinking()
    {
        using var sandbox = NodeSandbox("set-model-thinking");
        sandbox.Write(Path.Combine(sandbox.AgentDir, "settings.json"),
            """{"defaultThinkingLevel":"minimal","modelThinkingLevels":{"anthropic/claude-haiku-4-5":"high"}}""");
        var extension = sandbox.Write(Path.Combine(sandbox.Cwd, "switch.ts"), Probe + """
            export default function (pi: any) {
              pi.registerCommand("switch", { description: "Switch", handler: async (_args: string, ctx: any) => {
                log("start", pi.getThinkingLevel());
                log("haiku45", await pi.setModel(ctx.modelRegistry.find("anthropic", "claude-haiku-4-5")), pi.getThinkingLevel());
                log("haiku55", await pi.setModel(ctx.modelRegistry.find("anthropic", "claude-haiku-5-5")), pi.getThinkingLevel());
                log("sonnet", await pi.setModel(ctx.modelRegistry.find("anthropic", "claude-sonnet-4-5")), pi.getThinkingLevel());
              } });
            }
            """);
        var (code, records, stderr) = await RunRpc(sandbox, [.. Model, "-e", extension],
            ["""{"id":"s","type":"prompt","message":"/switch"}"""], (record, _) => IsResponse(record, "state"),
            react: (record, push) => { if (IsResponse(record, "s")) push("""{"id":"state","type":"get_state"}"""); });
        Equal(0, code, "rpc exit; " + stderr);
        Check(records.Single(record => IsResponse(record, "s"))["success"]?.GetValue<bool>() == true, "the command ran: " + string.Join("|", records.Select(r => r.ToJsonString())));
        Names(["""["start","minimal"]""", """["haiku45",true,"high"]""", """["haiku55",true,"low"]""", """["sonnet",true,"minimal"]"""], LogLines(sandbox),
            "per-model level, then defaultThinkingLevel clamped to haiku-5-5 (minimal -> low), then defaultThinkingLevel");
        var state = records.Single(record => IsResponse(record, "state"))["data"]!;
        Equal("claude-sonnet-4-5", state["model"]!["id"]!.GetValue<string>(), "final model");
        Equal("minimal", state["thinkingLevel"]!.GetValue<string>(), "final level");
    }

    // agent-session.ts setThinkingLevel: availableLevels.includes(level) ? level : _clampThinkingLevel(level) (models.ts
    // clampThinkingLevel: an unknown name selects the model's first supported level); nothing is refused.
    private static async Task NativeSetThinkingLevelClamps()
    {
        using var sandbox = NativeSandbox("native-thinking-clamp");
        sandbox.Write(Path.Combine(sandbox.AgentDir, "settings.json"), """{"defaultThinkingLevel":"high"}""");
        var folder = Path.Combine(sandbox.Cwd, ".pi", "extensions", "native-thinking");
        Directory.CreateDirectory(folder);
        File.Copy(typeof(Program).Assembly.Location, Path.Combine(folder, "ParityNative.dll"), overwrite: true);
        File.WriteAllText(Path.Combine(folder, "pisharp-extension.json"),
            JsonSerializer.Serialize(new { assembly = "ParityNative.dll", entryType = typeof(ParityNativeThinkingExtension).FullName }));
        var (code, records, stderr) = await RunRpc(sandbox, ["--provider", "anthropic", "--model", "claude-haiku-4-5"],
            ["""{"id":"bogus","type":"prompt","message":"/native-think bogus"}"""], (record, _) => IsResponse(record, "after-max"),
            react: (record, push) =>
            {
                if (IsResponse(record, "bogus")) push("""{"id":"after-bogus","type":"get_state"}""");
                else if (IsResponse(record, "after-bogus")) push("""{"id":"max","type":"prompt","message":"/native-think max"}""");
                else if (IsResponse(record, "max")) push("""{"id":"after-max","type":"get_state"}""");
            });
        Equal(0, code, "rpc exit; " + stderr);
        Check(records.Where(record => record["type"]?.GetValue<string>() == "response").All(record => record["success"]?.GetValue<bool>() == true),
            "every command succeeds: " + string.Join("|", records.Select(r => r.ToJsonString())));
        Equal("off", records.Single(record => IsResponse(record, "after-bogus"))["data"]!["thinkingLevel"]!.GetValue<string>(), "an unknown name: the first supported level");
        Equal("high", records.Single(record => IsResponse(record, "after-max"))["data"]!["thinkingLevel"]!.GetValue<string>(), "max: the nearest supported level below");
        Names(["think bogus off", "think max high"], NativeLog(sandbox), "the command saw the clamped levels");
    }
}

/// <summary>A native extension whose command sets the thinking level it is given (loaded from a copy of this assembly).</summary>
public sealed class ParityNativeThinkingExtension : IPiSharpExtension
{
    public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken cancellationToken)
    {
        registry.RegisterCommand(ExtensionCommandFacade.CreateCommand("native-think", "native-think", "Set the thinking level", async (arguments, facade, token) =>
        {
            var level = arguments.Value.ValueKind == JsonValueKind.String ? arguments.Value.GetString()! : "";
            await ((IExtensionSessionBehaviorFacade)facade).SetThinkingLevelAsync(level);
            if (Environment.GetEnvironmentVariable("PISHARP_EXTENSION_PARITY_NATIVE_LOG") is { } path)
                File.AppendAllText(path, "think " + level + " " + ((IExtensionSettingsThinkingReadFacade)facade).GetThinkingLevel() + "\n");
        }));
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
