using System.Text;
using System.Text.Json;
using PiSharp.CodingAgent.Resources;
using PiSharp.Contracts;
using PiSharp.Rpc.Protocol;

namespace PiSharp.Cli.Prompts;

/// <summary>Read-only prompt rows appended to a borrowed executable extension command revision.
/// Prompt rows never become extension registration or argument-completion authority.</summary>
internal sealed class PromptTemplateRpcCommandCatalog(PromptTemplateResourceSet templates,
    IRpcExtensionCommandCatalog? extensions = null) : IRpcExtensionCommandCatalog
{
    public JsonData CommandCatalog
    {
        get
        {
            var extensionRows = extensions?.CommandCatalog;
            using var bytes = new MemoryStream();
            using (var writer = new Utf8JsonWriter(bytes))
            {
                writer.WriteStartArray();
                if (extensionRows is not null)
                    foreach (var row in extensionRows.Value.EnumerateArray()) row.WriteTo(writer);
                foreach (var command in templates.Commands)
                {
                    writer.WriteStartObject(); writer.WriteString("name", command.Name);
                    writer.WriteString("description", command.Description); writer.WriteString("source", "prompt");
                    writer.WritePropertyName("sourceInfo"); writer.WriteStartObject();
                    writer.WriteString("path", command.SourceInfo.Path); writer.WriteString("source", command.SourceInfo.Source);
                    writer.WriteString("scope", command.SourceInfo.Scope.ToString().ToLowerInvariant());
                    writer.WriteString("origin", command.SourceInfo.Origin == PromptTemplateSourceOrigin.TopLevel ? "top-level" : "package");
                    if (command.SourceInfo.BaseDir is { } baseDir) writer.WriteString("baseDir", baseDir);
                    writer.WriteEndObject(); writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            return JsonData.Parse(Encoding.UTF8.GetString(bytes.ToArray()));
        }
    }

    public ValueTask<JsonData> CompleteCommandAsync(string name, string prefix, CancellationToken token) =>
        extensions?.CompleteCommandAsync(name, prefix, token) ??
        throw new InvalidOperationException("Extension command completion is unavailable.");
}
