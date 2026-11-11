# C-PUB implementation seam

Owner: `/root/chord_publication`; source `src/PiSharp.Chord.Hosting/Surfaces/*`. No builds or executions performed by this owner; root serializes verification.

## Compiler and input boundary

`ISurfaceBuilder.BuildAsync(SurfaceBuildRequest request, CancellationToken cancellationToken = default) -> Task<SurfaceBuildArtifact>`.

`SurfaceBuildRequest(string Kind, string Entry, string Prefix, string? Plugin = null)`. A base request has no plugin. A plugin artifact contains a CommonJS factory function expression. `SurfaceBuildArtifact(string JavaScript, BaseInputs Inputs, string? Css = null, IReadOnlyList<SurfaceAsset>? Assets = null)` carries actual compiler-read input hashes. `SurfaceAsset(string Name, ReadOnlyMemory<byte> Bytes, string? Mime = null)` uses the URL returned by `SurfacePaths.Asset(prefix,name,bytes)`. Artifacts are copied on publication; native DLLs are never substituted for JavaScript.

## Runtime and HTTP boundary

`SurfaceBuild(SurfaceBuildOptions options, CancellationToken cancellationToken = default)` is `IAsyncDisposable`. Options require `Builder` and `Base`; optional callbacks are `Plugins: Func<CancellationToken,Task<IReadOnlyList<SurfacePluginBuild>>>`, `Publish: Func<ScriptSet,CancellationToken,Task>` and `WatchBase: Func<BaseInputs,IDisposable>`.

`BuildAsync(CancellationToken = default) -> Task<SurfaceBuilt>` shares one cached build; only the lifetime token cancels the compiler. `SurfaceBuilt` has `Success`, aggregate `Base` hash, `Inputs`, `Error`. `Set` is the current portable `ScriptSet`; `BaseScript` and `BaseStyle` contain page-resource URLs. `Built` remains null until completion. `Contribution` supplies `GET <prefix>*`, public, and `ScriptPaths = [prefix]`. `GetFileAsync(path, token)` yields 503 after base failure, 404 for an absent artifact after success, otherwise actual bytes with the immutable private cache header.

`ReloadAsync(IReadOnlyList<SurfacePluginBuild>, CancellationToken = default)` publishes a complete set and invokes `Publish`. `SurfacePluginBuild(string Name, SurfaceBuildArtifact? Artifact = null, Exception? Error = null)` requires exactly one result. Absent names are removed; failed names retain prior JS, CSS and assets. `PublishedScripts.Publish` performs atomic validation and commits name-ordered sets, retaining the latest four generations. Factory digest is distinct from the served wrapper hash. Every served path hashes its actual bytes; base set identity covers JS/CSS/assets.

## Host state boundary

`SurfaceName.Web` / `.Tui`; `SurfaceScripts.CreateFacet(SurfaceName, Func<ScriptSet>)` serves `chord.<surface>.current` and portable `Scripts.WebScripts` / `TuiScripts`. `SurfaceScripts.PublishAsync(ChordHost, SurfaceName, ScriptSet, ChordContext)` updates the current object. Configure the build callback with this method, after loading the facet. The current-value getter permits a recovered attempt to start at the latest published set.

## Verification registration

New `tests/PiSharp.Chord.Hosting.Tests/SurfacePublicationTests.cs` static methods: `Retention`, `BuildOnce`, `FailureHttpIsolation`, `HttpArtifact`, `ScriptsRoundTrip`. Root owns registration in `Program.cs` and execution. Browser DOM/React hooks, CSS preload/adoption, and secure terminal downloaded-code execution remain explicit platform-adapter requirements, documented in the new Surfaces README; no native stand-ins are supplied.
