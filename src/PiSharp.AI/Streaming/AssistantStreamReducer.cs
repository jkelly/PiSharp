using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using PiSharp.Contracts;

namespace PiSharp.AI;

/// <summary>An assistant message keeps every content block its stream opens (pi-ai builds output.content without a count bound);
/// the accumulated characters stay the memory bound.</summary>
public sealed record StreamLimits(int MaximumBlocks = int.MaxValue, int MaximumCharacters = PiRequestBudget.StreamCharacters);

/// <summary>One stream owns one reducer. Events remain immutable; only per-block accumulators change.</summary>
public sealed class AssistantStreamReducer
{
    private sealed class Block(AssistantContent content)
    {
        public AssistantContent Content = content;
        public readonly StringBuilder Text = new(content switch
        {
            TextContent text => text.Text,
            ThinkingContent thinking => thinking.Thinking,
            _ => ""
        });
        public readonly StringBuilder ToolJson = new();
        public bool Ended;
        public bool Provisional;
    }

    private readonly List<Block> _blocks = [];
    private readonly AssistantMessage _fallback;
    private readonly bool _allowPiMessagesIdentityReplacement;
    private readonly StreamLimits _limits;
    private AssistantMessage? _start;
    private StreamTerminalEvent? _terminal;
    private long _characters;
    private long _currentSourceEmissionCharacters;

    public AssistantStreamReducer(AssistantMessage fallback, StreamLimits? limits = null) : this(fallback, limits, false) { }

    public AssistantStreamReducer(AssistantMessage fallback, StreamLimits? limits, bool allowPiMessagesIdentityReplacement)
    {
        ArgumentNullException.ThrowIfNull(fallback);
        if (allowPiMessagesIdentityReplacement && fallback.Api != "pi-messages")
            throw new ArgumentException("Final identity replacement is specific to Pi Messages.", nameof(allowPiMessagesIdentityReplacement));
        _allowPiMessagesIdentityReplacement = allowPiMessagesIdentityReplacement;
        _fallback = fallback with { Content = [], StopReason = StopReason.Pending };
        _limits = limits ?? new();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_limits.MaximumBlocks);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_limits.MaximumCharacters);
    }

    public bool HasStarted => _start is not null;
    public bool IsTerminal => _terminal is not null;

    public void Apply(StreamEvent value)
    {
        if (_terminal is not null) throw new StreamProtocolException("An event follows stream settlement.");
        var size = (long)(value.SourceEmissionSnapshot?.ToString().Length ?? 0) +
            (value.SourceDrainSnapshot?.ToString().Length ?? 0);
        if (size > _limits.MaximumCharacters - _characters)
            throw new StreamLimitException("Owned source emission limit exceeded.");
        _currentSourceEmissionCharacters = size;
        try { ApplyCore(value); }
        finally { _currentSourceEmissionCharacters = 0; }
    }

    private void ApplyCore(StreamEvent value)
    {
        switch (value)
        {
            case StreamStarted start:
                if (_start is not null) throw new StreamProtocolException("The stream starts more than once.");
                if (!start.Partial.Content.IsEmpty || start.Partial.StopReason != StopReason.Pending)
                    throw new StreamProtocolException("Start must contain empty content and pending stopReason.");
                CheckSize(EnvelopeCharacters(start.Partial));
                _start = start.Partial;
                return;
            case StreamError error:
                if (error.Reason is not (StopReason.Error or StopReason.Aborted) || error.Message.StopReason != error.Reason)
                    throw new StreamProtocolException("Error terminal has an inconsistent reason.");
                CheckTerminalBounds(error.Message);
                CheckSize(MessageCharacters(error.Message) - _characters);
                _terminal = error with { SourceEmissionSnapshot = null, SourceDrainSnapshot = null };
                return;
            case StreamDone done:
                RequireStarted();
                if (done.Reason is StopReason.Pending or StopReason.Error or StopReason.Aborted || done.Message.StopReason != done.Reason)
                    throw new StreamProtocolException("Done terminal has an inconsistent reason.");
                CheckTerminalBounds(done.Message);
                if (_blocks.Any(block => !block.Ended) &&
                    !(done.Reason == StopReason.Length && _start!.Api == "openai-responses" &&
                      _blocks.Where(block => !block.Ended).All(block => block.Content is TextContent)))
                    throw new StreamProtocolException("Successful terminal has an unfinished content block.");
                var snapshot = Snapshot();
                if (done.Message.Api != snapshot.Api || done.Message.Provider != snapshot.Provider || done.Message.Model != snapshot.Model)
                    throw new StreamProtocolException("Terminal changes the requested model identity.");
                var expected = PiWireJson.WriteMessage(snapshot).Value.GetProperty("content");
                var actual = PiWireJson.WriteMessage(done.Message).Value.GetProperty("content");
                if (!JsonElement.DeepEquals(expected, actual))
                    throw new StreamProtocolException("Terminal content differs from authoritative content ends.");
                CheckSize(EnvelopeCharacters(done.Message) - EnvelopeCharacters(_start!));
                _terminal = done with { SourceEmissionSnapshot = null, SourceDrainSnapshot = null };
                return;
        }

        RequireStarted();
        switch (value)
        {
            case TextStarted start: Add(start.ContentIndex, start.Content); break;
            case ThinkingStarted start: Add(start.ContentIndex, start.Content); break;
            case ToolCallStarted start:
                if (_blocks.Any(block => block.Content is ToolCallContent tool && tool.Id == start.ToolCall.Id))
                    throw new StreamProtocolException("Duplicate tool-call ID.");
                RequireToolIdentity(start.ToolCall);
                Add(start.ContentIndex, start.ToolCall);
                break;
            case ToolCallProvisionalStarted start:
                if (!string.IsNullOrEmpty(start.ToolCall.Id) && _blocks.Any(block => block.Content is ToolCallContent tool && tool.Id == start.ToolCall.Id))
                    throw new StreamProtocolException("Duplicate provisional tool-call ID.");
                if (start.ToolCall.Arguments.Value.ValueKind != JsonValueKind.Object || start.ToolCall.Arguments.Value.EnumerateObject().Any())
                    throw new StreamProtocolException("Provisional arguments must start as an empty object.");
                Add(start.ContentIndex, start.ToolCall);
                _blocks[^1].Provisional = true;
                break;
            case ToolCallHeaderUpdated header:
                var provisional = Active<ToolCallContent>(header.ContentIndex);
                var previous = (ToolCallContent)provisional.Content;
                if (!provisional.Provisional || previous.Id.Length > 0 && previous.Id != header.Id ||
                    previous.Name.Length > 0 && previous.Name != header.Name)
                    throw new StreamProtocolException("Only empty provisional identity can be filled.");
                if (header.Id.Length > 0 && _blocks.Where(block => !ReferenceEquals(block, provisional))
                    .Any(block => block.Content is ToolCallContent tool && tool.Id == header.Id))
                    throw new StreamProtocolException("Duplicate filled tool-call ID.");
                CheckSize(header.Id.Length + (long)header.Name.Length - previous.Id.Length - previous.Name.Length);
                provisional.Content = previous with { Id = header.Id, Name = header.Name };
                break;
            case TextDelta delta: Append(Active<TextContent>(delta.ContentIndex).Text, delta.Delta); break;
            case ThinkingDelta delta: Append(Active<ThinkingContent>(delta.ContentIndex).Text, delta.Delta); break;
            case TextEnded end:
                var text = Active<TextContent>(end.ContentIndex);
                var textProperties = EndProperties(text.Content.ExtraProperties, end.ExtraProperties, "textSignature");
                CheckSize(end.Content.Length - text.Text.Length + PropertiesCharacters(textProperties) - PropertiesCharacters(text.Content.ExtraProperties));
                text.Content = new TextContent(end.Content, textProperties);
                text.Text.Clear(); text.Text.Append(end.Content); text.Ended = true; break;
            case ThinkingCheckpoint checkpoint:
                var pendingThinking = Active<ThinkingContent>(checkpoint.ContentIndex);
                var pendingProperties = EndProperties(pendingThinking.Content.ExtraProperties, checkpoint.ExtraProperties, "thinkingSignature", "redacted");
                CheckSize(checkpoint.Content.Length - pendingThinking.Text.Length + PropertiesCharacters(pendingProperties) - PropertiesCharacters(pendingThinking.Content.ExtraProperties));
                pendingThinking.Content = new ThinkingContent(checkpoint.Content, pendingProperties);
                pendingThinking.Text.Clear(); pendingThinking.Text.Append(checkpoint.Content); break;
            case ThinkingEnded end:
                var thinking = Active<ThinkingContent>(end.ContentIndex);
                var thinkingProperties = EndProperties(thinking.Content.ExtraProperties, end.ExtraProperties, "thinkingSignature", "redacted");
                CheckSize(end.Content.Length - thinking.Text.Length + PropertiesCharacters(thinkingProperties) - PropertiesCharacters(thinking.Content.ExtraProperties));
                thinking.Content = new ThinkingContent(end.Content, thinkingProperties);
                thinking.Text.Clear(); thinking.Text.Append(end.Content); thinking.Ended = true; break;
            case ToolCallCheckpoint checkpoint:
                Replace(Active<ToolCallContent>(checkpoint.ContentIndex).ToolJson, checkpoint.Json); break;
            case ToolCallDelta delta: Append(Active<ToolCallContent>(delta.ContentIndex).ToolJson, delta.Delta); break;
            case ToolCallEnded end:
                var call = Active<ToolCallContent>(end.ContentIndex);
                RequireToolIdentity(end.ToolCall);
                var original = (ToolCallContent)call.Content;
                if ((original.Id != end.ToolCall.Id || original.Name != end.ToolCall.Name) &&
                    !(_allowPiMessagesIdentityReplacement && _start!.Api == "pi-messages"))
                    throw new StreamProtocolException("Tool-call identity changes at its end.");
                if (_blocks.Where(block => !ReferenceEquals(block, call)).Any(block => block.Content is ToolCallContent tool && tool.Id == end.ToolCall.Id))
                    throw new StreamProtocolException("Duplicate final tool-call ID.");
                CheckSize(ContentCharacters(end.ToolCall) - ContentCharacters(original));
                call.Content = end.ToolCall; call.Ended = true; break;
            case ContentBlockFinalized finalized:
            {
                if (finalized.ContentIndex < 0 || finalized.ContentIndex >= _blocks.Count || _blocks[finalized.ContentIndex].Ended ||
                    _blocks[finalized.ContentIndex].Content.GetType() != finalized.Content.GetType())
                    throw new StreamProtocolException("A finalized block must be an open block of the same kind.");
                var block = _blocks[finalized.ContentIndex];
                if (finalized.Content is ToolCallContent tool)
                {
                    var started = (ToolCallContent)block.Content;
                    if (started.Id != tool.Id || started.Name != tool.Name)
                        throw new StreamProtocolException("Tool-call identity changes at its end.");
                    CheckSize(ContentCharacters(tool) - ContentCharacters(started));
                }
                var finalText = finalized.Content switch { TextContent finalTextContent => finalTextContent.Text, ThinkingContent finalThinking => finalThinking.Thinking, _ => null };
                if (finalText is not null) { CheckSize(finalText.Length - block.Text.Length); block.Text.Clear(); block.Text.Append(finalText); }
                block.Content = finalized.Content; block.Ended = true; break;
            }
            default: throw new StreamProtocolException("Unknown progress event.");
        }
    }

    public AssistantMessage Snapshot()
    {
        if (_terminal is not null) return _terminal.Message;
        var content = _blocks.Select(block => block.Content switch
        {
            TextContent text => (AssistantContent)(text with { Text = block.Text.ToString() }),
            ThinkingContent thinking => thinking with { Thinking = block.Text.ToString() },
            _ => block.Content
        }).ToImmutableArray();
        return (_start ?? _fallback) with { Content = content };
    }

    /// <summary>Unparsed fragments are available for display only and never replace executable arguments.</summary>
    public string GetToolJsonPreview(int index)
    {
        if (index < 0 || index >= _blocks.Count || _blocks[index].Content is not ToolCallContent)
            throw new StreamProtocolException("Tool preview has no tool block at that index.");
        return _blocks[index].ToolJson.ToString();
    }

    public StreamError Failure(ChatFailure failure)
    {
        var reason = failure.Kind == ChatFailureKind.Cancelled ? StopReason.Aborted : StopReason.Error;
        var snapshot = Snapshot();
        var properties = (snapshot.ExtraProperties ?? JsonFields.Empty)
            .Set("errorMessage", JsonData.Parse(JsonSerializer.Serialize(failure.Message)));
        return new(reason, snapshot with { StopReason = reason, ExtraProperties = properties });
    }

    private void Add(int index, AssistantContent content)
    {
        if (index != _blocks.Count) throw new StreamProtocolException("Content indices must be contiguous and start exactly once.");
        if (_blocks.Count >= _limits.MaximumBlocks) throw new StreamLimitException("Content block limit exceeded.");
        var block = new Block(content);
        CheckSize(ContentCharacters(content));
        _blocks.Add(block);
    }

    private Block Active<T>(int index) where T : AssistantContent
    {
        if (index < 0 || index >= _blocks.Count) throw new StreamProtocolException("Content event has no started block.");
        var block = _blocks[index];
        if (block.Content is not T) throw new StreamProtocolException("Content event points to the wrong block kind.");
        if (block.Ended) throw new StreamProtocolException("Content event follows the block end.");
        return block;
    }

    private void Append(StringBuilder builder, string text) { CheckSize(text.Length); builder.Append(text); }
    private void Replace(StringBuilder builder, string text)
    {
        CheckSize(text.Length - builder.Length); builder.Clear(); builder.Append(text);
    }
    private void CheckSize(long delta)
    {
        if (_characters + delta + _currentSourceEmissionCharacters > _limits.MaximumCharacters) throw new StreamLimitException("Accumulated response limit exceeded.");
        _characters += delta;
    }
    private static long ContentCharacters(AssistantContent content) => PropertiesCharacters(content.ExtraProperties) + (content switch
    {
        TextContent text => (long)text.Text.Length,
        ThinkingContent thinking => thinking.Thinking.Length,
        ToolCallContent tool => tool.Id.Length + (long)tool.Name.Length + tool.Arguments.ToString().Length,
        _ => 0
    });
    private static long PropertiesCharacters(JsonFields? properties) => properties is null ? 0 :
        properties.Values.Sum(property => property.Key.Length + (long)property.Value.ToString().Length);
    private static long EnvelopeCharacters(AssistantMessage message) =>
        message.Api.Length + (long)message.Provider.Length + message.Model.Length +
        PropertiesCharacters(message.ExtraProperties) + PropertiesCharacters(message.Usage.ExtraProperties) +
        PropertiesCharacters(message.Usage.Cost.ExtraProperties) + (message.Usage.Cost.SourceBinary64Cost?.ToString().Length ?? 0);
    private static long MessageCharacters(AssistantMessage message) =>
        EnvelopeCharacters(message) + message.Content.Sum(ContentCharacters);
    private void CheckTerminalBounds(AssistantMessage message)
    {
        if (message.Content.Length > _limits.MaximumBlocks) throw new StreamLimitException("Content block limit exceeded.");
        if (MessageCharacters(message) + _currentSourceEmissionCharacters > _limits.MaximumCharacters) throw new StreamLimitException("Accumulated response limit exceeded.");
    }
    private void RequireStarted()
    {
        if (_start is null) throw new StreamProtocolException("Content or successful completion precedes start.");
    }
    private static void RequireToolIdentity(ToolCallContent call)
    {
        if (string.IsNullOrWhiteSpace(call.Id) || string.IsNullOrWhiteSpace(call.Name))
            throw new StreamProtocolException("Tool-call ID and name are required.");
        // Arguments are any JSON value: parseStreamingJson, a provider SDK or a pi-messages toolCall may produce an array, a
        // string, a number, a boolean or null, and upstream keeps it.
    }
    private static JsonFields EndProperties(JsonFields? initial, JsonFields? final, params string[] names)
    {
        var properties = initial ?? JsonFields.Empty;
        foreach (var name in names)
        {
            properties = properties.Remove(name);
            if (final is not null && final.TryGet(name, out var value)) properties = properties.Set(name, value!);
        }
        return properties;
    }
}

public sealed class StreamLimitException(string message) : Exception(message);
