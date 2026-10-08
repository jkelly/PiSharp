using PiSharp.Agent;
using PiSharp.CodingAgent.Resources;
using PiSharp.Contracts;
using static PromptTemplateDiscoveryTests;

internal static class PromptTemplateResourceSetTests
{
    public const string Prefix = "prompt template resource set ";
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        (Prefix + "loaded metadata becomes immutable command entries", Commands),
        (Prefix + "loaded template expands into materialized user text with images", Materialization),
        (Prefix + "captured resources survive file changes and extension expansion is opt-in", Capture)
    ];

    private static async Task Commands()
    {
        var fs = new Filesystem(); var path = P("review.md"); fs.File(path, "---\nmetadata\n---\nReview $1");
        var set = await PromptTemplateResourceSet.LoadAsync([Select(path)], _ => new("Review code", "[focus]"), fs);
        var command = set.Commands.Single(); Equal("review", command.Name); Equal("Review code", command.Description);
        Equal("[focus]", command.ArgumentHint); Equal("prompt", command.Source); Equal(path, command.SourceInfo.Path);
        Equal(0, set.Catalog.Diagnostics.Length);
    }

    private static async Task Materialization()
    {
        var fs = new Filesystem(); var path = P("review.md"); fs.File(path, "Review $1");
        var set = await PromptTemplateResourceSet.LoadAsync([Select(path)], _ => throw new Exception("Unexpected decoder"), fs);
        var images = JsonData.Parse("[{\"type\":\"image\",\"data\":\"AA==\",\"mimeType\":\"image/png\"}]");
        var input = new PromptInput("/review 'the change'", PromptInputSource.Rpc, images);
        var decision = await set.CreateInputAdmission(PromptTemplateInputOperation.Prompt).ReduceAsync(input, default);
        var owned = PromptInputValue.Apply(input, decision); Equal("Review the change", owned.Text); Equal(images.ToString(), owned.Images!.ToString());
        var message = PromptInputValue.Message(owned, 123); Equal("user", message.Role);
        Equal("Review the change", message.WireBody.Value.GetProperty("content")[0].GetProperty("text").GetString());
        Equal(2, message.WireBody.Value.GetProperty("content").GetArrayLength());
    }

    private static async Task Capture()
    {
        var fs = new Filesystem(); var path = P("review.md"); fs.File(path, "old $1");
        var set = await PromptTemplateResourceSet.LoadAsync([Select(path)], _ => throw new Exception("Unexpected decoder"), fs);
        fs.File(path, "new $1"); var input = new PromptInput("/review arg", PromptInputSource.Extension);
        Equal(PromptInputAction.Continue, (await set.CreateInputAdmission(PromptTemplateInputOperation.ExtensionMessage).ReduceAsync(input, default)).Action);
        Equal("old arg", (await set.CreateInputAdmission(PromptTemplateInputOperation.ExtensionMessage, expandTemplates: true).ReduceAsync(input, default)).Text);
        Equal(1, fs.Reads.Count);
    }
}
