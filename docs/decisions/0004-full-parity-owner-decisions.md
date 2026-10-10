# 0004: Owner decisions for full Pi v1.1.0 parity

Date: 2026-10-08. Context: [full-parity plan](../plans/full-parity-v1.1.0.md).

## Decisions

1. **Tool gates.** A new `--tool-policy pi|explicit` option (also the `toolPolicy`
   setting). The Pi-compatible entry points (plain `pisharp`, `-p`, `--mode json|rpc`)
   default to `pi`: file tools accept any path and bash runs any command with the full
   environment and the discovered shell, as in Pi. The existing `session …` and `rpc`
   subcommands default to `explicit` and keep their `--allow-*` grants. Session files,
   extension manifests, approvals and snapshots stay protected in both modes. Amended
   2026-10-08: as in Pi, project trust only gates project-local resources
   (`.pi/settings.json`, `SYSTEM.md`, project extensions, `.pi/mcp.json`); tools stay on the
   `pi` policy in untrusted projects. Owner: IMPL-F.
2. **Image codec.** SkiaSharp (MIT) with its per-platform native assets, pinned exactly,
   for `read` image decoding, resizing and JPEG re-encoding as Pi's Photon does. It is
   PiSharp's second external runtime dependency besides YamlDotNet (Jint is the third, for
   codemode). Owner: IMPL-D.
3. **Follow Pi on the remaining defaults:**
   - `!command` config values run, as Pi resolves them (including stored Anthropic keys).
     Owner: IMPL-A2.
   - Interactive startup refreshes the model catalog from pi.dev unless `PI_OFFLINE` is
     set. Owner: IMPL-I.
   - Vertex uses Google Application Default Credentials, as Pi does. Owner: IMPL-A1.
   - Trusted extensions call classifier and image models with stored credentials and may
     pass their own model objects, as Pi allows. Owner: IMPL-B.
   - Request and image budgets follow Pi (about 4.5 MB images) instead of the 1 MiB
     transport cap. Owner: IMPL-D with IMPL-A1.

Earlier decisions stand: MCP servers in `mcp.json` are trusted and their tools callable
(1.1.0.1); unknown tool names are ignored as in Pi (1.1.0).

## Later decisions (2026-10-08)

4. **Untrusted projects** keep the `pi` tool policy (see the amendment to 1).
5. **ripgrep and fd** are used from `PATH` or `<agentDir>/bin`, and downloaded from their
   release pages on first use when missing, as Pi's tools manager does (honouring
   `PI_OFFLINE`). Owner: IMPL-F.
6. **Stored `!command` keys for every provider and AWS `credential_process`** run as Pi
   does (confirmed separately because an agent's permission check had blocked the change).
7. **Codemode deadline** starts when the script starts running in its worker, not when the
   worker launches; Pi's worker starts in milliseconds, so the user-visible budget matches.
8. **Extensions run as in Pi:** discovered TypeScript/JavaScript extensions run in the
   user's Node with the user's privileges, gated only by project trust; `pisharp install`
   and startup package resolution use the user's npm and git.
9. **Pi's own Node packages** (the exact Pi 1.1.0 releases, pinned, verified against the
   npm registry integrity) are installed into the agent directory on first extension use
   and extensions load through Pi's jiti setup; the ported shims are only the offline
   fallback. Owner: IMPL-E.
10. **Native C# extensions in the Pi entry** are discovered like Pi extensions (global and
    project extension folders, settings, packages, `-e`), gated only by project trust,
    on every platform; the per-package approval and preflight files are not required
    there. Owner: IMPL-E.
11. **`/bug`** keeps Pi's local report, zip and summary but does not upload to Pi's
    developers; it opens a prefilled issue at github.com/jkelly/PiSharp/issues (or prints
    the report path to attach).
12. **Version check** looks up the latest `PiSharp.Cli` on NuGet and suggests
    `dotnet tool update -g PiSharp.Cli`, honouring `PI_OFFLINE` and the setting that
    disables it; it never asks pi.dev for Pi's version.
13. **Nameless tool calls** (Google streams them) are accepted exactly as far as Pi accepts
    them; the shared reducer check is relaxed to match, for every provider.

## Later decisions (2026-10-10)

14. **Azure resource name** (`AZURE_OPENAI_RESOURCE_NAME` or `azureResourceName`) is taken as
    Pi takes it, for both the Chat Completions and Responses routes: any non-empty value goes
    into `https://{resource}.openai.azure.com/openai/v1`, and that text is parsed as Node's
    WHATWG `URL` parses it before Pi's Azure path normalization. Dots, upper case, underscores,
    IDN names, backslashes, tabs, percent-escapes and slashes resolve exactly as in Pi; an empty
    value falls through to the model's base URL. PiSharp previously accepted only one ASCII DNS
    label. Names Node's `URL` refuses fail with Pi's "Invalid Azure OpenAI base URL" message
    (without echoing the value). Three outcomes are refused up front where Pi fails only on the
    request: a URL that ends up with credentials, a fragment or a query in front of the request
    path (the same rule as for `AZURE_OPENAI_BASE_URL`), and a host `System.Uri` cannot hold
    (empty labels or sub-delimiters such as `$`, `+`, `~`, `=`), which no resolver answers.
    Tests: `PiSharp.ProviderSync.Tests` (Pi's outputs for each name, under Node 22) and
    `PiSharp.AzureResponses.Tests`.

Install telemetry (`core/telemetry.ts`, which reports installs to Pi's servers) is not
ported: PiSharp is not Pi and must not report as it.
