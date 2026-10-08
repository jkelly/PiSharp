using System.Text;
using System.Text.Json;
using PiSharp.Agent;
using PiSharp.CodingAgent.Resources;
using PiSharp.CodingAgent.Resources.Skills;
using PiSharp.Contracts;
using PiSharp.Rpc.Protocol;

namespace PiSharp.Cli.Skills;

/// <summary>Explicit startup capture for the existing host. Borrows catalog/input callbacks and owns no
/// session, dispatcher, generation, task, file lease or credential capability.</summary>
internal sealed class SkillCliBinding
{
    internal SkillResourceSet Resources { get; }
    private SkillCliBinding(SkillResourceSet resources) => Resources = resources;
    internal static async Task<SkillCliBinding> LoadDiscoveredAsync(SkillDiscoveryRequest request,
        ISkillResourceFileSystem? files = null, Func<string, SkillMetadata>? decode = null,
        SkillResourceOptions? options = null, CancellationToken token = default) =>
        new(await SkillDiscovery.LoadAsync(request, decode ?? SkillFrontendDecoder.Decode,
            files, options, token).ConfigureAwait(false));
    internal static async Task<SkillCliBinding> LoadAsync(SkillCliConfiguration configuration,
        ISkillResourceFileSystem? files = null, Func<string, SkillMetadata>? decode = null,
        SkillResourceOptions? options = null, CancellationToken token = default) =>
        new(await SkillResourceSet.LoadAsync(configuration.Selections, decode ?? SkillFrontendDecoder.Decode,
            files, options, token).ConfigureAwait(false));

    internal IRpcExtensionCommandCatalog Commands(IRpcExtensionCommandCatalog? borrowed = null) => new SkillRpcCommandCatalog(Resources, borrowed);
    internal IPromptInputAdmission AdmissionFor(PromptTemplateInputOperation operation,
        PromptTemplateCatalogSnapshot? templates = null, IPromptInputAdmission? rawHandlers = null,
        IPromptTemplateCommandAdmission? rawCommands = null,
        Func<SkillDiagnostic, CancellationToken, ValueTask>? report = null, bool? expandTemplates = null) =>
        new PromptTemplateInputAdmission(templates ?? new([], []), operation,
            (expandTemplates ?? operation != PromptTemplateInputOperation.ExtensionMessage)
                ? new SkillInputAdmission(Resources, rawHandlers, report) : rawHandlers, rawCommands, expandTemplates);

    /// <summary>Caller applies through the existing acknowledged system-message boundary.
    /// All non-content fields (including declarations) are copied unchanged; this grants no tools.</summary>
    internal JsonData AddToSystemMessage(JsonData original, string fileReadTool = "read")
    {
        var suffix = Resources.FormatForPrompt(fileReadTool); if (suffix.Length == 0) return original;
        if (original.Value.ValueKind != JsonValueKind.Object || !original.Value.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.String) throw new ArgumentException("Skill system message requires string content.");
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes))
        {
            writer.WriteStartObject(); foreach (var property in original.Value.EnumerateObject())
                if (property.Name == "content") writer.WriteString("content", property.Value.GetString() + suffix); else property.WriteTo(writer);
            writer.WriteEndObject();
        }
        return JsonData.Parse(Encoding.UTF8.GetString(bytes.ToArray()));
    }
}
