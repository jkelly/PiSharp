import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { pathToFileURL } from "node:url";
import { parseJsonSupported } from "../CompatibilityReport/raw-json.mjs";

const [upstreamRoot, fixturePath] = process.argv.slice(2);
const fixture = parseJsonSupported(readFileSync(fixturePath, "utf8"));
if (fixture.kind !== "upstream-event-stream-input") throw new Error("Expected a reference stream input fixture");
// Node's built-in type stripping loads the exact, unmodified pinned source file.
const { createAssistantMessageEventStream } = await import(pathToFileURL(resolve(upstreamRoot, "packages/ai/src/utils/event-stream.ts")));
const stream = createAssistantMessageEventStream();
const emissionSnapshots = [];
let mutableMessage;
for (const operation of fixture.operations) {
  switch (operation.kind) {
    case "set_message": mutableMessage = structuredClone(operation.message); break;
    case "set_content": mutableMessage.content = structuredClone(operation.content); break;
    case "push": {
      const event = structuredClone(operation.event);
      if (operation.messageProperty) event[operation.messageProperty] = mutableMessage;
      emissionSnapshots.push(structuredClone(event));
      stream.push(event);
      break;
    }
    default: throw new Error(`Unsupported reference operation ${operation.kind}`);
  }
}
const consumedEvents = [];
for await (const event of stream) consumedEvents.push(structuredClone(event));
const result = structuredClone(await stream.result());
console.log(JSON.stringify({ emissionSnapshots, consumedEvents, result }));
