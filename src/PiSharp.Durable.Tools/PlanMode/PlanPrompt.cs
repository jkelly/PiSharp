// PiSharp-only (decision 0005): no upstream counterpart. The `plan_mode` prompt section the plan-mode extension contributes while it is
// selected.
using PiSharp.Durable.Chord;

namespace PiSharp.Durable.Tools;

/// <summary>The <c>plan_mode</c> prompt section.</summary>
internal static class PlanPrompt
{
    /// <summary>The section key.</summary>
    public const string SectionKey = "plan_mode";

    /// <summary>The tagged section; it reads the plan directory from <see cref="PlanMode.StateDoc"/> at every render.</summary>
    public static PromptSection Create() => Define.Section(SectionKey, async (input, context) =>
    {
        var state = PlanMode.StateOf(await input.Read.SnapshotAsync(PlanMode.StateDoc, input.ConversationId, context).ConfigureAwait(false));
        return Render(state?.Directory);
    }, tag: true);

    /// <summary>The section text for <paramref name="directory"/>; without one, write and edit are disabled and the text says so.</summary>
    public static string Render(string? directory)
    {
        if (directory is not { Length: > 0 })
            return "Plan mode is active, but no plan directory is set, so write and edit fail. Do not change the project. Read and search freely, " +
                "and ask the user to leave plan mode and enter it again.";
        return "Plan mode is active: the user wants a plan before anything in the project changes.\n" +
            "- Do not change the project. Read and search freely; you may use the shell to inspect (for example git log, git diff, builds or tests " +
            "that write nothing), but never change files outside the plan directory, through write, edit or the shell.\n" +
            $"- You may create and edit files only inside the plan directory: {directory}\n" +
            $"  Write the plan to {Path.Join(directory, PlanMode.DefaultPlanFile)}. Notes, sketches and prototype code also belong there; write and " +
            "edit fail for any other path.\n" +
            "- When a decision is the user's to make (requirements, trade-offs, scope), ask with ask_user_question instead of guessing.\n" +
            "- When the plan is ready, call exit_plan_mode. The user approves it or replies with feedback. Only after approval do you get the full " +
            "tool set; then implement the approved plan.\n" +
            "- A good plan states the goal, the files to change and why, ordered steps, how to verify each, and the risks and open questions.";
    }
}
