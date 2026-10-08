using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Contracts;
using PiSharp.Sessions.Context;
using PiSharp.Agent;
using PiSharp.Cli.Skills;
using PiSharp.CodingAgent;
using PiSharp.CodingAgent.Resources;
using PiSharp.CodingAgent.Resources.Skills;

namespace PiSharp.Cli.Commands;

internal sealed partial class OfflineSessionProfile
{
    private SkillCliBinding? _skills;
    private Func<SkillDiagnostic, CancellationToken, ValueTask>? _skillDiagnostic;
    internal async Task LoadSkillsAsync(SkillCliConfiguration configuration, TextWriter diagnostics, CancellationToken token)
    {
        if (configuration.Selections.IsEmpty) return;
        RequireStartupViewMutable();
        if (_skills is not null) throw new InvalidOperationException("Skills are already captured.");
        var capture = await SkillCliBinding.LoadAsync(configuration, token: token).ConfigureAwait(false);
        async ValueTask Report(SkillDiagnostic diagnostic, CancellationToken cancellationToken)
        {
            await diagnostics.WriteLineAsync(JsonSerializer.Serialize(new { type = "skill_diagnostic",
                severity = diagnostic.Type, code = diagnostic.Code, path = diagnostic.Path,
                name = diagnostic.Name, winnerPath = diagnostic.WinnerPath }).AsMemory(), cancellationToken).ConfigureAwait(false);
            await diagnostics.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        foreach (var diagnostic in capture.Resources.Diagnostics) await Report(diagnostic, token).ConfigureAwait(false);
        InitialSystem = WithSkillSection(InitialSystem, capture.Resources.FormatForPrompt());
        _skillDiagnostic = Report;
        _skills = capture;
    }
    /// <summary>Skills a Pi-style entry already placed in the original system prompt (its <c>skills</c> section): the binding serves
    /// <c>/skill:name</c> input only, and no native skills section is written.</summary>
    private bool _skillsInOriginalPrompt;
    internal void AdoptOriginalPromptSkills(SkillCliBinding capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        RequireStartupViewMutable();
        if (_skills is not null) throw new InvalidOperationException("Skills are already captured.");
        _skillsInOriginalPrompt = true; _skillDiagnostic = static (_, _) => ValueTask.CompletedTask;
        _skills = capture;
    }
    internal Task ApplySkillsAsync(PersistentAgentSession session, CancellationToken token) =>
        ApplySkillsFromViewAsync(session, CaptureRuntimeView(), token);
    private async Task ApplySkillsFromViewAsync(PersistentAgentSession session, ProfileRuntimeView view, CancellationToken token)
    {
        if (_skillsInOriginalPrompt) return;
        using var use = view.Lifetime.Enter();
        var catalog = view.Skills?.Resources.FormatForPrompt();
        var effective = new SessionSystemReplay().Replay(session.Snapshot.Agent.Messages).CurrentMessage;
        var current = effective?.WireBody.Value;
        var existing = current is { } body && body.TryGetProperty("sections", out var sections) &&
            sections.TryGetProperty(SkillSection, out var section) ? section.GetString() : null;
        if (string.Equals(existing, catalog, StringComparison.Ordinal)) return;
        // Append only the owned section update. Historical content and declarations replay unchanged.
        var update = WithSkillSection(JsonData.Parse("{\"role\":\"system\",\"content\":\"\",\"timestamp\":0}"), catalog);
        await session.ConfigureAsync(new(SystemMessage: new("system", update)), token).ConfigureAwait(false);
    }
    private const string SkillSection = "pisharp.skills";
    private static JsonData WithSkillSection(JsonData message, string? catalog)
    {
        var body = JsonNode.Parse(message.ToString())!.AsObject();
        var sections = body["sections"] as JsonObject;
        if (sections is null) { sections = new JsonObject(); body["sections"] = sections; }
        sections[SkillSection] = catalog is null ? null : JsonValue.Create(catalog);

        return JsonData.Parse(body.ToJsonString());
    }
}
