// Decision 0011 (run W4, R8): a preset as the page it is loaded into takes it. GET /api/presets/{id}/load?page= answers this, and
// presets-host.js applies it with the page's own writers (request-spec.js applySpec on the Playground; Compare's shared fields and
// /compare/column fragments; bench-setup.js openSession on the Tool Bench). Only the preset's parts are in the payload ("parts"), and the
// page keeps what the preset left out. A preset from another page is converted here, never silently: everything the target page cannot take
// (Compare's other columns in the Playground, tools in Compare, options the Tool Bench does not send, a bench's matching mocks in the
// Playground) is named in "notes", which the page shows when it loads the preset.
//   playground  {spec: {provider, model, account, systemPrompt, messages, options..., headers, toolChoice}, tools: [{name, description,
//                parameters, enabled, mock?, mockError?}], parallel}
//   compare     {draft: {systemPrompt, prompt, thinking, maxTokens, temperature, columns: [{provider, model, account, thinking, temperature}]}}
//   bench       {setup: {provider, model, account, thinking, systemPrompt, tools, toolChoice, parallel, rules}, messages: [texts]}
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.WebAgent.Bench;
using PiSharp.WebAgent.Compare;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Runs;

namespace PiSharp.WebAgent.Presets;

/// <summary>A preset converted for a page.</summary>
/// <param name="Page">The page (<see cref="PresetPages"/>).</param>
/// <param name="Parts">The parts the payload sets; the page keeps the others.</param>
/// <param name="Notes">What was converted or left out, for the user.</param>
/// <param name="Payload">What the page applies.</param>
public sealed record PresetLoadPlan(string Page, IReadOnlyList<string> Parts, IReadOnlyList<string> Notes, JsonObject Payload);

/// <summary>Presets converted for the pages.</summary>
public static class PresetLoad
{
    /// <summary><paramref name="preset"/> as <paramref name="page"/> takes it.</summary>
    /// <exception cref="ArgumentException"><paramref name="page"/> is not a page presets load into.</exception>
    public static PresetLoadPlan For(Preset preset, string page)
    {
        ArgumentNullException.ThrowIfNull(preset);
        return page switch
        {
            PresetPages.Playground => Playground(preset),
            PresetPages.Compare => Compare(preset),
            PresetPages.Bench => Bench(preset),
            _ => throw new ArgumentException("A preset loads into the Playground, Compare or the Tool Bench."),
        };
    }

    // ---- Playground ------------------------------------------------------------------------------------------------------------

    private static PresetLoadPlan Playground(Preset preset)
    {
        var parts = new List<string>();
        var notes = new List<string>();
        var spec = new JsonObject();
        var payload = new JsonObject { ["spec"] = spec };
        var options = preset.Options;
        if (preset.Has(PresetParts.Model))
        {
            parts.Add(PresetParts.Model);
            if (preset.Columns is { Count: > 0 } columns)
            {
                var first = columns[0];
                spec["provider"] = first.Provider;
                spec["model"] = first.Model;
                if (first.Account is { } account)
                {
                    spec["account"] = account;
                    notes.Add(AccountNote("The Playground", first.Provider));
                }
                if (columns.Count > 1)
                    notes.Add($"The Playground runs one model: the first column's ({first.Provider}/{first.Model}) is loaded and {Count(columns.Count - 1, "other model")} {Are(columns.Count - 1)} left out.");
                if (first.Overrides) options = (options ?? new PresetOptions()) with { Thinking = first.Thinking ?? options?.Thinking, Temperature = first.Temperature ?? options?.Temperature };
            }
            else
            {
                spec["provider"] = preset.Provider;
                spec["model"] = preset.Model;
                if (preset.Account is { } account)
                {
                    spec["account"] = account;
                    notes.Add(AccountNote("The Playground", preset.Provider!));
                }
            }
        }
        if (options is { Empty: false })
        {
            parts.Add(PresetParts.Parameters);
            foreach (var (key, value) in Node(options).AsObject().ToList()) spec[key] = value?.DeepClone();
        }
        else if (preset.Has(PresetParts.Parameters))
        {
            parts.Add(PresetParts.Parameters);
        }
        if (preset.Rules is not null) notes.Add("The auto-respond rules are the Tool Bench's; the Playground leaves them out.");
        if (preset.Has(PresetParts.Headers))
        {
            parts.Add(PresetParts.Headers);
            spec["headers"] = Node(preset.Headers);
        }
        if (preset.Has(PresetParts.SystemPrompt))
        {
            parts.Add(PresetParts.SystemPrompt);
            spec["systemPrompt"] = preset.SystemPrompt;
        }
        if (preset.Has(PresetParts.Messages))
        {
            parts.Add(PresetParts.Messages);
            spec["messages"] = Node(preset.Messages);
        }
        if (preset.Has(PresetParts.Tools))
        {
            parts.Add(PresetParts.Tools);
            var tools = new JsonArray();
            var trimmed = 0;
            foreach (var tool in preset.Tools ?? [])
            {
                var item = new JsonObject { ["name"] = tool.Name, ["description"] = tool.Description ?? "", ["parameters"] = tool.Parameters?.DeepClone(), ["enabled"] = tool.Enabled };
                var mock = tool.Mocks.FirstOrDefault(candidate => candidate.Fallback) ?? tool.Mocks.FirstOrDefault();
                if (mock is not null)
                {
                    item["mock"] = mock.Text;
                    item["mockError"] = mock.IsError;
                }
                if (tool.Mocks.Count > 1 || tool.Mocks.Any(candidate => !candidate.Fallback)) trimmed++;
                tools.Add(item);
            }
            payload["tools"] = tools;
            if (trimmed > 0)
                notes.Add($"The Playground keeps one mock response per tool: {Count(trimmed, "tool")} with matching or several mocks {(trimmed == 1 ? "keeps" : "keep")} the fallback (or the first) only.");
            var choice = preset.ToolChoice;
            if (choice == "any")
            {
                choice = "required";
                notes.Add("The Tool Bench's tool choice \"any\" is \"required\" in the Playground.");
            }
            if (choice is not null) spec["toolChoice"] = choice;
            if (preset.Parallel is { } parallel) payload["parallel"] = parallel;
        }
        return new PresetLoadPlan(PresetPages.Playground, parts, notes, payload);
    }

    // ---- Compare -------------------------------------------------------------------------------------------------------------------

    private static PresetLoadPlan Compare(Preset preset)
    {
        var parts = new List<string>();
        var notes = new List<string>();
        var draft = new JsonObject();
        if (preset.Has(PresetParts.Model))
        {
            parts.Add(PresetParts.Model);
            if (preset.Columns is { Count: > 0 } columns)
            {
                draft["columns"] = Node(columns);
                if (columns.FirstOrDefault(column => column.Account is not null) is { } withAccount)
                    notes.Add(AccountNote("Compare", withAccount.Provider));
            }
            else
            {
                draft["columns"] = Node(new[] { new CompareColumnDraft { Provider = preset.Provider!, Model = preset.Model!, Account = preset.Account } });
                notes.Add($"Compare runs two to four models: the preset's model ({preset.Provider}/{preset.Model}) goes in the first column and the other columns stay as they are.");
                if (preset.Account is not null) notes.Add(AccountNote("Compare", preset.Provider!));
            }
        }
        if (preset.Options is { Empty: false } options)
        {
            var kept = new PresetOptions { Thinking = options.Thinking, MaxTokens = options.MaxTokens, Temperature = options.Temperature };
            var dropped = options.Entries().Select(entry => entry.Name).Where(name => !PresetRules.CompareOptions.Contains(name)).ToList();
            if (dropped.Count > 0) notes.Add($"Compare sends thinking, max tokens and temperature only: {string.Join(", ", dropped)} {(dropped.Count == 1 ? "is" : "are")} left out.");
            parts.Add(PresetParts.Parameters);
            if (kept.Thinking is { } thinking) draft["thinking"] = thinking;
            if (kept.MaxTokens is { } max) draft["maxTokens"] = max;
            if (kept.Temperature is { } temperature) draft["temperature"] = temperature;
        }
        if (preset.Rules is not null) notes.Add("The auto-respond rules are the Tool Bench's; Compare leaves them out.");
        if (preset.Has(PresetParts.Headers)) notes.Add($"Compare sends no extra headers: the preset's {Count(preset.Headers!.Count, "header")} {(preset.Headers.Count == 1 ? "is" : "are")} left out.");
        if (preset.Has(PresetParts.SystemPrompt))
        {
            parts.Add(PresetParts.SystemPrompt);
            draft["systemPrompt"] = preset.SystemPrompt;
        }
        if (preset.Has(PresetParts.Messages))
        {
            var messages = preset.Messages!;
            var prompt = messages.LastOrDefault(message => message.Role == "user" && !string.IsNullOrWhiteSpace(message.Text));
            if (prompt is null)
            {
                notes.Add("Compare sends one prompt, and the preset has no user message with text, so its messages are left out.");
            }
            else
            {
                parts.Add(PresetParts.Messages);
                draft["prompt"] = prompt.Text;
                if (messages.Count > 1) notes.Add($"Compare sends one prompt: the last user message is used and {Count(messages.Count - 1, "other message")} {Are(messages.Count - 1)} left out.");
                if (prompt.Images is { Count: > 0 }) notes.Add("Compare sends text only: the prompt's images are left out.");
            }
        }
        if (preset.Tools is { Count: > 0 } tools) notes.Add($"Compare sends no tools: the preset's {Count(tools.Count, "tool")} {(tools.Count == 1 ? "is" : "are")} left out.");
        return new PresetLoadPlan(PresetPages.Compare, parts, notes, new JsonObject { ["draft"] = draft });
    }

    // ---- Tool Bench ----------------------------------------------------------------------------------------------------------------

    private static PresetLoadPlan Bench(Preset preset)
    {
        var parts = new List<string>();
        var notes = new List<string>();
        var setup = new JsonObject();
        var payload = new JsonObject { ["setup"] = setup };
        var thinking = preset.Options?.Thinking;
        if (preset.Has(PresetParts.Model))
        {
            parts.Add(PresetParts.Model);
            if (preset.Columns is { Count: > 0 } columns)
            {
                var first = columns[0];
                setup["provider"] = first.Provider;
                setup["model"] = first.Model;
                if (first.Account is { } account) setup["account"] = account;
                if (first.Thinking is { } level) thinking = level;
                if (columns.Count > 1)
                    notes.Add($"The Tool Bench runs one model: the first column's ({first.Provider}/{first.Model}) is loaded and {Count(columns.Count - 1, "other model")} {Are(columns.Count - 1)} left out.");
                if (first.Temperature is not null) notes.Add("The Tool Bench sends no temperature: the first column's is left out.");
            }
            else
            {
                setup["provider"] = preset.Provider;
                setup["model"] = preset.Model;
                if (preset.Account is { } account) setup["account"] = account;
            }
        }
        if (preset.Has(PresetParts.Parameters))
        {
            parts.Add(PresetParts.Parameters);
            setup["thinking"] = thinking ?? "off";
            if (preset.Rules is { } rules) setup["rules"] = Node(rules);
            var dropped = (preset.Options?.Entries() ?? []).Select(entry => entry.Name).Where(name => name != "thinking").ToList();
            if (dropped.Count > 0) notes.Add($"The Tool Bench sends thinking only: {string.Join(", ", dropped)} {(dropped.Count == 1 ? "is" : "are")} left out.");
        }
        else if (thinking is not null && preset.Columns is { Count: > 0 })
        {
            parts.Add(PresetParts.Parameters);
            setup["thinking"] = thinking;
        }
        if (preset.Has(PresetParts.Headers)) notes.Add($"The Tool Bench sends no extra headers: the preset's {Count(preset.Headers!.Count, "header")} {(preset.Headers.Count == 1 ? "is" : "are")} left out.");
        if (preset.Has(PresetParts.SystemPrompt))
        {
            parts.Add(PresetParts.SystemPrompt);
            setup["systemPrompt"] = preset.SystemPrompt;
        }
        if (preset.Has(PresetParts.Messages))
        {
            var messages = preset.Messages!;
            var texts = messages.Where(message => message.Role == "user" && !string.IsNullOrWhiteSpace(message.Text)).Select(message => message.Text!).ToList();
            var other = messages.Count - texts.Count;
            if (texts.Count == 0)
            {
                notes.Add("The Tool Bench starts from user messages, and the preset has none with text, so its messages are left out.");
            }
            else
            {
                parts.Add(PresetParts.Messages);
                payload["messages"] = new JsonArray([.. texts.Select(text => (JsonNode)JsonValue.Create(text)!)]);
                if (other > 0) notes.Add($"The Tool Bench starts from user messages: {Count(other, "other message")} (system, assistant or tool result) {(other == 1 ? "is" : "are")} left out.");
                if (messages.Any(message => message.Images is { Count: > 0 })) notes.Add("The Tool Bench sends text only: the messages' images are left out.");
            }
        }
        if (preset.Has(PresetParts.Tools))
        {
            parts.Add(PresetParts.Tools);
            setup["tools"] = Node(preset.Tools ?? []);
            var choice = preset.ToolChoice;
            if (choice is not null) setup["toolChoice"] = choice;
            if (preset.Parallel is { } parallel) setup["parallel"] = parallel;
        }
        return new PresetLoadPlan(PresetPages.Bench, parts, notes, payload);
    }

    // The Playground and Compare run on the provider's active account (they have no account selector); only the Tool Bench takes one.
    private static string AccountNote(string page, string provider) =>
        $"{page} runs on the active {provider} account: the account the preset names is not selected here.";

    private static string Count(int count, string noun) => count == 1 ? "1 " + noun : $"{count} {noun}s";

    private static string Are(int count) => count == 1 ? "is" : "are";

    private static JsonNode Node<T>(T value) => JsonSerializer.SerializeToNode(value, SettingsStore.JsonOptions) ?? new JsonObject();
}
