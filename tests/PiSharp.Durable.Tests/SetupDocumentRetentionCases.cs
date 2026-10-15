// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/test/setup-document-retention.ts. Upstream loads it for every test
// file: PI_DURABLE_DOCUMENT_RETENTION_MS sets the document retention of every Session that keeps the default, so the suite can run with
// each unattached document evicted right after use, and `npm test` runs the suite a second time with it set to 0. Program.Main applies it
// before any case runs; the branch CI runs PiSharp.Durable.Tests a second time with PI_DURABLE_DOCUMENT_RETENTION_MS=0
// (.github/workflows/durable.yml).
using System.Globalization;
using PiSharp.Durable;
using static Program;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> SetupDocumentRetentionCases()
    {
        yield return Case("setup.document-retention.applies-the-forced-default-to-every-session-that-keeps-the-default", () =>
        {
            Equal(SetupDocumentRetention.DefaultForRun, Session.DocumentRetention(null), "default retention");
            Equal(SetupDocumentRetention.DefaultForRun, Agents.ResolveSettings(null).DocumentRetentionMs, "resolved setting");
        });
    }
}

/// <summary>The suite-wide document retention (upstream <c>setup-document-retention.ts</c>).</summary>
internal static class SetupDocumentRetention
{
    /// <summary>The environment variable upstream's setup file reads.</summary>
    public const string Variable = "PI_DURABLE_DOCUMENT_RETENTION_MS";

    /// <summary>The default retention of this run: the forced one, or the ten-minute default.</summary>
    public static double DefaultForRun { get; private set; } = 600_000;

    /// <summary>Applies <see cref="Variable"/>, when set, as the default of every Session.</summary>
    public static void Apply()
    {
        var forced = Environment.GetEnvironmentVariable(Variable);
        if (string.IsNullOrEmpty(forced)) return;
        DefaultForRun = double.Parse(forced, CultureInfo.InvariantCulture);
        Session.SetDefaultDocumentRetention(DefaultForRun);
    }
}
