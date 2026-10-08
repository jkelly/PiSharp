# Selected companion receipt corrections

Base: `f4dbf4b8b21a8efd8ca4a53376426e355e1c453b`. Source pin remains Pi v0.99.1, `d86654abb8862e201933517d6f1fce9f88dd117f`.

The coordinator's `NATIVE_SELECTED_COMPANION_OUTCOMES_f4dbf4b8.json` and original Google/Pi Messages failed companion artifacts are retained unchanged. This patch authors corrections; it does not establish a passing native receipt or close source qualification gates. No build, native test, SDK oracle, or API call was executed by this worker.

## Google fixture corrections

The output cost expectation is `0.000030000000000000004m`, preserving the observed binary64 projection. Production arithmetic is unchanged. The preterminal diagnostic fixture compact-serializes its JSON into one SSE `data:` line, so pretty JSON newlines do not accidentally produce malformed SSE before the intended resource-limit observation. Existing cancellation, cleanup, terminal authority, and classification expectations remain in place.

## Pi Messages header observation layers

The harness now records both headers before body materialization and the complete native headers after `ReadAsByteArrayAsync`. The frozen source header dictionary is compared unchanged against the first layer. The second layer receives an exact `content-length` expectation derived from the frozen `bodyUtf8Base64` bytes, only when the frozen dictionary does not already specify it. Every header remains subject to full dictionary comparison; arbitrary headers are never discarded. Whole body byte equality remains required. No frozen case or dependency fixture is edited.

## Pi Messages debug URL candidate

For a fragment-free endpoint, the factory first admits and normalizes the URL with ordinary `Uri`, runs the existing form decoder/encoder, and constructs an exact query representation from that validated prefix plus encoder output. It checks the expanded URL against the existing byte budget and revalidates scheme, authority, userinfo, and fragment before enabling `DangerousDisablePathAndQueryCanonicalization`. This preserves the frozen expectation `q=a+b%7E%2B` rather than accepting `q=a+b~%2B`.

The restricted representation uses the [.NET URI option](https://learn.microsoft.com/en-us/dotnet/api/system.uricreationoptions.dangerousdisablepathandquerycanonicalization?view=net-10.0); the [.NET 10 implementation](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.Uri/src/System/Uri.cs) treats fragments differently when canonicalization is disabled. Therefore fragment-bearing URLs retain ordinary `UriBuilder` handling. Their fragment must never become part of the HTTP request query.

Three additional authored native control groups cover exact escaping through factory and owned request (six vectors), ordinary fragment separation, and bounded URL admission. They run separately from the frozen case counts and remain unexecuted after an unjoined owner. They confer no source differential acceptance.

Open criterion: source-exact query escapes for fragment-bearing URLs (for example a tilde in the query plus `#frag`) still require a representation that preserves both escaped query bytes and fragment separation. This patch does not waive that criterion. Coordinator execution must also qualify fragment-free `AbsoluteUri` and `PathAndQuery`, retained frozen URI vectors, header observation layers, and actual transport request-target behavior before declaring parity. All original phase and source qualification gates remain open.
