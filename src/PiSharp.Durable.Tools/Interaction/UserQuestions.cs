// PiSharp-only (decision 0005): no upstream counterpart. The ask_user_question tool, modelled on Claude Code's AskUserQuestion: 1-4
// multiple-choice questions, 2-4 options each, multi-select, and a free-text "Other" answer the user always has. The call stays open
// durably until the host answers or declines (UserInteraction.cs), and the answers come back as the tool result in the same turn.
using System.Collections.Immutable;
using System.Text;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Internal;
using PiSharp.Durable.Internal.PiAi.Validation;

namespace PiSharp.Durable.Tools;

/// <summary>
/// The <c>ask_user_question</c> tool and its host API. The tool is replay-safe and only the model may call it. While it waits, its
/// <c>pi.live</c> tool slot details are <c>{kind: "pisharp.questions", version: 1, state: "pending", questions}</c>; a host lists them with
/// <see cref="PendingAsync"/> (or <see cref="UserInteraction.PendingIn(ConversationView)"/> from a view it already watches) and answers
/// with <see cref="AnswerAsync"/> or <see cref="DeclineAsync"/>, each one commit.
/// </summary>
public static class UserQuestions
{
    /// <summary>The tool name.</summary>
    public const string ToolName = "ask_user_question";

    /// <summary>The <c>kind</c> of the tool's details.</summary>
    public const string DetailsKind = "pisharp.questions";

    /// <summary>The interaction document's <c>kind</c> for questions.</summary>
    internal const string DocKind = "questions";

    internal const int MaxQuestionChars = 1000;
    internal const int MaxDescriptionChars = 500;
    internal const int MaxOtherChars = 4000;

    internal const string Description =
        "Ask the user 1-4 multiple-choice questions and wait for the answers. Use it to gather preferences or requirements, clarify ambiguous " +
        "instructions, or let the user choose between approaches. Each question has 2-4 options; the user can always answer \"Other\" with " +
        "their own text, so never add an Other option. Set multiSelect when several options can apply. If you recommend an option, list it " +
        "first and append \"(Recommended)\" to its label. The answers come back as this tool's result. In plan mode, do not use it to ask " +
        "whether the plan is ready or approved; call exit_plan_mode.";

    /// <summary>TypeBox <c>Type.Object({ questions: Type.Array(Type.Object({ question, header, options: Type.Array(Type.Object({ label,
    /// description }), {minItems: 2, maxItems: 4}), multiSelect }), {minItems: 1, maxItems: 4}) })</c>, as TypeBox prints it.</summary>
    internal static readonly JsonObjectValue Parameters = (JsonObjectValue)JsonValue.Parse(
        """{"type":"object","required":["questions"],"properties":{"questions":{"type":"array","items":{"type":"object","required":["question","header","options","multiSelect"],"properties":{"question":{"type":"string","description":"The complete question to ask the user, clear and specific."},"header":{"type":"string","maxLength":12,"description":"A very short label for the question, shown as a tab (at most 12 characters), for example \"Auth method\"."},"options":{"type":"array","items":{"type":"object","required":["label","description"],"properties":{"label":{"type":"string","description":"The choice as the user sees and selects it, in a few words."},"description":{"type":"string","description":"What this choice means or what happens if it is chosen."}}},"minItems":2,"maxItems":4,"description":"The 2-4 choices. Do not add an \"Other\" option; the user can always answer in their own words."},"multiSelect":{"type":"boolean","description":"Whether the user may select more than one option."}}},"minItems":1,"maxItems":4,"description":"The 1-4 questions to ask."}}}""");

    /// <summary>The <c>ask_user_question</c> tool: replay-safe, callable only by the model. Each call creates a new registration.</summary>
    public static ToolRegistration CreateTool() => Define.Tool(new ToolRegistration
    {
        Name = ToolName,
        Description = Description,
        Parameters = Parameters,
        Callers = [ToolCaller.Model],
        Replay = ToolReplay.Safe,
        PrepareArguments = PrepareArguments,
        Execute = ExecuteAsync,
    });

    /// <summary>The interaction a tool slot waits for (<see cref="UserInteraction.PendingOf"/>): questions or a plan review.</summary>
    public static PendingInteraction? PendingOf(ConversationId conversationId, JsonObjectValue toolSlot) =>
        UserInteraction.PendingOf(conversationId, toolSlot);

    /// <summary>Every pending interaction of the conversation, questions and plan reviews, lowest task ID first
    /// (<see cref="UserInteraction.PendingAsync"/>).</summary>
    public static Task<IReadOnlyList<PendingInteraction>> PendingAsync(IConversation conversation, DurableContext context) =>
        UserInteraction.PendingAsync(conversation, context);

    /// <summary>The questions of pending details (<see cref="PendingInteraction.Details"/> of kind <see cref="DetailsKind"/>).</summary>
    public static ImmutableArray<UserQuestion> QuestionsOf(JsonObjectValue details)
    {
        ArgumentNullException.ThrowIfNull(details);
        return details.TryGetValue("questions", out var questions) && questions is JsonArrayValue list ? [.. list.Select(UserQuestion.FromJson)] : [];
    }

    /// <summary>
    /// Answers the questions of tool call <paramref name="toolTask"/> in one commit of <paramref name="conversation"/>; the waiting call
    /// returns the answers as its result. Answering again with the same answers does nothing. Throws <see cref="ArgumentException"/> for
    /// answers that do not fit the questions (one answer per question; a single-select question takes exactly one of its labels or an
    /// "Other" text, a multi-select one at least one label or an "Other" text; "Other" text at most 4000 characters), and
    /// <see cref="InteractionNotPendingException"/> when the call no longer waits (ended, aborted, or answered differently).
    /// </summary>
    public static Task AnswerAsync(IConversation conversation, TaskId toolTask, QuestionResponse response, DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(context);
        return conversation.CommitAsync(async tx =>
        {
            var doc = await UserInteraction.OpenAsync(tx, conversation.Id, toolTask, DocKind).ConfigureAwait(false);
            var questions = QuestionsOf((JsonObjectValue)doc.ToValue());
            var json = ResponseJson(Normalize(response, questions));
            if (doc.Get("response") is JsonObjectValue existing)
            {
                if (JsonValue.DeepEquals(existing.Remove("at"), json)) return;
                throw new InteractionNotPendingException(toolTask, "it was already answered");
            }
            doc.Set("response", json.SetItem("at", UserInteraction.Now()));
        }, context);
    }

    /// <summary>Declines the questions of tool call <paramref name="toolTask"/>: the call returns that the user declined to answer.
    /// Otherwise as <see cref="AnswerAsync"/>.</summary>
    public static Task DeclineAsync(IConversation conversation, TaskId toolTask, DurableContext context) =>
        AnswerAsync(conversation, toolTask, QuestionResponse.Declined, context);

    // ─── Tool ───────────────────────────────────────────────────────────────

    /// <summary>Repairs shapes models send (as <c>edit</c>'s PrepareEditArguments does): <c>questions</c> as a JSON string, a single question
    /// object instead of an array, and a missing <c>multiSelect</c> (false).</summary>
    internal static JsonValue PrepareArguments(JsonValue input)
    {
        if (input is not JsonObjectValue args || !args.TryGetValue("questions", out var questions)) return input;
        if (questions is JsonStringValue text)
        {
            try { questions = JsonValue.Parse(text.Value); }
            catch (Exception) { return input; } // Not JSON: validation reports the string.
        }
        if (questions is JsonObjectValue single) questions = JsonValue.Array(single);
        if (questions is not JsonArrayValue list) return args;
        var repaired = list.Select(item => item is JsonObjectValue question && !question.ContainsKey("multiSelect")
            ? question.SetItem("multiSelect", false) : item);
        return args.SetItem("questions", JsonValue.Array(repaired));
    }

    private static async Task<ToolExecutionResult> ExecuteAsync(JsonValue arguments, IToolExecutionApi api, DurableContext context)
    {
        var questions = ((JsonArrayValue)((JsonObjectValue)arguments)["questions"]).Select(UserQuestion.FromJson).ToImmutableArray();
        if (Problem(questions) is { } problem)
            return new ToolExecutionResult { Output = ToolContent.Text($"Invalid questions: {problem}"), IsError = true };
        var questionsJson = JsonValue.Array(questions.Select(question => (JsonValue)question.ToJson()));
        // Created once: a rerun after a close or crash finds the document, and the answer the host may have committed meanwhile.
        var doc = await api.CommitAsync(async tx =>
        {
            var draft = await tx.DocAsync(UserInteraction.InteractionDoc, api.TaskId).ConfigureAwait(false);
            if (!draft.ContainsKey("kind"))
            {
                draft.Set("kind", DocKind);
                draft.Set("questions", questionsJson);
                draft.Set("createdAt", UserInteraction.Now());
            }
            return (JsonObjectValue)draft.ToValue();
        }, context).ConfigureAwait(false);
        var asked = QuestionsOf(doc);
        if (UserInteraction.ResponseOf(doc) is null)
        {
            await api.DetailsAsync(Details("pending", asked, null), context).ConfigureAwait(false);
            doc = await InteractionWait.WaitForResponseAsync(api, context).ConfigureAwait(false);
        }
        return Result(asked, ParseResponse(UserInteraction.ResponseOf(doc)!));
    }

    /// <summary>Why questions the schema accepted cannot be asked, or null.</summary>
    private static string? Problem(ImmutableArray<UserQuestion> questions)
    {
        var headers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < questions.Length; index++)
        {
            var question = questions[index];
            var at = $"question {index + 1}";
            if (Js.Trim(question.Question).Length == 0) return $"{at} is empty";
            if (question.Question.Length > MaxQuestionChars) return $"{at} is longer than {MaxQuestionChars} characters";
            if (Js.Trim(question.Header).Length == 0) return $"{at} has an empty header";
            if (!headers.Add(Js.Trim(question.Header))) return $"{at} repeats the header \"{question.Header}\"";
            var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var option in question.Options)
            {
                var label = Js.Trim(option.Label);
                if (label.Length == 0) return $"{at} has an option without a label";
                if (string.Equals(label, "Other", StringComparison.OrdinalIgnoreCase))
                    return $"{at} has an \"Other\" option; the user can always answer in their own words, so leave it out";
                if (!labels.Add(label)) return $"{at} repeats the option \"{option.Label}\"";
                if (option.Description.Length > MaxDescriptionChars) return $"{at}: the description of \"{option.Label}\" is longer than {MaxDescriptionChars} characters";
            }
        }
        return null;
    }

    /// <summary>The tool slot details: <c>{kind, version, state, questions, answers?}</c>.</summary>
    private static JsonObjectValue Details(string state, ImmutableArray<UserQuestion> questions, ImmutableArray<QuestionAnswer>? answers) =>
        new RecordJson.Writer()
            .Add("kind", DetailsKind)
            .Number("version", 1)
            .Add("state", state)
            .Add("questions", JsonValue.Array(questions.Select(question => (JsonValue)question.ToJson())))
            .Add("answers", answers is { } given ? JsonValue.Array(given.Select(answer => (JsonValue)answer.ToJson())) : null)
            .Build();

    private static ToolExecutionResult Result(ImmutableArray<UserQuestion> questions, QuestionResponse response)
    {
        if (response.Status == QuestionResponseStatus.Declined)
            return new ToolExecutionResult
            {
                Output = ToolContent.Text("The user declined to answer these questions. Proceed with your best judgement and state the assumptions " +
                    "you make, or ask in plain text if you cannot proceed."),
                Details = Details("declined", questions, null),
            };
        var text = new StringBuilder("User answered your questions:\n");
        for (var index = 0; index < questions.Length; index++)
        {
            var question = questions[index];
            text.Append(index + 1).Append(". [").Append(question.Header).Append("] ").Append(question.Question).Append('\n');
            var answer = index < response.Answers.Length ? response.Answers[index] : new QuestionAnswer([]);
            if (answer.Selected.Length > 0) text.Append("   Answer: ").Append(string.Join(", ", answer.Selected)).Append('\n');
            if (answer.Other is { } other) text.Append("   Other: \"").Append(other).Append("\"\n");
        }
        text.Append("Continue with these answers in mind.");
        return new ToolExecutionResult { Output = ToolContent.Text(text.ToString()), Details = Details("answered", questions, response.Answers) };
    }

    // ─── Responses ──────────────────────────────────────────────────────────

    /// <summary>The response checked against the questions, with "Other" text trimmed and empty text dropped.</summary>
    private static QuestionResponse Normalize(QuestionResponse response, ImmutableArray<UserQuestion> questions)
    {
        if (response.Status == QuestionResponseStatus.Declined) return QuestionResponse.Declined;
        if (response.Status != QuestionResponseStatus.Answered) throw new ArgumentException("Unknown response status", nameof(response));
        var answers = response.Answers.IsDefault ? [] : response.Answers;
        if (answers.Length != questions.Length)
            throw new ArgumentException($"Expected {questions.Length} answer(s), one per question, got {answers.Length}", nameof(response));
        var normalized = ImmutableArray.CreateBuilder<QuestionAnswer>(answers.Length);
        for (var index = 0; index < answers.Length; index++)
        {
            var question = questions[index];
            var answer = answers[index] ?? throw new ArgumentException($"Answer {index + 1} is null", nameof(response));
            var selected = answer.Selected.IsDefault ? [] : answer.Selected;
            var other = answer.Other is null ? null : Js.Trim(answer.Other);
            if (other is { Length: 0 }) other = null;
            if (other is { Length: > MaxOtherChars }) throw new ArgumentException($"Answer {index + 1}: the other text is longer than {MaxOtherChars} characters", nameof(response));
            var labels = question.Options.Select(option => option.Label).ToList();
            foreach (var label in selected)
                if (!labels.Contains(label, StringComparer.Ordinal)) throw new ArgumentException($"Answer {index + 1}: \"{label}\" is not an option of question {index + 1}", nameof(response));
            if (selected.Distinct(StringComparer.Ordinal).Count() != selected.Length) throw new ArgumentException($"Answer {index + 1} selects an option twice", nameof(response));
            if (question.MultiSelect)
            {
                if (selected.Length == 0 && other is null) throw new ArgumentException($"Answer {index + 1} selects nothing and has no other text", nameof(response));
                // Selected in the question's option order, so equal answers compare equal.
                selected = [.. labels.Where(label => selected.Contains(label, StringComparer.Ordinal))];
            }
            else if ((selected.Length == 1) == (other is not null) || selected.Length > 1)
                throw new ArgumentException($"Answer {index + 1} must select exactly one option or give other text", nameof(response));
            normalized.Add(new QuestionAnswer(selected, other));
        }
        return new QuestionResponse(QuestionResponseStatus.Answered, normalized.MoveToImmutable());
    }

    /// <summary><c>{status, answers?}</c>, without <c>at</c>.</summary>
    private static JsonObjectValue ResponseJson(QuestionResponse response) => new RecordJson.Writer()
        .Add("status", response.Status == QuestionResponseStatus.Answered ? "answered" : "declined")
        .Add("answers", response.Status == QuestionResponseStatus.Answered ? JsonValue.Array(response.Answers.Select(answer => (JsonValue)answer.ToJson())) : null)
        .Build();

    private static QuestionResponse ParseResponse(JsonObjectValue response)
    {
        if (response.TryGetValue("status", out var status) && status is JsonStringValue { Value: "answered" } &&
            response.TryGetValue("answers", out var answers) && answers is JsonArrayValue list)
            return new QuestionResponse(QuestionResponseStatus.Answered, [.. list.Select(QuestionAnswer.FromJson)]);
        return QuestionResponse.Declined;
    }
}

/// <summary>One question of <c>ask_user_question</c>.</summary>
/// <param name="Question">The question.</param>
/// <param name="Header">A short label (at most 12 characters), shown as a tab.</param>
/// <param name="Options">The 2-4 choices.</param>
/// <param name="MultiSelect">Whether more than one choice may be selected.</param>
public sealed record UserQuestion(string Question, string Header, ImmutableArray<QuestionOption> Options, bool MultiSelect)
{
    /// <summary><c>{question, header, options: [{label, description}], multiSelect}</c>.</summary>
    public JsonObjectValue ToJson() => new RecordJson.Writer()
        .Add("question", Question)
        .Add("header", Header)
        .Add("options", JsonValue.Array(Options.Select(option => (JsonValue)option.ToJson())))
        .Add("multiSelect", MultiSelect)
        .Build();

    /// <summary>Reads <see cref="ToJson"/>'s shape.</summary>
    public static UserQuestion FromJson(JsonValue json)
    {
        var value = (JsonObjectValue)json;
        return new UserQuestion(Text(value, "question"), Text(value, "header"),
            value.TryGetValue("options", out var options) && options is JsonArrayValue list ? [.. list.Select(QuestionOption.FromJson)] : [],
            value.TryGetValue("multiSelect", out var multi) && multi is JsonBooleanValue { Value: true });
    }

    internal static string Text(JsonObjectValue value, string key) => value.TryGetValue(key, out var text) && text is JsonStringValue s ? s.Value : "";
}

/// <summary>One choice of a <see cref="UserQuestion"/>.</summary>
/// <param name="Label">The choice as the user selects it.</param>
/// <param name="Description">What it means.</param>
public sealed record QuestionOption(string Label, string Description)
{
    /// <summary><c>{label, description}</c>.</summary>
    public JsonObjectValue ToJson() => new RecordJson.Writer().Add("label", Label).Add("description", Description).Build();

    /// <summary>Reads <see cref="ToJson"/>'s shape.</summary>
    public static QuestionOption FromJson(JsonValue json)
    {
        var value = (JsonObjectValue)json;
        return new QuestionOption(UserQuestion.Text(value, "label"), UserQuestion.Text(value, "description"));
    }
}

/// <summary>The answer to one question: the selected option labels and the user's own text ("Other").</summary>
/// <param name="Selected">Labels of the selected options, exactly as the question lists them.</param>
/// <param name="Other">The user's own text; null when none.</param>
public sealed record QuestionAnswer(ImmutableArray<string> Selected, string? Other = null)
{
    /// <summary><c>{selected, other?}</c>.</summary>
    public JsonObjectValue ToJson() => new RecordJson.Writer()
        .Add("selected", JsonValue.Array((Selected.IsDefault ? [] : Selected).Select(label => (JsonValue)label)))
        .Add("other", Other)
        .Build();

    /// <summary>Reads <see cref="ToJson"/>'s shape.</summary>
    public static QuestionAnswer FromJson(JsonValue json)
    {
        var value = (JsonObjectValue)json;
        return new QuestionAnswer(
            value.TryGetValue("selected", out var selected) && selected is JsonArrayValue list ? [.. list.OfType<JsonStringValue>().Select(label => label.Value)] : [],
            value.TryGetValue("other", out var other) && other is JsonStringValue text ? text.Value : null);
    }
}

/// <summary>How the user responded.</summary>
public enum QuestionResponseStatus
{
    /// <summary>The user answered every question.</summary>
    Answered,
    /// <summary>The user declined to answer.</summary>
    Declined,
}

/// <summary>The user's response to an <c>ask_user_question</c> call: one answer per question, or a decline.</summary>
/// <param name="Status">Answered or declined.</param>
/// <param name="Answers">One answer per question, in order; empty when declined.</param>
public sealed record QuestionResponse(QuestionResponseStatus Status, ImmutableArray<QuestionAnswer> Answers)
{
    /// <summary>A decline.</summary>
    public static QuestionResponse Declined { get; } = new(QuestionResponseStatus.Declined, []);

    /// <summary>Answers, one per question.</summary>
    public static QuestionResponse Answered(params IEnumerable<QuestionAnswer> answers) => new(QuestionResponseStatus.Answered, [.. answers]);
}
