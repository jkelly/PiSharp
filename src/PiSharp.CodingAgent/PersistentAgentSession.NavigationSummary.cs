using System.Collections.Immutable;
using PiSharp.Sessions.Compaction;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Serialization;

namespace PiSharp.CodingAgent;

public sealed partial class PersistentAgentSession
{
    private async Task<ImmutableArray<SessionEntry>> PrepareTreeRecordsAsync(SessionTreeNavigationRevision revision,
        SessionTreeNavigationPreview preview, SessionTreeNavigationOptions options, SessionProvidedSummary? provided,
        SessionTreeNavigationExecution? execution, CancellationToken token, SessionBoundaryOriginals originals)
    {
        if (options.CustomInstructions?.Length > 65_536 || options.Label?.Length > 65_536)
            throw new ArgumentException("Tree option bound exceeded.");
        SessionProvidedSummary? summary = null;
        if (provided is not null && provided.Text is null) throw new ArgumentException("Provided summary text required.");
        if (options.Summarize)
        {
            summary = provided;
            if (summary is null && !preview.AbandonedEntries.IsEmpty)
            {
                var admitted = execution ?? throw new NotSupportedException("No admitted tree summary owner.");
                if(admitted.CaptureSummaryBudget?.GetInvocationList().Length>1)throw new ArgumentException("One actual summary-budget capture required.");
                var budget=admitted.CaptureSummaryBudget is { } capture?capture():new SessionTreeSummaryBudget(admitted.ContextWindow,admitted.ReserveTokens);
                if (budget is null || !double.IsFinite(budget.ContextWindow) || budget.ContextWindow <= 0 ||
                    !double.IsFinite(budget.ReserveTokens) || budget.ReserveTokens < 0)
                    throw new ArgumentException("Invalid tree summary settings.");
                var generator = admitted.Generator ?? throw new NotSupportedException("No admitted tree summary generator.");
                var plan = new SessionBranchSummaryPlanner().Prepare(preview.AbandonedEntries,
                    budget.ContextWindow - budget.ReserveTokens, preview.CommonAncestorId, token);
                if (!plan.Messages.IsEmpty)
                {
                    var request = SessionSummaryRequestBuilder.Branch(plan, revision.Configuration.Model,
                        revision.Log.Header.Id, new(CustomInstructions: options.CustomInstructions, ReplaceBranchInstructions: options.ReplaceInstructions));
                    var actual = generator.GenerateAsync(request, token).AsTask();
                    var generated = await originals.Join(actual, "tree-summary-generator").ConfigureAwait(false);
                    ValidateGenerated(generated);
                    var (read, modified) = plan.FileOps.ComputeFileLists();
                    summary = new(SessionSummaryRequestBuilder.BranchPreamble + generated.Text + SessionFileOperations.FormatFileOperations(read, modified),
                        generated.Usage, PiSharp.Contracts.JsonData.Parse(System.Text.Json.JsonSerializer.Serialize(new { readFiles = read, modifiedFiles = modified })));
                }
            }
        }
        var entries = ImmutableArray.CreateBuilder<SessionEntry>();
        string? selected = preview.NewLeafId;
        if (summary is { Text.Length: > 0 })
        {
            var id = Identity(_nextEntryId, revision.Log.Header.Id, revision.Log.Entries);
            var record = SummaryRecord(false, id, selected, null, 0, summary.Text, summary.Usage,
                summary.Details, provided is not null, revision.Context, _clock);
            entries.Add(record); selected = record.Id;
        }
        if (!string.IsNullOrEmpty(options.Label))
        {
            var target = entries.Count == 0 ? preview.TargetId : entries[0].Id;
            if (target is null) throw new ArgumentException("A root without summary cannot be labelled.");
            var id = Identity(_nextEntryId, revision.Log.Header.Id, revision.Log.Entries.AddRange(entries));
            entries.Add(Record(_codec, "label", id, selected, _clock, writer =>
            { writer.WriteString("targetId", target); writer.WriteString("label", options.Label); }));
        }
        return entries.ToImmutable();
    }
}
