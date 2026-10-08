import assert from 'node:assert/strict';

export function createOriginalToolContext(runner, toolCallId, signal) {
  const context = runner.createToolContext(toolCallId, signal);
  assert(typeof Object.getOwnPropertyDescriptor(context, 'tools')?.get === 'function' && typeof context.executeTool === 'function',
    'Original tool context requires the guarded tools getter and executeTool method');
  return context;
}
export function unsupportedToolOperation(surface) {
  return () => {
    const error = new Error('Unsupported Node tool context operation: ' + surface);
    error.bridgeCode = 'UnsupportedHostOperation'; error.surface = surface; throw error;
  };
}
