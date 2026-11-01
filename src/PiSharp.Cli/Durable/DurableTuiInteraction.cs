// PiSharp-only (decision 0005, "PiSharp-only additions"): no upstream counterpart. The `pisharp durable` UI of ask_user_question and plan
// mode: the question form and the plan review mounted in place of the editor while a tool call waits for the user, and the tool cards of
// ask_user_question and exit_plan_mode. Built from PiSharp.Tui's Input, Text, Markdown and Spacer and pi's DynamicBorder,
// ExtensionSelectorComponent and ExtensionEditorComponent, with pi's tui.select.* keybindings.
using System.Collections.Immutable;
using System.Text.Json.Nodes;
using PiSharp.Cli.Interactive.Mode;
using PiSharp.Cli.Interactive.Mode.Components;
using PiSharp.Durable.Tools;
using PiSharp.Tui.Pi;
using static PiSharp.Cli.Durable.DurableTuiState;
using static PiSharp.Cli.Interactive.Mode.ThemeGlobals;
using TuiKeybindingsManager = PiSharp.Tui.Pi.KeybindingsManager;

namespace PiSharp.Cli.Durable;

/// <summary>
/// The form of a pending <c>ask_user_question</c> call, in place of the editor. A row of tabs, one chip per question header and a Submit tab;
/// ←/→ and Tab/Shift+Tab move between them. A question tab lists its options as "label — description" and an "Other…" row: ↑/↓ moves,
/// a digit jumps to a row and acts on it. A single-select question picks with Enter (or its digit) and moves to the next tab; a
/// multi-select one toggles with Space (or its digit) and moves on with Enter. "Other…" opens an inline input: Enter keeps the text, Esc
/// goes back to the list. The Submit tab summarizes the answers and offers "Submit answers" and "Decline to answer". Elsewhere the
/// selector's cancel keys (Esc, Ctrl+C) abort the run, and the app's exit key (Ctrl+D) exits <c>pisharp durable</c> with the question
/// still waiting, so <c>--continue</c> shows it again.
/// </summary>
internal sealed class DurableQuestionForm : Container, IFocusable, IInputHandler
{
    private const string OtherLabel = "Other…";

    private readonly ImmutableArray<UserQuestion> questions;
    private readonly Action<QuestionResponse> onSubmit;
    private readonly Action onAbort;
    private readonly Action onExit;
    private readonly List<SortedSet<int>> selected;
    private readonly string?[] other;
    private readonly int[] cursor;
    private int tab;
    private int submitCursor;
    private Input? editing;
    private bool busy;
    private string? warning;
    private bool focused;

    public DurableQuestionForm(ImmutableArray<UserQuestion> questions, Action<QuestionResponse> onSubmit, Action onAbort, Action onExit)
    {
        this.questions = questions;
        this.onSubmit = onSubmit;
        this.onAbort = onAbort;
        this.onExit = onExit;
        selected = [.. questions.Select(_ => new SortedSet<int>())];
        other = new string?[questions.Length];
        cursor = new int[questions.Length];
        Refresh();
    }

    public bool Focused
    {
        get => focused;
        set
        {
            focused = value;
            if (editing is not null) editing.Focused = value;
        }
    }

    /// <summary>The answers were not taken (the runtime reported why): the form takes input again.</summary>
    public void Retry()
    {
        busy = false;
        Refresh();
    }

    private bool OnSubmitTab => tab == questions.Length;

    private bool Answered(int index) => questions[index].MultiSelect
        ? selected[index].Count > 0 || other[index] is not null
        : selected[index].Count == 1 || other[index] is not null;

    public void HandleInput(string data)
    {
        var keys = TuiKeybindingsManager.Global;
        if (editing is not null)
        {
            editing.HandleInput(data);
            Refresh();
            return;
        }
        if (keys.Matches(data, "app.exit"))
        {
            onExit();
            return;
        }
        if (keys.Matches(data, "tui.select.cancel"))
        {
            onAbort();
            return;
        }
        if (busy) return;
        warning = null;
        if (Keys.Matches(data, "tab") || Keys.Matches(data, "right")) tab = Math.Min(questions.Length, tab + 1);
        else if (Keys.Matches(data, "shift+tab") || Keys.Matches(data, "left")) tab = Math.Max(0, tab - 1);
        else if (OnSubmitTab) HandleSubmitTab(data, keys);
        else HandleQuestion(data, keys);
        Refresh();
    }

    private void HandleSubmitTab(string data, TuiKeybindingsManager keys)
    {
        if (keys.Matches(data, "tui.select.up")) submitCursor = 0;
        else if (keys.Matches(data, "tui.select.down")) submitCursor = 1;
        else if (data is "1" or "2")
        {
            submitCursor = data == "1" ? 0 : 1;
            Confirm();
        }
        else if (keys.Matches(data, "tui.select.confirm") || data == "\n") Confirm();
    }

    private void Confirm()
    {
        if (submitCursor == 1)
        {
            busy = true;
            onSubmit(QuestionResponse.Declined);
            return;
        }
        var missing = Enumerable.Range(0, questions.Length).FirstOrDefault(index => !Answered(index), -1);
        if (missing >= 0)
        {
            tab = missing;
            warning = "Answer every question first, or decline to answer.";
            return;
        }
        busy = true;
        onSubmit(QuestionResponse.Answered(questions.Select((question, index) =>
            new QuestionAnswer([.. selected[index].Select(option => question.Options[option].Label)], other[index]))));
    }

    private void HandleQuestion(string data, TuiKeybindingsManager keys)
    {
        var question = questions[tab];
        var rows = question.Options.Length + 1;
        if (keys.Matches(data, "tui.select.up")) cursor[tab] = Math.Max(0, cursor[tab] - 1);
        else if (keys.Matches(data, "tui.select.down")) cursor[tab] = Math.Min(rows - 1, cursor[tab] + 1);
        else if (data.Length == 1 && data[0] is >= '1' and <= '9' && data[0] - '0' <= rows)
        {
            cursor[tab] = data[0] - '1';
            if (question.MultiSelect) Toggle();
            else Pick();
        }
        else if (Keys.Matches(data, "space") || data == " ")
        {
            if (question.MultiSelect) Toggle();
            else if (cursor[tab] == question.Options.Length) OpenOther();
            else Choose();
        }
        else if (keys.Matches(data, "tui.select.confirm") || data == "\n")
        {
            if (cursor[tab] == question.Options.Length) OpenOther();
            else if (question.MultiSelect) tab++;
            else Pick();
        }
    }

    /// <summary>Single-select: chooses the option under the cursor and moves to the next tab; "Other…" opens its input.</summary>
    private void Pick()
    {
        if (cursor[tab] == questions[tab].Options.Length)
        {
            OpenOther();
            return;
        }
        Choose();
        tab++;
    }

    private void Choose()
    {
        selected[tab].Clear();
        selected[tab].Add(cursor[tab]);
        other[tab] = null;
    }

    /// <summary>Multi-select: toggles the option under the cursor; "Other…" opens its input.</summary>
    private void Toggle()
    {
        if (cursor[tab] == questions[tab].Options.Length)
        {
            OpenOther();
            return;
        }
        if (!selected[tab].Remove(cursor[tab])) selected[tab].Add(cursor[tab]);
    }

    private void OpenOther()
    {
        var index = tab;
        var input = new Input($"→ {questions[index].Options.Length + 1}. Other: ") { Focused = focused };
        // The earlier text, with the cursor after it (as pasted).
        if (other[index] is { } earlier) input.HandleInput("\u001b[200~" + earlier + "\u001b[201~");
        input.OnSubmit = text =>
        {
            var trimmed = TextUtils.JsTrim(text);
            other[index] = trimmed.Length > 0 ? trimmed : null;
            editing = null;
            if (!questions[index].MultiSelect && other[index] is not null)
            {
                selected[index].Clear();
                tab = index + 1;
            }
        };
        input.OnEscape = () => editing = null;
        editing = input;
    }

    private void Refresh()
    {
        Clear();
        AddChild(new DynamicBorder());
        AddChild(new Text(Chips(), 1, 0));
        AddChild(new Spacer(1));
        if (OnSubmitTab) RenderSubmitTab();
        else RenderQuestion();
        AddChild(new Spacer(1));
        if (warning is not null) AddChild(new Text(theme.Fg("warning", warning), 1, 0));
        AddChild(new Text(Hint(), 1, 0));
        AddChild(new DynamicBorder());
    }

    private string Chips()
    {
        var chips = questions.Select((question, index) =>
        {
            var text = $" {(Answered(index) ? "✓ " : "")}{question.Header} ";
            return index == tab ? theme.Fg("accent", theme.Inverse(theme.Bold(text))) : theme.Fg(Answered(index) ? "success" : "muted", text);
        }).ToList();
        chips.Add(OnSubmitTab ? theme.Fg("accent", theme.Inverse(theme.Bold(" Submit "))) : theme.Fg("muted", " Submit "));
        return string.Join(" ", chips);
    }

    private void RenderQuestion()
    {
        var question = questions[tab];
        AddChild(new Text(theme.Bold(question.Question), 1, 0));
        AddChild(new Text(theme.Fg("muted", question.MultiSelect ? "Select all that apply." : "Select one."), 1, 0));
        AddChild(new Spacer(1));
        for (var row = 0; row < question.Options.Length; row++)
        {
            var option = question.Options[row];
            var mark = question.MultiSelect ? selected[tab].Contains(row) ? "[x] " : "[ ] " : selected[tab].Contains(row) ? "(•) " : "( ) ";
            var description = option.Description.Length > 0 ? theme.Fg("muted", " — " + option.Description) : "";
            AddChild(new Text(Row(row, mark + option.Label) + description, 1, 0));
        }
        var otherRow = question.Options.Length;
        if (editing is not null)
        {
            AddChild(new InputRow(editing));
            return;
        }
        var chosen = other[tab] is not null;
        var otherMark = question.MultiSelect ? chosen ? "[x] " : "[ ] " : chosen ? "(•) " : "( ) ";
        AddChild(new Text(Row(otherRow, otherMark + (chosen ? $"Other: \"{other[tab]}\"" : OtherLabel)), 1, 0));
    }

    private string Row(int row, string text)
    {
        var line = $"{row + 1}. {text}";
        return row == cursor[tab] ? theme.Fg("accent", "→ " + line) : "  " + theme.Fg("text", line);
    }

    private void RenderSubmitTab()
    {
        AddChild(new Text(theme.Bold("Review your answers"), 1, 0));
        AddChild(new Spacer(1));
        var width = questions.Select(question => question.Header.Length).DefaultIfEmpty(0).Max();
        for (var index = 0; index < questions.Length; index++)
        {
            var answer = Summary(index);
            AddChild(new Text($"{theme.Fg("accent", questions[index].Header.PadRight(width))}  " +
                (answer is null ? theme.Fg("warning", "(not answered)") : theme.Fg("text", answer)), 1, 0));
        }
        AddChild(new Spacer(1));
        string[] choices = ["Submit answers", "Decline to answer"];
        for (var row = 0; row < choices.Length; row++)
            AddChild(new Text(row == submitCursor ? theme.Fg("accent", "→ " + choices[row]) : "  " + theme.Fg("text", choices[row]), 1, 0));
    }

    /// <summary>The answer to question <paramref name="index"/> as one line, or null while it has none.</summary>
    private string? Summary(int index)
    {
        if (!Answered(index)) return null;
        var parts = selected[index].Select(option => questions[index].Options[option].Label).ToList();
        if (other[index] is { } text) parts.Add($"Other: \"{text}\"");
        return string.Join(", ", parts);
    }

    private string Hint()
    {
        if (busy) return theme.Fg("muted", "Sending...");
        if (editing is not null) return KeybindingHints.KeyHint("tui.select.confirm", "save") + "  " + KeybindingHints.KeyHint("tui.select.cancel", "back");
        var tabs = KeybindingHints.RawKeyHint("←→", "questions");
        var abort = KeybindingHints.KeyHint("tui.select.cancel", "abort") + "  " + KeybindingHints.KeyHint("app.exit", "exit");
        if (OnSubmitTab) return KeybindingHints.RawKeyHint("↑↓", "navigate") + "  " + KeybindingHints.KeyHint("tui.select.confirm", "confirm") + "  " + tabs + "  " + abort;
        var rows = questions[tab].Options.Length + 1;
        return questions[tab].MultiSelect
            ? KeybindingHints.RawKeyHint("↑↓", "navigate") + "  " + KeybindingHints.RawKeyHint("space", "toggle") + "  " +
                KeybindingHints.RawKeyHint($"1-{rows}", "toggle") + "  " + KeybindingHints.KeyHint("tui.select.confirm", "next") + "  " + tabs + "  " + abort
            : KeybindingHints.RawKeyHint("↑↓", "navigate") + "  " + KeybindingHints.RawKeyHint($"1-{rows}", "pick") + "  " +
                KeybindingHints.KeyHint("tui.select.confirm", "select") + "  " + tabs + "  " + abort;
    }

    /// <summary>The inline "Other" input, indented like the option rows.</summary>
    private sealed class InputRow(Input input) : IComponent
    {
        public void Invalidate() => input.Invalidate();
        public List<string> Render(int width) => [.. input.Render(Math.Max(1, width - 1)).Select(line => " " + line)];
    }
}

/// <summary>
/// The review of a plan a pending <c>exit_plan_mode</c> call submitted, in place of the editor: the plan as Markdown (cut to fit; the tool
/// card above shows all of it) and its file, then pi's selector with "Yes, implement it", "Yes, but don't start yet", "No, keep planning
/// (give feedback)", which opens pi's multi-line editor for the feedback (Esc goes back), and "No, keep planning". The selector's cancel
/// keys (Esc, Ctrl+C) abort the run; the app's exit key (Ctrl+D) exits <c>pisharp durable</c> with the plan still waiting for review,
/// except while the feedback editor is open.
/// </summary>
internal sealed class DurablePlanReview : Container, IFocusable, IInputHandler
{
    internal const string Approve = "Yes, implement it";
    internal const string ApproveAndStop = "Yes, but don't start yet";
    internal const string RejectWithFeedback = "No, keep planning (give feedback)";
    internal const string Reject = "No, keep planning";

    private readonly ITui ui;
    private readonly TuiKeybindingsManager appKeybindings;
    private readonly Action<DurablePlanDecision> onDecide;
    private readonly Action onAbort;
    private readonly Action onExit;
    private readonly Container lower = new();
    private readonly ExtensionSelectorComponent choices;
    private IInputHandler active;
    private ExtensionEditorComponent? feedback;
    private bool busy;
    private bool focused;

    public DurablePlanReview(ITui ui, TuiKeybindingsManager appKeybindings, string plan, string? planFile, Action<DurablePlanDecision> onDecide,
        Action onAbort, Action onExit)
    {
        this.ui = ui;
        this.appKeybindings = appKeybindings;
        this.onDecide = onDecide;
        this.onAbort = onAbort;
        this.onExit = onExit;
        AddChild(new DynamicBorder());
        AddChild(new Spacer(1));
        AddChild(new Text(theme.Fg("accent", theme.Bold("Plan ready for review")), 1, 0));
        if (planFile is not null) AddChild(new Text(theme.Fg("muted", "Plan file: " + planFile), 1, 0));
        AddChild(new Spacer(1));
        AddChild(new ClippedLines(new Markdown(plan, 1, 0, Themes.GetMarkdownTheme()), () => Math.Max(3, ui.Terminal.Rows / 2 - 8)));
        AddChild(new Spacer(1));
        AddChild(lower);
        choices = new ExtensionSelectorComponent("Would you like to proceed?", [Approve, ApproveAndStop, RejectWithFeedback, Reject], Choose, onAbort);
        active = Show(choices);
    }

    public bool Focused
    {
        get => focused;
        set
        {
            focused = value;
            if (feedback is not null) feedback.Focused = value;
        }
    }

    /// <summary>The decision was not taken (the runtime reported why): the review takes input again where it was, with the feedback
    /// typed so far.</summary>
    public void Retry()
    {
        busy = false;
        active = feedback is null ? Show(choices) : Show(feedback);
        ui.RequestRender();
    }

    private IInputHandler Show<T>(T component) where T : IComponent, IInputHandler
    {
        lower.Clear();
        lower.AddChild(component);
        return component;
    }

    private void Choose(string choice)
    {
        switch (choice)
        {
            case Approve: Decide(DurablePlanDecision.Approve); break;
            case ApproveAndStop: Decide(DurablePlanDecision.ApproveAndStop); break;
            case Reject: Decide(DurablePlanDecision.Reject()); break;
            default:
                feedback = new ExtensionEditorComponent(ui, appKeybindings, "What should change in the plan?", null,
                    text => Decide(DurablePlanDecision.Reject(TextUtils.JsTrim(text) is { Length: > 0 } trimmed ? trimmed : null)),
                    () =>
                    {
                        feedback = null;
                        active = Show(choices);
                        ui.RequestRender();
                    },
                    new ExtensionEditorOptions(Description: "The agent stays in plan mode and revises the plan."))
                { Focused = focused };
                lower.Clear();
                lower.AddChild(feedback);
                active = feedback;
                break;
        }
    }

    private void Decide(DurablePlanDecision decision)
    {
        busy = true;
        lower.Clear();
        lower.AddChild(new Text(theme.Fg("muted", "Sending..."), 1, 0));
        lower.AddChild(new DynamicBorder());
        onDecide(decision);
    }

    public void HandleInput(string data)
    {
        if (feedback is null && appKeybindings.Matches(data, "app.exit"))
        {
            onExit();
            return;
        }
        if (busy)
        {
            if (TuiKeybindingsManager.Global.Matches(data, "tui.select.cancel")) onAbort();
            return;
        }
        active.HandleInput(data);
        ui.RequestRender();
    }

    /// <summary>A component cut to at most <c>maxLines()</c> lines, the last saying how many more there are.</summary>
    private sealed class ClippedLines(IComponent inner, Func<int> maxLines) : IComponent
    {
        public void Invalidate() => inner.Invalidate();

        public List<string> Render(int width)
        {
            var lines = inner.Render(width);
            var max = Math.Max(1, maxLines());
            if (lines.Count <= max) return lines;
            var kept = lines.Take(max - 1).ToList();
            kept.Add(" " + theme.Fg("muted", $"... {lines.Count - kept.Count} more lines; the tool card above shows the whole plan"));
            return kept;
        }
    }
}

/// <summary>The tool cards of <c>ask_user_question</c> and <c>exit_plan_mode</c> (pi's renderCall and renderResult).</summary>
internal static class DurableInteractionRenderers
{
    /// <summary>The renderers by tool name.</summary>
    public static IReadOnlyDictionary<string, ToolRenderers> Create() => new Dictionary<string, ToolRenderers>(StringComparer.Ordinal)
    {
        [UserQuestions.ToolName] = new(RenderCall: QuestionsCall, RenderResult: QuestionsResult),
        [PlanMode.ExitToolName] = new(RenderCall: PlanCall, RenderResult: PlanResult),
    };

    /// <summary>The questions of the arguments; a JSON string or a single question object as the tool's argument repair reads them.</summary>
    private static List<JsonObject> Questions(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            try { node = JsonNode.Parse(text); }
            catch (System.Text.Json.JsonException) { return []; }
        }
        return node switch
        {
            JsonArray array => [.. array.OfType<JsonObject>()],
            JsonObject single => [single],
            _ => [],
        };
    }

    private static Text Reuse(ToolRenderContext context) => context.LastComponent as Text ?? new Text("", 0, 0);

    private static IComponent QuestionsCall(JsonNode? args, Theme theme, ToolRenderContext context)
    {
        var questions = Questions(args is JsonObject value ? value["questions"] : null);
        var lines = new List<string>
        {
            theme.Fg("toolTitle", theme.Bold(UserQuestions.ToolName)) +
                string.Concat(questions.Select(question => " " + theme.Fg("accent", $"[{Str(question, "header") ?? ""}]"))),
        };
        lines.AddRange(questions.Select((question, index) => theme.Fg("toolOutput", $"  {index + 1}. {Str(question, "question") ?? ""}")));
        var text = Reuse(context);
        text.SetText(string.Join("\n", lines));
        return text;
    }

    private static IComponent QuestionsResult(JsonObject result, ToolRenderResultOptions options, Theme theme, ToolRenderContext context)
    {
        var details = result["details"] as JsonObject;
        if (Str(details, "kind") != UserQuestions.DetailsKind) return ContentText(result, theme);
        var questions = Arr(details, "questions").OfType<JsonObject>().ToList();
        var text = new Text("", 0, 0);
        switch (Str(details, "state"))
        {
            case "pending":
                text.SetText(theme.Fg("muted", "Waiting for your answer..."));
                break;
            case "declined":
                text.SetText(theme.Fg("warning", "Declined to answer."));
                break;
            default:
                var answers = Arr(details, "answers").ToList();
                text.SetText(string.Join("\n", questions.Select((question, index) =>
                {
                    var answer = index < answers.Count ? answers[index] : null;
                    var parts = Arr(answer, "selected").Select(label => label is JsonValue value && value.TryGetValue<string>(out var name) ? name : "").ToList();
                    if (Str(answer, "other") is { } other) parts.Add($"Other: \"{other}\"");
                    return theme.Fg("success", "✓ ") + theme.Fg("accent", $"[{Str(question, "header") ?? ""}] ") + theme.Fg("toolOutput", string.Join(", ", parts));
                })));
                break;
        }
        return text;
    }

    private static IComponent PlanCall(JsonNode? args, Theme theme, ToolRenderContext context)
    {
        var file = Str(args, "planFile");
        var text = Reuse(context);
        text.SetText(theme.Fg("toolTitle", theme.Bold(PlanMode.ExitToolName)) + (file is { Length: > 0 } ? " " + theme.Fg("accent", file) : ""));
        return text;
    }

    private static IComponent PlanResult(JsonObject result, ToolRenderResultOptions options, Theme theme, ToolRenderContext context)
    {
        var details = result["details"] as JsonObject;
        if (Str(details, "kind") != PlanMode.DetailsKind) return ContentText(result, theme);
        var plan = Str(details, "plan") ?? "";
        var file = Str(details, "planFile");
        var state = Str(details, "state");
        var container = new Container();
        if (file is not null) container.AddChild(new Text(theme.Fg("muted", "Plan file: " + file), 0, 0));
        var outcome = state switch
        {
            "pending" => null,
            "approved" => details!["stop"] is JsonValue stop && stop.TryGetValue<bool>(out var stopped) && stopped
                ? theme.Fg("success", "✓ Plan approved; the agent waits for you to say when to start.")
                : theme.Fg("success", "✓ Plan approved; plan mode is off."),
            "rejected" => theme.Fg("warning", "✗ Plan rejected; the agent keeps planning.") +
                (Str(details, "feedback") is { } feedback ? "\n" + theme.Fg("toolOutput", "Feedback: " + feedback) : ""),
            _ => theme.Fg("muted", "Plan mode was left without a review."),
        };
        // Pending: the whole plan, for the review below. Reviewed: collapsed to the outcome until expanded.
        if (state == "pending" || options.Expanded)
            container.AddChild(new Markdown(plan, 0, 0, Themes.GetMarkdownTheme()));
        else
            container.AddChild(new Text(theme.Fg("muted", $"Plan: {plan.Split('\n').Length} lines (") +
                theme.Fg("dim", KeybindingHints.KeyText("app.tools.expand")) + theme.Fg("muted", " to expand)"), 0, 0));
        container.AddChild(new Text(outcome ?? theme.Fg("muted", "Waiting for your review..."), 0, 0));
        return container;
    }

    /// <summary>The result's text, as pi's default card shows an error or an interrupted call.</summary>
    private static IComponent ContentText(JsonObject result, Theme theme)
    {
        var text = string.Join("\n", Arr(result, "content").Where(block => Str(block, "type") == "text").Select(block => Str(block, "text") ?? ""));
        var error = result["isError"] is JsonValue flag && flag.TryGetValue<bool>(out var isError) && isError;
        return new Text(theme.Fg(error ? "error" : "toolOutput", text), 0, 0);
    }
}
