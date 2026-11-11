# Surface publication

`ISurfaceBuilder` is an explicit compiler adapter. It returns actual JavaScript, optional CSS, binary assets and `BaseInputs` hashes of the bytes the compiler read. Base JavaScript is executable browser code; plugin JavaScript is one CommonJS factory function expression, published through `chordFacet(name,digest,factory)`. A native DLL is never interpreted as JavaScript.

`SurfaceBuild` shares one build between startup and requests. A waiter's cancellation does not cancel that build. Its cached failure returns HTTP 503 on its own routes; other server routes continue. Supply the server lifetime token to cancel outstanding compiler work on shutdown. Set `WatchBase = plugins.WatchBase` to track the actual compiler inputs, and `Publish` to call `SurfaceScripts.PublishAsync` after the server loads its scripts facet.

`PublishedScripts.Publish` accepts a complete plugin set. An error retains that plugin's previous script, stylesheet and assets; omitted plugins are removed. It retains files from the latest four publications, returns defensive byte copies, and derives immutable URLs from SHA-256 content. Asset imports use `SurfacePaths.Asset(prefix,name,bytes)` so compiler output and publication agree on URLs. MIME declarations for identical asset content must remain stable. Every artifact is served with private immutable caching and a public HTTP route; the prefix alone is contributed to `ScriptPaths` for CSP.

`SurfaceScripts.CreateFacet` exposes the current set through the native `WebScripts` or `TuiScripts` contract. Publication callbacks run without waiting for clients. `SurfaceBuild.BaseScript` and `BaseStyle` are page resource URLs; `ScriptSet.Base` is the aggregate base artifact hash.

Browser DOM and React hooks require an actual JavaScript/React adapter. This package supplies neither browser execution nor fake native React hooks. Native ALC loading is not a security sandbox. Secure downloaded terminal plugin execution, Node permission isolation, terminal launcher and browser CSS preload/adoption are unsupported until such an adapter is installed and independently tested.
