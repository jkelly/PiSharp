# Bounded native shared event bus mapping R551

Base: accepted private SDK commit `e7d33b7261e0925199f4a294b14a8eb2fcee7e6d`, tree `f93f74588140f5ab7bc91a1efc36569a45196f52`. Original Pi v0.99.1: `d86654abb8862e201933517d6f1fce9f88dd117f`.

## Actual implementation gap

Original `packages/coding-agent/src/core/extensions/types.ts:1857` publishes `ExtensionAPI.events: EventBus`. Its `event-bus.ts:3-6` declares synchronous `emit(channel, data)` and `on(channel, handler)` returning an unsubscribe function. The accepted native `IExtensionRegistry` has no shared event bus; its `Observe` subscribes to awaited host observations and supplies a host context. Those observations do not implement extension-to-extension synchronous emission. This is an actual missing public capability, independent of unrun coverage.

This candidate adds optional `IExtensionEventBusRegistry.Events`, implemented by the existing `RegistrationScope`, without changing `IExtensionRegistry`. Each `ExtensionRegistry` supplies one private shared bus. The API maps JavaScript `unknown` to in-process `object?`, and an unsubscribe function to `IDisposable`. Existing native registration callers remain source compatible. This is an intentional bounded native mapping, not unchanged JavaScript ABI acceptance.

## Callback behavior and ownership

`ExtensionEventBus.Emit` captures the current ordered subscription list, calls listeners synchronously without holding its lock, and preserves the payload reference, including null. Removal/addition/clear during a listener changes later or recursive emissions; removed listeners in the already captured emission still execute, matching Node EventEmitter's listener snapshot behavior. Duplicate subscriptions are independent. Channel comparison is ordinal, including empty channels. Listener exceptions are reported and swallowed so subsequent listeners run. The optional host error reporter cannot make Emit throw by itself. Default reporting writes the channel and full exception to standard error.

Original `event-bus.ts:20-27` wraps each listener in an async function and awaits any returned runtime Promise, catching asynchronous failures. This native candidate deliberately accepts **synchronous `Action<object?>` listeners only**. Async-void listeners and callbacks that launch detached work are unsupported. No task is borrowed, detached, joined or claimed as qualified here. A future asynchronous mapping needs explicit original-task tracking, fault/cancellation identity and owner settlement controls; the Promise-returning behavior remains an implementation gap.

Original `loader.ts:496-505` asserts runtime activity before emit/on and tracks unsubscribe functions; `loader.ts:196-209` invalidates tracked subscriptions. Native owner facades reject operations after lifetime cancellation and attach each subscription to `ExtensionLifetimeCancellationToken`. Explicit disposal removes both listener and cancellation registration, idempotently. Initialization can subscribe immediately, as original loading does; failed initialization cancels the owner and removes the loading subscription through the existing cleanup path. Native cancellation is the retirement boundary. A callback already captured by an emission can still run after another listener disposes its owner, consistent with the original emitter snapshot. These synchronous communication callbacks do not borrow operation/session contexts or use the registry's awaited callback-admission path. No core session, Agent, transport or terminal behavior is changed.

The sole existing-file change is the `RegistrationScope` declaration becoming partial and implementing the new optional interface. The registry is already partial. Public contracts, runtime implementation, scope facade, standalone control project and this document are new files.

## Pinned source bytes

All paths below are within the local source oracle `P:/PiSharp/root/Documents/Codex/2026-09-30/task-2/Pi-typescript-semantic-oracle-v0.99.1/upstream/`:

| Source | SHA-256 |
| --- | --- |
| `packages/coding-agent/src/core/event-bus.ts` | `fe5c39a57081fb9674c5bdc8c4bc6779bb8524c8895afc337af5ea517bdc71a2` |
| `packages/coding-agent/src/core/extensions/types.ts` | `de0ccce3b8b222d19a2ecef24902fc72aab62b6573129cbf886bc064dd6ab505` |
| `packages/coding-agent/src/core/extensions/loader.ts` | `aa903524bb2db3ee9fb7f21e863e373e7f12a56c74fd05d27c879a4a7020deb0` |

Historical inventory counts such as 177 capabilities and 562 member signatures are not recomputed or used as current implementation coverage.

## Authored verification, not executed

The standalone `tests/PiSharp.SdkEventBus.Tests/PiSharp.SdkEventBus.Tests.csproj` authors eight controls: order/identity/null; listener snapshot mutation; duplicate/idempotent unsubscribe; listener/reporter fault isolation; clear/recursive emission; same-host sharing and host isolation; owner retirement/stale rejection; failed-initialization cleanup. It references the runtime project directly and is not added to the shared solution or test runner. The parent owns all restore, compilation and execution admission. No build, test, compiler, Node, NativeRunner, network, server or process qualification was performed in this lane. Source metadata checks do not establish behavior acceptance.

The global SDK checkpoint remains partial: this adds one bounded capability at source level, with synthetic control execution pending; async listeners, the broader original ABI, callback facades, public plugin corpus and platform coverage remain separate implementation or verification gaps.
