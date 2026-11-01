---
title: Service tiers
description: Choose normal, economy, priority, fast or ultrafast capacity for each request. Experimental and unreleased.
sidebar:
  badge:
    text: Unreleased
    variant: caution
---

:::caution[Experimental and unreleased]
Service tiers are on PiSharp's `main` branch but not in any release or NuGet package yet. To use them, build PiSharp from source and reference the projects directly (see [Build from source](/docs/contribute/build/)). Their API is marked `[Experimental("PISHARP_TIERS001")]`, so suppress `PISHARP_TIERS001` in your project to compile it, and it may change or be removed without notice.
:::

A service tier asks the provider for a class of capacity: cheaper and slower, steadier at a higher price, or the fastest output a model has. Providers name these differently. PiSharp gives them one set of names and maps each to the field the provider's API expects. Pi has no service tiers; this is a PiSharp addition ([decision 0007](https://github.com/jkelly/PiSharp/blob/main/docs/decisions/0007-media-generation.md)).

The `--tier` option and the `/tier` command are not in any release yet. To use them, run a CLI built from source (see [Run the CLI from source](/docs/contribute/build/#run-the-cli-from-source)).

## Tiers

| Tier | Also accepted | What it asks for |
| --- | --- | --- |
| `auto` | | The provider decides. |
| `normal` | `default`, `standard`, `off`, `none` | Standard capacity and price. |
| `economy` | `flex`, `cheap`, `eco` | Flex processing: about half price, slower, and requests may queue (OpenAI, OpenRouter, Google). |
| `priority` | `prio` | Priority capacity: steadier and faster, at about twice the price. |
| `fast` | `speed` | Fast mode: the highest output speed (OpenAI `fast`, Anthropic `speed: fast`). |
| `ultrafast` | `ultra`, `super`, `superspeed`, `super-speed`, `turbo` | OpenRouter's and OpenAI's ultrafast endpoints; elsewhere the same as `fast`. |

Names are case-insensitive, and `_` counts as `-`. With no tier set, PiSharp sends no tier field and the provider uses its default. A tier only changes requests to providers that support one; see [What each provider gets](#what-each-provider-gets).

:::note
`/tier none` and `/tier clear` remove the tier. Everywhere else, `none` and `off` are aliases of `normal`, so `--tier none` and `"serviceTier": "off"` ask for standard capacity.
:::

## In pisharp

There are three ways to set the tier for a run of `pisharp`:

| How | Effect |
| --- | --- |
| `--tier <tier>` or `--service-tier <tier>` | The tier for this run. An unknown name prints the warning `Invalid service tier "<name>". Valid values: ...` and the run falls back to the setting. |
| `serviceTier` setting | The tier when you pass no `--tier`, from the global or project `settings.json`. An unknown name is ignored. |
| `/tier` in interactive mode | Changes the tier from the next request. |

```sh
pisharp --tier economy -p "Summarize CHANGELOG.md"
pisharp --tier fast
```

```json
{
  "serviceTier": "priority"
}
```

The `/tier` command:

| Command | What it does |
| --- | --- |
| `/tier` | Shows the current tier and the choices. |
| `/tier <tier>` | Uses that tier from the next request, for this run only. |
| `/tier <tier> default` | Also saves it as `serviceTier` in your global settings. |
| `/tier none` or `/tier clear` | Removes the tier, so requests carry none. |
| `/tier none default` | Removes it and deletes the global `serviceTier` setting. |

The footer shows the tier after the model and thinking level, for example `• economy`. Every request the session sends reads the tier at the moment it is sent, so a change applies to the next request, not to one already streaming. See [Command line](/docs/reference/cli/) and [Settings](/docs/reference/settings/) for the other options.

## In pisharp durable

In [`pisharp durable`](/docs/guides/durable/) the tier belongs to a conversation and is saved in the session:

- `pisharp durable --tier <tier>` sets the main conversation's tier when the run creates the session. Without `--tier`, a new session takes the `serviceTier` setting. A session reopened with `--continue` keeps the tier it has, and an unknown `--tier` name fails with exit code 1.
- `/tier` shows the tier of the conversation on screen, `/tier <tier>` sets it, and `/tier none` (or `clear`) removes it. After `/agents`, `/tier` acts on the subagent you switched to. There is no `default` form here.
- The `subagent` tool takes an optional `tier` argument for the child conversation. Without it, the child starts with its parent's tier. An unknown name fails the call with `Unknown service tier`.
- The footer shows `tier:<tier> (/tier)` while the conversation has one.

A model request records its tier in its checkpoint, so a turn that resumes after a crash asks for the tier it started with. The media tools have no tier field of their own; `generate_image` on OpenRouter turns the conversation's tier into provider routing instead (see [Media tools](/docs/guides/media/#service-tiers)).

## What each provider gets

PiSharp maps the tier to the request's own field. Providers and APIs not listed here, such as Mistral, Amazon Bedrock and Azure OpenAI, get no tier field.

| PiSharp tier | OpenAI and OpenRouter: `service_tier` | OpenAI Codex: `service_tier` | Anthropic | Google Gemini: `serviceTier` |
| --- | --- | --- | --- | --- |
| `auto` | `auto` | not sent | `service_tier: auto` | not sent |
| `normal` | `default` | not sent | `service_tier: standard_only` | `standard` |
| `economy` | `flex` | `flex` | `service_tier: standard_only` | `flex` |
| `priority` | `priority` | `priority` | `service_tier: auto` | `priority` |
| `fast` | `fast` | `priority` | `speed: fast` | `priority` |
| `ultrafast` | `ultrafast` | `priority` | `speed: fast` | `priority` |

- **OpenAI and OpenRouter.** The Responses and Chat Completions APIs send `service_tier` for the `openai` and `openrouter` providers only. Another provider on the same API, such as a local server, gets nothing.
- **OpenAI Codex.** The ChatGPT subscription route accepts only `flex` and `priority`, so `fast` and `ultrafast` become `priority`, and `auto` and `normal` send nothing.
- **Anthropic.** The Messages API sends the fields for the `anthropic` and `openrouter` providers. `speed: fast` goes only to models with fast mode (ids containing `opus-5`, `opus-4-8` or `opus-4.8`), with the `fast-mode-2026-02-01` beta header. On other models `fast` and `ultrafast` send `service_tier: auto` instead.
- **Google.** The Gemini API sends `serviceTier` in the request body, unless the body already has one.

A transport's own configured tier still applies to requests that carry none. A request's tier wins over it.

## In your code

Requests carry the tier in `ChatRequest.ServiceTier`. Each protocol maps it, and protocols without tiers ignore it. The agent and session layers take a function, read when each request is sent, so you can change the tier while a session runs:

```csharp
using PiSharp.Agent;
using PiSharp.AI;
using PiSharp.Contracts;

var tier = ServiceTiers.Economy;

// Every request this agent sends asks for the tier current at that moment.
var configuration = new AgentConfiguration(model, transport, []) { ServiceTier = () => tier };

// One request on its own.
var request = new ChatRequest(model, messages) { ServiceTier = ServiceTiers.Priority };

// Later requests use priority.
tier = ServiceTiers.Priority;
```

Here `model` is a `ModelDescriptor`, `transport` an `IChatTransport` and `messages` the request's `TranscriptEntry` list. In a durable harness, the tier is part of the conversation's agent choices (this also needs `PISHARP_DURABLE001`):

```csharp
using PiSharp.AI;
using PiSharp.Durable;

await conversation.ConfigureAsync(new AgentChange { ServiceTier = ServiceTiers.Economy }, context);
await conversation.ConfigureAsync(new AgentChange { ServiceTier = Patch<string>.Clear }, context);
```

`ServiceTiers` also does the mapping, if you build requests yourself:

```csharp
using PiSharp.AI;

Console.WriteLine(ServiceTiers.Normalize("flex"));            // economy
Console.WriteLine(ServiceTiers.OpenAI("economy", "openai"));  // flex
var (serviceTier, speed) = ServiceTiers.Anthropic("fast", "anthropic", "claude-opus-5");
Console.WriteLine($"{serviceTier ?? "-"} {speed ?? "-"}");    // - fast
```

### API

| Member | Namespace | Meaning |
| --- | --- | --- |
| `ServiceTiers` | `PiSharp.AI` | `Auto`, `Normal`, `Economy`, `Priority`, `Fast`, `Ultrafast` and `All`; `Describe(tier)`; `Normalize(name)` (null for an unknown name); and the mappings `OpenAI(tier)`, `OpenAI(tier, provider)`, `Codex(tier)`, `Anthropic(tier)`, `Anthropic(tier, provider, modelId)`, `Google(tier)` and `SupportsAnthropicFastMode(modelId)`. |
| `ChatRequest.ServiceTier` | `PiSharp.AI` | The tier of one request. Null keeps the transport's own. |
| `AgentConfiguration.ServiceTier` | `PiSharp.Agent` | `Func<string?>`, read when each request is sent. |
| `TurnRunner.ServiceTier` | `PiSharp.Agent` | `Func<string?>`, stamped on every request that has none. |
| `SessionRuntimeRegistryOptions.ServiceTier` | `PiSharp.CodingAgent` | `Func<string?>`: the live tier every configuration of a coding session carries. The CLI sets it. |
| `AnthropicMessagesKeyAuthRequestOptions.ServiceTier`, `.Speed`, `.PiSharpTier` | `PiSharp.AI.Protocols.AnthropicMessages` | A transport's configured `service_tier` and `speed` wire values, and the PiSharp tier they came from. |
| `GoogleGenerativeAIOptions.ServiceTier` | `PiSharp.AI.Protocols.GoogleGenerativeAI` | A transport's configured `serviceTier` wire value. |
| `CompletionsKeyAuthRequestOptions.ServiceTier` | `PiSharp.AI.Protocols.OpenAICompletions` | A transport's configured `service_tier` wire value. |
| `AgentChange.ServiceTier`, `Agent.ServiceTier`, `DurableStreamOptions.ServiceTier` | `PiSharp.Durable` | A durable conversation's tier, as stored and as sent. Marked `PISHARP_DURABLE001` like the rest of the durable API. |

`ResponsesKeyAuthRequestOptions` and `OpenAICodexResponsesOptions` already had a `ServiceTier` option in Pi, so those are not experimental. A transport serves every tier: the request factories build one scoped variant per requested tier, so you don't need a transport per tier.
