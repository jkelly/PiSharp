// Decision 0011 (run W2, WU6): routing under the chat area, end to end in process. ChatHarness (WU7) runs the chats on the durable device;
// here its faux models and catalog are replaced by the routing area's (RuntimeModelCatalog, WebDurableModels through AccountRouter) over the
// credentials area, a fake environment, an ephemeral key ring and the fake provider (ProviderFake), so every chat request goes through the
// account its chat was created with: a chat keeps that account when another becomes active, and a chat whose account was removed or expired
// shows the expired state (the chat page's banner) and sends nothing (never another account). No request leaves the process.
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PiSharp.Durable;
using PiSharp.Durable.Client;
using PiSharp.WebAgent.Chat;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Credentials;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Hosting;
using PiSharp.WebAgent.Routing;

internal static partial class Program
{
    /// <summary>ChatHarness with real routing (not --faux) over <paramref name="provider"/> and an empty environment.</summary>
    internal static Task<ChatHarness> RoutedChatAsync(string name, ProviderFake provider) =>
        ChatHarness.StartAsync(name, configure: services =>
        {
            services.RemoveAll<IModelCatalog>();
            services.RemoveAll<IDurableModels>();
            services.RemoveAll<WebAgentDb>();
            services.AddSingleton(sp => new WebAgentDb(sp.GetRequiredService<WebAgentOptions>().DatabasePath,
                [CoreMigrations.AppInfo, ChatBindings.Migration, SettingsStore.Migration, CredentialVault.Migration]));
            // An ephemeral key ring: the test's credentials.json is never protected with a key in the user's profile.
            services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
            services.AddSingleton(sp => RoutingCredentials(new Dictionary<string, string>(), new FakeOAuth(), sp.GetRequiredService<WebAgentOptions>().DataDirectory));
            services.AddSingleton(new RoutingOptions { ProviderHandler = provider });
            // The areas read only the options' Faux switch (the harness's own options, faux, keep the data folder and the device).
            var routed = new WebAgentOptions { DataDirectory = Path.Join(Path.GetTempPath(), "unused"), Faux = false };
            services.AddCredentialsArea(routed);
            services.AddRoutingArea(routed);
        });

    /// <summary>Checks that the chat page shows routing's account error <paramref name="error"/> as the expired state (WU8's banner and
    /// failed answer), with its sentence instead of the raw prefix (integration of WU6 and WU8).</summary>
    internal static async Task ExpiredBannerAsync(ChatHarness chat, string chatId, string? error)
    {
        Equal(PiSharp.WebAgent.Chat.ChatFailureKind.Expired, ChatFailures.Classify("error", error), "the chat pages classify it as expired: " + error);
        var page = await chat.Chats.OpenAsync(chatId);
        Equal("openai", page.Banners.ExpiredProvider, "the expired banner shows for the chat's provider");
        Check(page.Banners.Detail is { Length: > 0 } detail && !detail.Contains(AccountRouteErrors.Prefix, StringComparison.Ordinal)
            && char.IsUpper(detail[0]), "the banner shows the error's sentence: " + page.Banners.Detail);
    }

    /// <summary>The last answer of the chat's current branch.</summary>
    internal static async Task<AssistantItem> LastAnswerAsync(ChatHarness chat, string chatId) =>
        (await chat.ItemsAsync(chatId)).OfType<AssistantItem>().Last();

    internal static IEnumerable<(string, Func<Task>)> RoutingChatCases() =>
    [
        Case("routing.chat.a-chat-keeps-its-account-after-the-active-one-changes", async () =>
        {
            var provider = new ProviderFake();
            await using var chat = await RoutedChatAsync("route-chat-keeps", provider);
            Check(chat.Services.GetRequiredService<IDurableModels>() is WebDurableModels, "the chats run on the routed models");
            var accounts = chat.Services.GetRequiredService<IAccountStore>();
            var model = ModelOf(chat.Services.GetRequiredService<IModelCatalog>(), "openai", "openai-responses", "gpt-4.1");
            var keyA = Planted("sk-proj-a");
            var keyB = Planted("sk-proj-b");
            var a = (await accounts.AddApiKeyAsync(new ApiKeyAccountRequest("openai", keyA) { Label = "Personal" })).Account.Id;
            var b = (await accounts.AddApiKeyAsync(new ApiKeyAccountRequest("openai", keyB) { Label = "Work" })).Account.Id;

            var one = (await chat.Chats.CreateAsync(new ChatCreateRequest("route-one", "chat-one-first") { Cwd = chat.Work, Provider = "openai", Model = model })).Binding;
            await chat.IdleAsync(one.SessionId);
            Equal(a, one.AccountId, "the first chat binds the active account");
            await accounts.SetActiveAsync(b);
            var two = (await chat.Chats.CreateAsync(new ChatCreateRequest("route-two", "chat-two-first") { Cwd = chat.Work, Provider = "openai", Model = model })).Binding;
            await chat.IdleAsync(two.SessionId);
            Equal(b, two.AccountId, "a new chat binds the new active account");
            await chat.Chats.SendAsync(one.SessionId, new ChatSendRequest("route-one-2", "chat-one-second"));
            await chat.IdleAsync(one.SessionId);

            string AuthorizationOf(string text) => provider.Requests.First(request => request.Body?.Contains(text, StringComparison.Ordinal) == true).Header("authorization")!;
            Equal("Bearer " + keyA, AuthorizationOf("chat-one-first"), "chat one's first request used A");
            Equal("Bearer " + keyB, AuthorizationOf("chat-two-first"), "chat two's request used B");
            Equal("Bearer " + keyA, AuthorizationOf("chat-one-second"), "chat one still uses A after B became active");
            Equal(a, chat.Bindings.Get(one.SessionId)!.AccountId, "and is still bound to A");
            foreach (var id in new[] { one.SessionId, two.SessionId })
            {
                var answer = await LastAnswerAsync(chat, id);
                Check(answer.Error is null && answer.Blocks.OfType<TextBlockView>().Any(block => block.Text.Contains("Hello world", StringComparison.Ordinal)),
                    $"chat {id} shows the provider's answer: {answer.Error}");
            }
            Check(provider.Requests.All(request => request.Body is null || !request.Body.Contains("\"pisharp\"", StringComparison.Ordinal)), "no metadata reached the provider");
            NoSecret(await chat.TextAsync(one.SessionId) + await chat.TextAsync(two.SessionId), "the transcripts", keyA, keyB);
        }),

        Case("routing.chat.an-expired-or-removed-account-shows-expired-and-sends-nothing", async () =>
        {
            var provider = new ProviderFake();
            await using var chat = await RoutedChatAsync("route-chat-expired", provider);
            var accounts = chat.Services.GetRequiredService<IAccountStore>();
            var model = ModelOf(chat.Services.GetRequiredService<IModelCatalog>(), "openai", "openai-responses", "gpt-4.1");
            var keyA = Planted("sk-proj-a");
            var a = (await accounts.AddApiKeyAsync(new ApiKeyAccountRequest("openai", keyA) { Label = "Personal" })).Account.Id;
            var one = (await chat.Chats.CreateAsync(new ChatCreateRequest("route-expired", "first") { Cwd = chat.Work, Provider = "openai", Model = model })).Binding;
            await chat.IdleAsync(one.SessionId);
            Equal(null, (await LastAnswerAsync(chat, one.SessionId)).Error, "the first answer");
            var sent = provider.Requests.Count;

            await accounts.MarkExpiredAsync(a, "test");
            await chat.Chats.SendAsync(one.SessionId, new ChatSendRequest("route-expired-2", "second"));
            await chat.IdleAsync(one.SessionId);
            var error = (await LastAnswerAsync(chat, one.SessionId)).Error;
            Equal(AccountRouteErrors.Expired, AccountRouteErrors.Reason(error), "the chat shows the expired state: " + error);
            Equal(sent, provider.Requests.Count, "nothing was sent");
            await ExpiredBannerAsync(chat, one.SessionId, error);

            var keyB = Planted("sk-proj-b");
            var b = (await accounts.AddApiKeyAsync(new ApiKeyAccountRequest("openai", keyB) { Label = "Work" })).Account.Id;
            await accounts.SetActiveAsync(b);
            await accounts.RemoveAsync(a);
            await chat.Chats.SendAsync(one.SessionId, new ChatSendRequest("route-expired-3", "third"));
            await chat.IdleAsync(one.SessionId);
            error = (await LastAnswerAsync(chat, one.SessionId)).Error;
            Equal(AccountRouteErrors.Removed, AccountRouteErrors.Reason(error), "a removed account: " + error);
            Equal(sent, provider.Requests.Count, "nothing was sent, with the new active account either");
            await ExpiredBannerAsync(chat, one.SessionId, error);
            Equal(a, chat.Bindings.Get(one.SessionId)!.AccountId, "the chat did not move to another account");
            NoSecret(await chat.TextAsync(one.SessionId), "the transcript", keyA, keyB, b);
        }),

        // The real Program without --faux (a temporary data folder; the launcher strips this machine's keys and tokens from the child's
        // environment). Only pages are fetched: no chat is sent, so no provider is called.
        Case("routing.app.without-faux-the-app-offers-the-built-in-providers", async () =>
        {
            var root = Temp("route-app");
            try
            {
                await using var app = await WebAgentApp.StartAsync(root, new AppStart { DataDirectory = Path.Join(root, "data"), Faux = false });
                var health = await app.Get("/api/health");
                Equal(200, health.Status, "health");
                Equal(false, health.Body!["faux"]!.GetValue<bool>(), "the faux switch is off");
                var page = await app.Get("/chat/new");
                Equal(200, page.Status, "the new chat page");
                Check(page.Text.Contains("OpenAI Codex", StringComparison.Ordinal) && page.Text.Contains("Anthropic", StringComparison.Ordinal),
                    "it offers the built-in providers");
                Check(!app.Log.Contains("fail:", StringComparison.Ordinal) && !app.Log.Contains("Exception", StringComparison.Ordinal), "a clean start: " + app.Log);
            }
            finally
            {
                DeleteTemp(root);
            }
        }),
    ];
}
