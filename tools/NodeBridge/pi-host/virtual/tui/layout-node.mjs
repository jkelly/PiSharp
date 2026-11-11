// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/tui/src/layout-node.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
export const LAYOUT_NODE = Symbol.for("@earendil-works/pi-tui/layout-node");
export function getLayoutNode(component) {
    const candidate = component;
    return typeof candidate[LAYOUT_NODE] === "function" ? candidate[LAYOUT_NODE]() : undefined;
}
