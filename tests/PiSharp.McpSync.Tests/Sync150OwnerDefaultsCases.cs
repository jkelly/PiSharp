using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Mcp.Authentication;
using PiSharp.Extensions.Mcp.Authentication;
using PiSharp.Extensions.Mcp.Configuration;
using PiSharp.Extensions.Runtime.Mcp.Authentication;

// Pi 1.5.0 sync, mcp.oauth-client-registration-auto (f9a26365568127461c47a07a37dcb42c140a3ac5, upstream commit 908aa7b74):
// packages/coding-agent/src/extensions/mcp/oauth.ts (offersClientMetadataDocument, clientMetadataDocument, createProvider and
// signInMcpServer's keepClient), core/mcp-servers.ts (validateOAuth, isClientMetadataDocumentCallback) and extensions/mcp/cli.ts
// (add --oauth-client-registration). The cases follow upstream's mcp-oauth-refresh.test.ts, mcp-extension.test.ts and
// mcp-command.test.ts; their expectations are authored from reading the v1.5.0 sources, never captured from an upstream run.
// PiSharp is treated as a rebranded build (owner decision 2026-11-05, "Use safe defaults"): upstream's `auto` presents pi's Client ID
// Metadata Document on pi.dev unless APP_NAME is not "pi", and PiSharp's APP_NAME is "pi", so PiSharp's `auto` applies the rebrand
// guard itself and always registers. An explicit `cimd` presents pi.dev's document, as upstream's does in a rebranded build.
// Fake authorization servers answer in process; the loopback callback binds 127.0.0.1 and the redirect URL is pasted.
internal static partial class Program
{
    private static IEnumerable<(string Id, Func<Task> Run)> Sync150OwnerDefaultsCases() =>
    [
        ("sync150.mcp-oauth-client-registration-auto-validates", ClientRegistrationAutoValidates),
        ("sync150.mcp-oauth-auto-registers-and-never-presents-pi-dev", AutoRegistersWithoutPiDocument),
        ("sync150.mcp-oauth-auto-keeps-a-registered-client", AutoKeepsRegisteredClient),
        ("sync150.mcp-oauth-explicit-cimd-presents-pi-dev-and-replaces-a-registered-client", ExplicitCimdPresentsPiDocument),
        ("sync150.mcp-add-oauth-client-registration", McpAddClientRegistration),
    ];

    // mcp-servers.ts validateOAuth: "auto" (the new default) is accepted, also with clientName; any other value names all three modes.
    // offersClientMetadataDocument: only an explicit "cimd" offers the document in PiSharp.
    private static Task ClientRegistrationAutoValidates()
    {
        var loaded = McpConfigurationReader.Load(new("global.json", """
            {"mcpServers":{
            "auto":{"url":"https://a.example/mcp","oauth":{"clientRegistration":"auto"}},
            "autoNamed":{"url":"https://a.example/mcp","oauth":{"clientRegistration":"auto","clientName":"x","clientId":"y","callbackUrl":"http://[::1]/cb"}},
            "dcr":{"url":"https://a.example/mcp","oauth":{"clientRegistration":"dcr"}},
            "cimd":{"url":"https://a.example/mcp","oauth":{"clientRegistration":"cimd"}},
            "oidc":{"url":"https://a.example/mcp","oauth":{"clientRegistration":"oidc"}},
            "nullMode":{"url":"https://a.example/mcp","oauth":{"clientRegistration":null}}}}
            """), null, false);
        Equal("auto,autoNamed,dcr,cimd", string.Join(',', loaded.Servers.Select(server => server.Name)));
        Equal(string.Join('|', [
            "global.json: server \"oidc\": oauth.clientRegistration must be \"auto\", \"dcr\", or \"cimd\"",
            "global.json: server \"nullMode\": oauth.clientRegistration must be \"auto\", \"dcr\", or \"cimd\""]), string.Join('|', loaded.Errors));
        // Upstream offers the document under auto unless clientId, clientName, a non-document callback or a rebranded APP_NAME; PiSharp
        // never does under auto, however the rest is configured.
        Check(!McpSignIn.OffersClientMetadataDocument(new()), "the default never offers the document");
        Check(!McpSignIn.OffersClientMetadataDocument(new(ClientRegistration: "auto", CallbackUrl: "http://127.0.0.1/callback")), "auto never offers the document");
        Check(!McpSignIn.OffersClientMetadataDocument(new(ClientRegistration: "dcr")), "dcr never offers the document");
        Check(McpSignIn.OffersClientMetadataDocument(new(ClientRegistration: "cimd")), "cimd offers the document");
        Equal(new Uri("https://pi.dev/oauth"), McpSignIn.ClientMetadataBaseUrl);
        return Task.CompletedTask;
    }

    // #11426 upstream: "uses pi's document by default when authorization responses name their issuer". PiSharp's default registers a
    // client dynamically instead, and nothing it sends names pi.dev.
    private static async Task AutoRegistersWithoutPiDocument()
    {
        foreach (var settings in new McpOAuthSettings[] { new(), new(ClientRegistration: "auto"), new(ClientRegistration: "dcr") })
        {
            var server = new SignInServer(); var store = server.Store();
            await server.SignIn(store, settings);
            Equal(1, server.Registrations.Count);
            Equal("client-1", Query(server.Authorizations.Single(), "client_id"));
            Equal("client-1", (await store.LoadAsync())!.ClientInformation!.Value.GetProperty("client_id").GetString());
            Check(server.Http.Log.All(row => !row.Uri.AbsoluteUri.Contains("pi.dev", StringComparison.Ordinal) && !row.Body.Contains("pi.dev", StringComparison.Ordinal)) &&
                server.Authorizations.All(url => !url.AbsoluteUri.Contains("pi.dev", StringComparison.Ordinal)), "nothing names pi.dev");
        }
    }

    // #11426 upstream: "keeps a registered client by default". A client registered with dcr stays in use under auto while its redirect
    // URI matches: no second registration and no second browser authorization.
    private static async Task AutoKeepsRegisteredClient()
    {
        var server = new SignInServer(); var store = server.Store();
        await server.SignIn(store, new(ClientRegistration: "dcr"));
        await server.SignIn(store, new());
        Equal(1, server.Registrations.Count);
        Equal(1, server.Authorizations.Count);
        Check(server.TokenRequests.All(body => Form(body, "client_id") == "client-1"), "every token request uses the registered client");
        Equal("client-1", (await store.LoadAsync())!.ClientInformation!.Value.GetProperty("client_id").GetString());
    }

    // An explicit cimd keeps PiSharp's opt-in behaviour, which upstream 1.5.0 also keeps for a rebranded build: pi's document on pi.dev,
    // no registration, nothing stored for the client, and a registered client replaced. Going back to auto discards the document's
    // tokens, since PiSharp's auto never offers the document (upstream keeps them when it would offer it).
    private static async Task ExplicitCimdPresentsPiDocument()
    {
        var server = new SignInServer(); var store = server.Store();
        await server.SignIn(store, new(ClientRegistration: "dcr"));
        Equal(1, server.Registrations.Count);
        await server.SignIn(store, new(ClientRegistration: "cimd"));
        Equal(1, server.Registrations.Count);
        Equal(2, server.Authorizations.Count);
        Equal("https://pi.dev/oauth/client.json", Query(server.Authorizations[1], "client_id"));
        Equal("https://pi.dev/oauth/client.json", Form(server.TokenRequests[^1], "client_id"));
        Equal(null, (await store.LoadAsync())!.ClientInformation);
        await server.SignIn(store, new());
        Equal(2, server.Registrations.Count);
        Equal(3, server.Authorizations.Count);
        Equal("client-2", Query(server.Authorizations[2], "client_id"));
        // Without documents for public clients, cimd fails before the browser opens; auto would have registered.
        var refusing = new SignInServer(documents: false);
        var failure = await Failure(refusing.SignIn(refusing.Store(), new(ClientRegistration: "cimd")));
        Check(Find<McpOAuthProtocolException>(failure).Message.Contains("remove oauth.clientRegistration \"cimd\"", StringComparison.Ordinal), failure.ToString());
        Equal(0, refusing.Authorizations.Count);
    }

    // cli.ts add: --oauth-client-registration writes oauth.clientRegistration for an HTTP server; an invalid mode fails validation and
    // nothing is written; the help lists the option.
    private static async Task McpAddClientRegistration()
    {
        var (root, options) = CliFixture();
        try
        {
            var global = Path.Combine(root, "agent", "mcp.json");
            Check(McpCommand.Help.Contains("  --oauth-client-name <name>\n                          Client name sent when registering with the OAuth server\n" +
                "  --oauth-client-registration <mode>\n                          auto (default), cimd, or dcr\n", StringComparison.Ordinal), "help");
            Equal((0, $"Added global MCP server \"linear\" in {global}.\nCheck it with: pisharp mcp list. If it requires sign-in: pisharp mcp login linear\n", ""),
                await Mcp(options, "add", "linear", "--url", "https://mcp.linear.app/mcp", "--oauth-client-registration", "dcr"));
            var linear = JsonNode.Parse(File.ReadAllText(global))!["mcpServers"]!["linear"]!;
            Equal("""{"url":"https://mcp.linear.app/mcp","oauth":{"clientRegistration":"dcr"}}""", linear.ToJsonString());
            var before = File.ReadAllText(global);
            Equal((1, "", "server \"x\": oauth.clientRegistration must be \"auto\", \"dcr\", or \"cimd\"\n"),
                await Mcp(options, "add", "x", "--url", "https://example.com", "--oauth-client-registration", "maybe"));
            Equal((1, "", "--oauth-client-registration only applies to HTTP servers (--url).\n"),
                await Mcp(options, "add", "x", "--oauth-client-registration", "auto", "--", "cmd"));
            Equal(before, File.ReadAllText(global));
        }
        finally { Delete(root); }
    }

    /// <summary>An authorization server for whole sign-ins (McpSignIn.SignInAsync): discovery, dynamic registration (client-1, client-2,
    /// ... echoing the redirect URIs) and a token endpoint. The browser step is the pasted redirect URL with a code and the issuer.</summary>
    private sealed class SignInServer
    {
        internal readonly FakeServer Http = new();
        internal readonly List<string> Registrations = [];
        internal readonly List<string> TokenRequests = [];
        internal readonly List<Uri> Authorizations = [];
        private readonly bool documents;
        internal SignInServer(bool documents = true)
        {
            this.documents = documents;
            Http.Respond = (request, body, _) => Task.FromResult(request.RequestUri!.AbsolutePath switch
            {
                "/.well-known/oauth-protected-resource/mcp" => Response(200, "{\"resource\":\"" + Server.AbsoluteUri + "\",\"authorization_servers\":[\"" + Issuer + "\"]}"),
                "/.well-known/oauth-authorization-server" => Response(200, Metadata(true, documents)),
                "/register" => Register(body),
                "/token" => Token(body),
                _ => Response(404, "{}")
            });
        }
        internal IMcpAdmittedOAuthStateStore Store() => new McpOAuthCredentialStore(new MemoryBackend()).ForServer("docs", Server);
        private HttpResponseMessage Register(string body)
        {
            lock (Registrations) Registrations.Add(body);
            var uris = JsonNode.Parse(body)!["redirect_uris"]!.ToJsonString();
            return Response(201, "{\"client_id\":\"client-" + Registrations.Count + "\",\"redirect_uris\":" + uris + "}");
        }
        private HttpResponseMessage Token(string body)
        {
            lock (TokenRequests) TokenRequests.Add(body);
            return Response(200, "{\"access_token\":\"access-" + TokenRequests.Count + "\",\"token_type\":\"Bearer\",\"refresh_token\":\"refresh-" + TokenRequests.Count + "\",\"expires_in\":3600}");
        }
        internal async Task SignIn(IMcpAdmittedOAuthStateStore store, McpOAuthSettings settings)
        {
            if (await store.LoadAsync() is null) await store.SaveAsync(Cached(Metadata(true, documents), client: false));
            Uri? shown = null;
            var prompt = new McpSignInPrompt(url => { shown = url; lock (Authorizations) Authorizations.Add(url); }, _ =>
            {
                if (shown is null) throw new InvalidOperationException("No authorization URL was shown.");
                var redirect = Query(shown, "redirect_uri")!; var state = Query(shown, "state")!;
                return Task.FromResult<string?>(redirect + "?code=code-" + Authorizations.Count + "&state=" + Uri.EscapeDataString(state) + "&iss=" + Uri.EscapeDataString(Issuer));
            });
            await McpSignIn.SignInAsync(new(Server, store, settings, prompt, Http.Factory)).WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    private static string? Form(string body, string name) => Query(new Uri("https://form.invalid/?" + body), name);
}
