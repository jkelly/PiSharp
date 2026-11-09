namespace PiSharp.Chord.Legacy;

public sealed partial class LegacyHost
{
    private sealed record Retirement(LegacyGeneration Generation, TaskCompletionSource Completion);
    private readonly SortedDictionary<int, Retirement> Retirements = [];

    // Logical retirement denies new calls immediately. Physical retirement retains the old module until every
    // admitted asynchronous effect and old native Close has actually settled, including after a bounded close.
    internal void TrackWork(int codeVersion, Task work)
    {
        var generation = Generations.GetValueOrDefault(codeVersion) ?? Retirements.GetValueOrDefault(codeVersion)?.Generation;
        if (generation is null || work.IsCompleted) return;
        generation.Work.Add(work);
        _ = work.ContinueWith(_ => Fire(() => { generation.Work.Remove(work); return Task.CompletedTask; }), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    internal void RetireGeneration(LegacyGeneration generation)
    {
        if (!Generations.Remove(generation.Id) || Retirements.ContainsKey(generation.Id)) return;
        var retirement = new Retirement(generation, new(TaskCreationOptions.RunContinuationsAsynchronously));
        Retirements.Add(generation.Id, retirement);
        try { generation.Abort.Cancel(); } catch (Exception error) { Report(error); }
        var oldRuns = new List<(LegacyRun Run, LegacyRun? Kept)>();
        foreach (var owner in Objects.Values.ToArray())
            if (owner.Runs.Remove(generation.Id, out var run) && !owner.Runs.Values.Contains(run)) oldRuns.Add((run, owner.Current));
        Fire(async () =>
        {
            try
            {
                await DrainWorkAsync(generation);
                foreach (var (run, kept) in oldRuns) await DisposeUninstalled(run, kept);
                // Releasing old dependencies can synchronously start their own disposal.
                await DrainWorkAsync(generation);
                generation.Services.Clear(); generation.Tables.Clear(); generation.Abort.Dispose();
            }
            catch (Exception error)
            {
                // A failed cleanup protocol is not evidence that module code stopped executing. Keep ownership;
                // subsequent reload/close waits remain bounded and report the unfinished retirement.
                Report(error); return;
            }
            retirement.Completion.TrySetResult(); PublishRetirements();
        });
    }

    private static async Task DrainWorkAsync(LegacyGeneration generation)
    {
        while (true)
        {
            generation.Work.RemoveWhere(work => work.IsCompleted);
            if (generation.Work.Count == 0) return;
            try { await Task.WhenAll(generation.Work.ToArray()); } catch (Exception) { /* Settlement, including failure, releases code ownership. */ }
        }
    }

    internal async Task WaitForRetirementCleanupAsync(TimeSpan deadline, CancellationToken cancellationToken)
    {
        if (Retirements.Count == 0) return;
        try { await Task.WhenAll(Retirements.Values.Select(retirement => retirement.Completion.Task)).WaitAsync(deadline, cancellationToken); }
        catch (TimeoutException error)
        { throw new ChordException("upgrade_conflict", "old code cleanup has not completed before the retirement deadline", "retirement_cleanup", cause: error); }
    }

    private void PublishRetirements()
    {
        // A later cleanup may finish first. Keep the public watermark honest for cluster catch-up and loaders.
        while (Retirements.Count > 0)
        {
            var first = Retirements.First();
            if (!first.Value.Completion.Task.IsCompleted) return;
            Retirements.Remove(first.Key); HighestRetiredCodeVersion = Math.Max(HighestRetiredCodeVersion, first.Key);
            foreach (var handler in Retired?.GetInvocationList().Cast<Action<int>>() ?? [])
                try { handler(first.Key); } catch (Exception error) { Report(error); }
        }
    }
}
