using System.Text.Json;

internal static class NativeSessionFixturePackage
{
    internal const string AssemblyFile = "PublishedFixture.SessionLifecycle.dll";
    private static string Output => Path.GetDirectoryName(typeof(NativeToolNamespaceFixture.Entry).Assembly.Location)
        ?? throw new InvalidOperationException("Native session fixture output is unavailable.");
    private static readonly string[] Files = [AssemblyFile, "PublishedFixture.SessionLifecycle.deps.json", "PublishedFixture.SessionLifecycle.runtimeconfig.json"];

    internal static void VerifyOutput()
    {
        foreach (var name in Files)
            if (!File.Exists(Path.Combine(Output, name))) throw new InvalidOperationException("Missing isolated native fixture artifact: " + name);
        using var deps = JsonDocument.Parse(File.ReadAllText(Path.Combine(Output, Files[1])));
        var libraries = deps.RootElement.GetProperty("libraries");
        var foundEntry = false;
        foreach (var library in libraries.EnumerateObject())
        {
            var name = library.Name.Split('/')[0];
            if (name is not ("PublishedFixture.SessionLifecycle" or "PiSharp.Contracts" or "PiSharp.Extensions.Abstractions") ||
                library.Value.GetProperty("type").GetString() != "project")
                throw new InvalidOperationException("Native session fixture acquired a host or package dependency: " + library.Name);
            foundEntry |= name == "PublishedFixture.SessionLifecycle";
        }
        if (!foundEntry || typeof(NativeShutdownPlacementFixture.Entry).Assembly != typeof(NativeToolNamespaceFixture.Entry).Assembly)
            throw new InvalidOperationException("Native session fixture entry assembly differs.");
    }

    internal static void CopyTo(string directory)
    {
        VerifyOutput();
        foreach (var name in Files) File.Copy(Path.Combine(Output, name), Path.Combine(directory, name), overwrite: false);
    }
}
