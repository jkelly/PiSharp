using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using PiSharp.Cli.Mcp.Authentication;
using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.Authentication;
using PiSharp.Extensions.Runtime.Mcp.Authentication;

internal static partial class Program
{
    private static async Task OccupiedCallbackRefresh() => await OccupiedCallbackSignIn("success");
    private static async Task OccupiedCallbackCancellation() => await OccupiedCallbackSignIn("cancel");
    private static async Task OccupiedCallbackFallback() => await OccupiedCallbackSignIn("fallback");

    // Only the callback listener uses loopback sockets. All authorization-server requests
    // are admitted in-memory fakes, with synthetic credentials and no live provider.
    private static async Task OccupiedCallbackSignIn(string mode)
    {
        using var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Start();
        var registeredUri = $"http://127.0.0.1:{((IPEndPoint)occupied.LocalEndpoint).Port}/callback";
        var client = JsonData.Parse(JsonSerializer.Serialize(new { client_id = "original-client", redirect_uris = new[] { registeredUri } }));
        var store = new McpOAuthCredentialStore(new MemoryBackend()).ForServer("docs", Server);
        var original = Cached(Metadata(), tokens: new("original-access", "Bearer", Scope: "read", RefreshToken: "original-refresh"))
            with { ClientInformation = client };
        await store.SaveAsync(original);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new FakeServer();
        var prompts = 0; string? browserRedirect = null;
        server.Respond = async (request, body, token) =>
        {
            if (request.RequestUri!.AbsolutePath == "/register")
            {
                using var registration = JsonDocument.Parse(body);
                browserRedirect = registration.RootElement.GetProperty("redirect_uris")[0].GetString();
                return Response(201, "{\"client_id\":\"replacement-client\"}");
            }
            Equal("/token", request.RequestUri.AbsolutePath);
            var form = new Uri("https://form.invalid/?" + body);
            if (Query(form, "grant_type") == "refresh_token")
            {
                Equal("original-client", Query(form, "client_id"));
                Equal("original-refresh", Query(form, "refresh_token"));
                Equal(client.ToString(), (await store.LoadAsync())!.ClientInformation!.ToString());
                Equal("admitted-state", (await store.LoadAsync())!.OAuthState);
                entered.TrySetResult();
                if (mode == "cancel") await Task.Delay(Timeout.Infinite, token);
                if (mode == "fallback") return Response(500, "{\"error\":\"server_error\"}");
                return Response(200, "{\"access_token\":\"rotated-access\",\"token_type\":\"Bearer\",\"refresh_token\":\"rotated-refresh\"}");
            }
            Equal("authorization_code", Query(form, "grant_type"));
            Equal("replacement-client", Query(form, "client_id"));
            Equal(browserRedirect, Query(form, "redirect_uri"));
            return Response(200, "{\"access_token\":\"browser-access\",\"token_type\":\"Bearer\"}");
        };
        Uri? shown = null;
        var prompt = new McpSignInPrompt(uri => { prompts++; shown = uri; }, _ =>
        {
            var redirect = Query(shown!, "redirect_uri");
            Check(redirect != registeredUri, "The interactive callback must bind a free port.");
            return Task.FromResult<string?>(redirect + "?code=fake-code&state=" + Query(shown!, "state"));
        });
        using var cancel = new CancellationTokenSource();
        var signIn = McpSignIn.SignInAsync(new(Server, store, new(), prompt, server.Factory), cancel.Token);
        if (mode == "cancel")
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await cancel.CancelAsync();
            Find<McpSignInCancelledException>(await Failure(signIn));
            var retained = (await store.LoadAsync())!;
            Equal(client.ToString(), retained.ClientInformation!.ToString());
            Equal("original-access", retained.Tokens!.AccessToken);
            Equal("original-refresh", retained.Tokens.RefreshToken);
            Equal("admitted-state", retained.OAuthState);
        }
        else
        {
            await signIn;
            var saved = (await store.LoadAsync())!;
            Equal(mode == "success" ? "rotated-access" : "browser-access", saved.Tokens!.AccessToken);
            if (mode == "success")
            {
                Equal("rotated-refresh", saved.Tokens.RefreshToken);
                Equal(client.ToString(), saved.ClientInformation!.ToString());
                Equal("read", saved.Tokens.Scope);
                Equal(registeredUri, McpSignIn.RegisteredRedirectUrls(saved.ClientInformation).Single());
            }
        }
        Equal(mode == "fallback" ? 1 : 0, prompts);
        Equal(mode == "fallback" ? 3 : 1, server.Log.Count);
        Equal(server.Log.Count, server.Stops);
    }
}
