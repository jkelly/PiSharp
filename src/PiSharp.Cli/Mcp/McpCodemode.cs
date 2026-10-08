// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/extensions/codemode/index.ts
// (createCodemodeExtension: appendEntry, models, getToolNamespace, getToolGuidelines, getMode, getInlineBudget),
// packages/coding-agent/src/extensions/codemode/execute.ts (ctx.tools, ctx.executeTool, ctx.sessionManager.getBranch,
// ctx.modelRegistry) and packages/coding-agent/src/extensions/mcp/tools.ts (createMcpResultSchema, convertMcpResult: scripts
// receive the CallToolResult without _meta).
using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Cli.Extensions;
using PiSharp.Codemode;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Contracts.ModelOperations;
using PiSharp.Extensions;

namespace PiSharp.Cli.Mcp;

/// <summary>The built-in <c>codemode</c> tool of production sessions: scripts run in a Jint sandbox (PiSharp.Codemode) and
/// call the session's nested tools through the shared invoker, with the codemode call as their parent. <c>store()</c> writes
/// are appended to the session as <c>codemode-store</c> custom entries, so each branch sees its own values.</summary>
internal static class McpCodemode
{
    internal const string Name = CodemodeToolDefinition.Name, RegistrationId = "codemode";
    private static readonly JsonData McpResultSchema = JsonData.Parse(
        """{"type":"object","properties":{"content":{"type":"array","items":{"type":"object"}},"isError":{"type":"boolean"},"_meta":{"type":"object"}},"required":["content"]}""");

    /// <summary>A fresh definition for one generation; each binds to exactly one attachment.</summary>
    internal static McpDiscoveryExecutableDefinition Create(CodemodeMode mode, int? inlineBudget, Func<ICodemodeModelRuntime?> models)
    {
        var withModels = true;
        return McpDiscoveryExecutableDefinition.CreateCodemode(RegistrationId, CodemodeToolDefinition.CreateDescription([], withModels),
            (code, attachment, invocation, token) => new(ExecuteAsync(code, attachment, invocation, models, token)),
            loadout => CodemodeToolDefinition.PrepareLoadout(loadout, mode, withModels, inlineBudget, tool => OutputSchema(tool.Name, tool.Namespace)),
            descriptor => descriptor with
            {
                PromptGuidelines = CodemodeToolDefinition.PromptGuidelines,
                ConstrainedSampling = CodemodeToolDefinition.ConstrainedSampling,
                Renderers = CodemodeRenderer.Renderers
            });
    }

    /// <summary>tool.ts toCodemodeDeclaration's output schemas as PiSharp knows them: read and bash declare theirs, MCP tools
    /// resolve to their CallToolResult; other tools resolve to their text.</summary>
    internal static JsonData? OutputSchema(string name, ToolNamespace? toolNamespace) => name switch
    {
        "read" => PiSharp.Tools.Files.ReadWriteTools.ReadOutputSchema,
        "bash" => PiSharp.Tools.Processes.BashTool.OutputSchema,
        _ => IsMcpTool(name, toolNamespace) ? McpResultSchema : null
    };

    private static bool IsMcpTool(string name, ToolNamespace? toolNamespace) =>
        name.StartsWith("mcp__", StringComparison.Ordinal) && toolNamespace?.Name.StartsWith("mcp__", StringComparison.Ordinal) == true;

    private static async Task<JsonData> ExecuteAsync(string code, AgentSessionAttachment attachment, IExtensionToolInvocationContext invocation,
        Func<ICodemodeModelRuntime?> models, CancellationToken token)
    {
        var host = new Host(attachment.Session, invocation, models);
        try
        {
            return await CodemodeExecutor.ExecuteAsync(invocation.ToolCallId, code, host,
                partial => invocation.ReportUpdateAsync(partial, token), models: true, token).ConfigureAwait(false);
        }
        catch (CodemodeSourceException error)
        {
            // The agent loop reports a thrown error as an error result with the message.
            return JsonData.Parse(JsonSerializer.Serialize(new { content = new[] { new { type = "text", text = error.Message } }, details = new { }, isError = true }));
        }
    }

    private sealed class Host(PersistentAgentSession session, IExtensionToolInvocationContext invocation, Func<ICodemodeModelRuntime?> models) : ICodemodeHost
    {
        private readonly Lazy<ImmutableArray<CodemodeNestedTool>> tools = new(() =>
        {
            var callable = invocation.Tools.ToHashSet(StringComparer.Ordinal);
            return [.. session.CaptureToolCatalogRegistry().RegisteredTools
                .Select(tool => (Tool: tool, Name: tool.Declaration.Value.GetProperty("name").GetString()!))
                .Where(row => callable.Contains(row.Name) && row.Name != Name)
                .Select(row => new CodemodeNestedTool(row.Name,
                    row.Tool.Declaration.Value.TryGetProperty("description", out var description) && description.ValueKind == JsonValueKind.String ? description.GetString()! : "",
                    row.Tool.Declaration.Value.TryGetProperty("parameters", out var parameters) ? JsonData.FromElement(parameters) : JsonData.EmptyObject)
                {
                    Namespace = row.Tool.Namespace, PromptGuidelines = row.Tool.PromptGuidelines.IsDefault ? [] : row.Tool.PromptGuidelines,
                    OutputSchema = OutputSchema(row.Name, row.Tool.Namespace)
                })];
        });
        private readonly Lazy<ICodemodeModelRuntime?> runtime = new(models);

        public ImmutableArray<CodemodeNestedTool> Tools => tools.Value;
        public ICodemodeModelRuntime? Models => runtime.Value;

        public async ValueTask<CodemodeNestedOutcome> ExecuteToolAsync(string name, JsonData? arguments, CancellationToken cancellationToken)
        {
            var outcome = await invocation.ExecuteToolAsync(name, arguments ?? JsonData.Null, new(cancellationToken)).ConfigureAwait(false);
            JsonData? scriptValue = null;
            if (IsMcpTool(name, Tools.FirstOrDefault(tool => tool.Name == name)?.Namespace) && outcome.Result.Value.ValueKind == JsonValueKind.Object)
            {
                // convertMcpResult: the script receives the CallToolResult without _meta.
                var fields = outcome.Result.Value.EnumerateObject().Where(property => property.Name is not ("_meta" or "details" or "usage"))
                    .ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
                scriptValue = JsonData.Parse(JsonSerializer.Serialize(fields));
            }
            return new(outcome.ToolCallId, outcome.Result, outcome.IsError) { ScriptValue = scriptValue };
        }

        public IReadOnlyList<KeyValuePair<string, JsonData>> ReadStore() =>
            CodemodeStore.Read(session.Snapshot.Context.Ancestry.Where(entry => entry.Type == "custom").Select(entry =>
                (entry.WireBody.Value.TryGetProperty("customType", out var type) && type.ValueKind == JsonValueKind.String ? type.GetString() : null,
                 entry.WireBody.Value.TryGetProperty("data", out var data) ? data : default)));

        public async ValueTask AppendStoreEntryAsync(JsonData data, CancellationToken cancellationToken) =>
            await session.AppendRunCustomEntryAsync(CodemodeToolDefinition.StoreEntryType, data, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The CLI's model registry as scripts reach it (IMPL-B's facade): resolution by provider and id, headers dropped.</summary>
    internal sealed class ModelRuntime(NativeExtensionModelOperations operations) : ICodemodeModelRuntime
    {
        public static ModelRuntime CreateDefault() => new(new(NativeExtensionModelOperations.CreateDefaultRegistry(
            PiSharp.Cli.Commands.LiveSessionRuntime.Default.ReadEnvironment, PiSharp.Cli.Commands.LiveSessionRuntime.Default.AuthPath)));
        public ImmutableArray<JsonData> GetModelsOfType(ModelType type, string? provider) => operations.GetModelsOfType(type, provider);
        public Task<ImmutableArray<JsonData>> GetAvailableOfTypeAsync(ModelType type, string? provider, CancellationToken cancellationToken) =>
            operations.GetAvailableOfTypeAsync(type, provider, cancellationToken);
        public JsonData? GetModelOfType(ModelType type, string provider, string id) => operations.GetModelOfType(type, provider, id);
        public Task<ClassifierResult> ClassifyAsync(JsonData model, ClassifierContext context, CancellationToken cancellationToken) =>
            operations.ClassifyAsync(model, context, null, cancellationToken);
        public Task<AssistantImages> GenerateImagesAsync(JsonData model, ImagesContext context, CancellationToken cancellationToken) =>
            operations.GenerateImagesAsync(model, context, null, cancellationToken);
    }
}
