// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/src/harness/define.ts. Upstream's identity functions exist for
// TypeScript inference; here they are plain constructors.
namespace PiSharp.Durable;

/// <summary>Definitions of extensions, tools, prompt sections, hooks and wraps (upstream <c>define.ts</c>).</summary>
public static class Define
{
    /// <summary>An extension (upstream <c>defineExtension</c>); returns it unchanged.</summary>
    public static Extension Extension(Extension extension) => extension ?? throw new ArgumentNullException(nameof(extension));

    /// <summary>A tool (upstream <c>defineTool</c>); returns it unchanged.</summary>
    public static T Tool<T>(T tool) where T : ToolRegistration => tool ?? throw new ArgumentNullException(nameof(tool));

    /// <summary>A prompt section, tagged unless <paramref name="tag"/> is false (upstream <c>section</c>). <c>Tag</c> stays null unless
    /// given.</summary>
    public static PromptSection Section(string key, PromptRender render, bool? tag = null) => new() { Key = key, Render = render, Tag = tag };

    /// <summary>A prompt section rendering fixed text.</summary>
    public static PromptSection Section(string key, string? text, bool? tag = null) =>
        Section(key, (_, _) => ValueTask.FromResult(text), tag);

    /// <summary>Hook handlers for tasks with <paramref name="task"/>'s name (upstream <c>hook</c>). <paramref name="handlers"/> is the task's
    /// hook map type, such as <see cref="ToolHooks"/>.</summary>
    public static HookRegistration Hook(DurableTask task, object handlers)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(handlers);
        return new(task.Definition.Name, handlers);
    }

    /// <summary>Wraps the tool named like <paramref name="tool"/> wherever the wrapping extension is selected (upstream
    /// <c>wrapTool</c>).</summary>
    public static Wrap WrapTool(ToolRegistration tool, Func<ToolRegistration, ToolRegistration> wrapper)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(wrapper);
        return new ToolWrap(tool.Name, wrapper);
    }

    /// <summary>Wraps the section <paramref name="key"/> wherever the wrapping extension is selected (upstream <c>wrapSection</c>).</summary>
    public static Wrap WrapSection(string key, Func<PromptSection, PromptSection> wrapper)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(wrapper);
        return new SectionWrap(key, wrapper);
    }
}
