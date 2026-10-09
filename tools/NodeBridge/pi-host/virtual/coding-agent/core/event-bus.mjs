// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/event-bus.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
import { EventEmitter } from "node:events";
export function createEventBus() {
    const emitter = new EventEmitter();
    return {
        emit: (channel, data)=>{
            emitter.emit(channel, data);
        },
        on: (channel, handler)=>{
            const safeHandler = async (data)=>{
                try {
                    await handler(data);
                } catch (err) {
                    console.error(`Event handler error (${channel}):`, err);
                }
            };
            emitter.on(channel, safeHandler);
            return ()=>emitter.off(channel, safeHandler);
        },
        clear: ()=>{
            emitter.removeAllListeners();
        }
    };
}
