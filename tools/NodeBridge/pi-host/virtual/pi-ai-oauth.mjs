// PiSharp shim for `@earendil-works/pi-ai/oauth` (upstream packages/ai/src/oauth.ts).
// Upstream's oauth entry point is type-only (OAuthAuthInfo, OAuthCredentials, OAuthDeviceCodeInfo,
// OAuthLoginCallbacks, OAuthPrompt, OAuthSelectOption, OAuthSelectPrompt): it has no runtime exports, so
// neither does this module. Extension OAuth flows run in the PiSharp host via ExtensionAPI.registerProvider.
export {};
