// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/types.ts.
using System.Collections.Immutable;

namespace PiSharp.Contracts.ModelOperations;

/// <summary>types.ts <c>ModelType</c>: what a catalog entry is for. Chat is the default for entries without a type.</summary>
public enum ModelType { Chat, Image, Classifier }

/// <summary>types.ts <c>ClassifierStopReason</c> and <c>ImagesStopReason</c>.</summary>
public enum ModelOperationStopReason { Stop, Error, Aborted }

/// <summary>types.ts <c>ImagesInputContent</c>/<c>ImagesOutputContent</c>: a text block or an <see cref="ImageContent"/>.</summary>
public abstract record ImagesContentBlock
{
    private protected ImagesContentBlock() { }
}

/// <summary>types.ts <c>TextContent</c> inside image requests and results.</summary>
public sealed record ImagesTextBlock(string Text) : ImagesContentBlock;

/// <summary>types.ts <c>ImageContent</c>: base64 data and its MIME type, e.g. <c>image/png</c>.</summary>
public sealed record ImageContent(string Data, string MimeType) : ImagesContentBlock;

/// <summary>types.ts <c>ClassifierQuestion</c>.</summary>
public abstract record ClassifierQuestion(string Instructions);

/// <summary>A choice between labels. <see cref="Criteria"/> maps each label to its meaning, in source order; an empty
/// meaning is allowed.</summary>
public sealed record ClassifierChoiceQuestion(string Instructions, ImmutableArray<KeyValuePair<string, string>> Criteria)
    : ClassifierQuestion(Instructions);

/// <summary>A score over ordered levels, lowest first.</summary>
public sealed record ClassifierScoreQuestion(string Instructions, ImmutableArray<string> Criteria) : ClassifierQuestion(Instructions);

/// <summary>A yes/no question with the meanings of true and false (<c>criteria: { true, false }</c>).</summary>
public sealed record ClassifierBoolQuestion(string Instructions, string True, string False) : ClassifierQuestion(Instructions);

/// <summary>types.ts <c>ClassifierContext</c>. <see cref="State"/> must be a JSON object. <see cref="Questions"/> keep
/// their ids in source order and must be unique. <see cref="Images"/> are judged together with the state; only models
/// whose input includes <c>"image"</c> accept them.</summary>
public sealed record ClassifierContext(JsonData State, ImmutableArray<KeyValuePair<string, ClassifierQuestion>> Questions,
    ImmutableArray<ImageContent> Images = default)
{
    /// <summary>The images, or empty when none were supplied.</summary>
    public ImmutableArray<ImageContent> ImageList => Images.IsDefault ? [] : Images;
}

/// <summary>types.ts <c>ClassifierAnswer</c>.</summary>
public abstract record ClassifierAnswer
{
    private protected ClassifierAnswer() { }
}

/// <summary>The chosen label, the probability of every label, and the confidence.</summary>
public sealed record ClassifierChoiceAnswer(string Choice, ImmutableArray<KeyValuePair<string, double>> Probabilities, double Confidence)
    : ClassifierAnswer;

/// <summary>The expected level index and the confidence.</summary>
public sealed record ClassifierScoreAnswer(double Score, double Confidence) : ClassifierAnswer;

/// <summary>The probability of <c>true</c>.</summary>
public sealed record ClassifierBoolAnswer(double Probability) : ClassifierAnswer;

/// <summary>types.ts <c>ClassifierResult</c>. Classification never throws for provider failures: they arrive as
/// <see cref="ModelOperationStopReason.Error"/> or <see cref="ModelOperationStopReason.Aborted"/> with an
/// <see cref="ErrorMessage"/> and no answers. <see cref="Usage"/> is the token usage priced at the model's catalog rates,
/// present whenever the service reported token counts, also for a failed request that was billed.</summary>
public sealed record ClassifierResult(string Api, string Provider, string Model,
    ImmutableArray<KeyValuePair<string, ClassifierAnswer>> Answers, ModelOperationStopReason StopReason, long Timestamp)
{
    public TokenUsage? Usage { get; init; }
    public string? ErrorMessage { get; init; }

    /// <summary>The answer to question <paramref name="id"/>, or null.</summary>
    public ClassifierAnswer? GetAnswer(string id)
    {
        foreach (var answer in Answers.IsDefault ? [] : Answers) if (answer.Key == id) return answer.Value;
        return null;
    }
}

/// <summary>types.ts <c>ImagesContext</c>: text prompts and optional reference images, in order.</summary>
public sealed record ImagesContext(ImmutableArray<ImagesContentBlock> Input);

/// <summary>types.ts <c>AssistantImages</c>. Image generation never throws for provider failures: they arrive as
/// <see cref="ModelOperationStopReason.Error"/> or <see cref="ModelOperationStopReason.Aborted"/> with an
/// <see cref="ErrorMessage"/>.</summary>
public sealed record AssistantImages(string Api, string Provider, string Model, ImmutableArray<ImagesContentBlock> Output,
    ModelOperationStopReason StopReason, long Timestamp)
{
    public string? ResponseId { get; init; }
    public TokenUsage? Usage { get; init; }
    public string? ErrorMessage { get; init; }
}
