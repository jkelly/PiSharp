// The native extension cases run packages that tools/publish-native-extension-fixtures.ps1 (or the native
// gate's preparation, tools/prepare-native-products.ps1) publishes under artifacts/extensions/published-fixtures.
// A run with no published fixtures at all skips those cases with this reason. Once the directory exists, every
// fixture a case needs must be complete, so a prepared run cannot skip a case.
internal static class PublishedFixtures
{
    internal static void Require(string published, string assembly)
    {
        if (File.Exists(Path.Combine(published, assembly))) return;
        var root = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(published))!;
        if (!Directory.Exists(root))
            throw new TestSkippedException("needs the published native extension fixtures; run tools/publish-native-extension-fixtures.ps1 first.");
        throw new InvalidOperationException($"The published fixture '{Path.GetFileName(published)}' has no {assembly}; " +
            "run tools/publish-native-extension-fixtures.ps1 again.");
    }
}

internal sealed class TestSkippedException(string reason) : Exception(reason);
