// PiSharp host-backed replacement for pi-ai's provider API implementation modules
// (packages/ai/src/api/<api>.ts, loaded lazily by the ported `*.lazy.ts` wrappers). Instead of talking to
// the provider over the network, each call is forwarded to the PiSharp host through the bridge hook:
//
//   stream / streamSimple / fetchDeferred : globalThis.__pisharpBridge.call(name, model, context, options)
//   cancelDeferred                       : globalThis.__pisharpBridge.call("cancelDeferred", model, handle, options)
//   generateImages                       : globalThis.__pisharpBridge.call("generateImages", model, context, options)
//
// The host may return (or resolve to) an AsyncIterable of AssistantMessageEvents (optionally with
// `result()`), an array of events, or a final AssistantMessage. Without the hook the stream ends with an
// error event "<name> is not available in the PiSharp Node bridge" (via lazyStream's setup-error path).
import { AssistantMessageEventStream } from "../ai/utils/event-stream.mjs";
import { hostCall } from "../bridge.mjs";

function isAsyncIterable(value) {
	return value != null && typeof value[Symbol.asyncIterator] === "function";
}

function isAssistantMessage(value) {
	return value != null && typeof value === "object" && value.role === "assistant";
}

/** Normalize whatever the host returned into an AssistantMessageEventStream. */
export function toAssistantMessageEventStream(result) {
	if (result instanceof AssistantMessageEventStream) return result;
	const stream = new AssistantMessageEventStream();
	const pushMessage = (message) => {
		if (message.stopReason === "error" || message.stopReason === "aborted") {
			stream.push({ type: "error", reason: message.stopReason, error: message });
		} else {
			stream.push({ type: "start", partial: message });
			stream.push({ type: "done", reason: message.stopReason ?? "stop", message });
		}
		stream.end(message);
	};
	(async () => {
		let value = await result;
		if (isAssistantMessage(value)) {
			pushMessage(value);
			return;
		}
		if (Array.isArray(value)) {
			const events = value;
			value = (async function* () {
				yield* events;
			})();
		}
		if (!isAsyncIterable(value)) throw new Error("PiSharp host returned an unsupported stream result");
		for await (const event of value) stream.push(event);
		stream.end(typeof value.result === "function" ? await value.result() : undefined);
	})().catch((error) => {
		const message = {
			role: "assistant",
			content: [],
			api: "unknown",
			provider: "unknown",
			model: "unknown",
			usage: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, totalTokens: 0, cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 } },
			stopReason: "error",
			errorMessage: error instanceof Error ? error.message : String(error),
			timestamp: Date.now(),
		};
		stream.push({ type: "error", reason: "error", error: message });
		stream.end(message);
	});
	return stream;
}

export function stream(model, context, options) {
	return toAssistantMessageEventStream(hostCall("stream", model, context, options));
}

export function streamSimple(model, context, options) {
	return toAssistantMessageEventStream(hostCall("streamSimple", model, context, options));
}

export function fetchDeferred(model, handle, options) {
	return toAssistantMessageEventStream(hostCall("fetchDeferred", model, handle, options));
}

export async function cancelDeferred(model, handle, options) {
	await hostCall("cancelDeferred", model, handle, options);
}

export async function generateImages(model, context, options) {
	return await hostCall("generateImages", model, context, options);
}
