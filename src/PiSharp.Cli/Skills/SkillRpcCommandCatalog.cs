using System.Text;
using System.Text.Json;
using PiSharp.CodingAgent.Resources.Skills;
using PiSharp.Contracts;
using PiSharp.Rpc.Protocol;

namespace PiSharp.Cli.Skills;

internal sealed class SkillRpcCommandCatalog(SkillResourceSet skills, IRpcExtensionCommandCatalog? borrowed = null) : IRpcExtensionCommandCatalog
{
    public JsonData CommandCatalog
    {
        get
        {
            using var bytes = new MemoryStream();
            using (var writer = new Utf8JsonWriter(bytes))
            {
                writer.WriteStartArray();
                if (borrowed is not null) foreach (var row in borrowed.CommandCatalog.Value.EnumerateArray()) row.WriteTo(writer);
                foreach (var skill in skills.Skills)
                {
                    writer.WriteStartObject(); writer.WriteString("name", "skill:" + skill.Name);
                    writer.WriteString("description", skill.Description); writer.WriteString("source", "skill");
                    writer.WritePropertyName("sourceInfo"); writer.WriteStartObject();
                    writer.WriteString("path", skill.FilePath); writer.WriteString("source", skill.SourceInfo.Source);
                    writer.WriteString("scope", skill.SourceInfo.Scope.ToString().ToLowerInvariant()); writer.WriteString("origin", "top-level"); writer.WriteString("baseDir", skill.BaseDir);
                    writer.WriteEndObject(); writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            return JsonData.Parse(Encoding.UTF8.GetString(bytes.ToArray()));
        }
    }
    public ValueTask<JsonData> CompleteCommandAsync(string name, string prefix, CancellationToken token) =>
        borrowed?.CompleteCommandAsync(name, prefix, token) ?? throw new InvalidOperationException("Skill rows grant no extension command completion authority.");
}
