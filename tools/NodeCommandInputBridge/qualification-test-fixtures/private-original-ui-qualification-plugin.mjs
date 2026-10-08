// Authored private qualification consumer of the GENUINE original ExtensionAPI/Runner.
// NOT an upstream example and NOT admitted by the production six-example source allowlist.
// No imports, filesystem, process, network, credentials or replacement public namespace.
export default function originalUiQualification(pi) {
  const controllers = new Map();
  const result = (ctx, kind, value) => ctx.ui.notify(JSON.stringify({ kind, presence: value === undefined ? 'undefined' : 'json', ...(value === undefined ? {} : { value }) }), 'info');
  pi.registerCommand('qualification-input', {
    description: 'Private admitted original input consumer',
    handler: async (_args, ctx) => result(ctx, 'input', await ctx.ui.input('Original SDK input', '', { timeout: 1234 }))
  });
  pi.registerCommand('qualification-editor', {
    description: 'Private admitted original editor consumer',
    handler: async (_args, ctx) => result(ctx, 'editor', await ctx.ui.editor('Original SDK editor', 'line1\nline2\ud800'))
  });
  pi.registerCommand('qualification-timeout', {
    description: 'Private admitted original timeout consumer',
    handler: async (_args, ctx) => result(ctx, 'timeout', await ctx.ui.confirm('Original SDK timeout', 'One millisecond', { timeout: 1 }))
  });
  pi.registerCommand('qualification-preabort', {
    description: 'Private admitted original pre-aborted signal consumer',
    handler: async (_args, ctx) => {
      const controller = new AbortController(); controller.abort();
      result(ctx, 'preabort', await ctx.ui.input('No native input should be acquired', '', { signal: controller.signal }));
    }
  });
  pi.registerCommand('qualification-child', {
    description: 'Private admitted original held child cancellation consumer',
    handler: async (args, ctx) => {
      if (args !== 'held-child' || controllers.has(args)) throw Error('Private child identity');
      const controller = new AbortController(); controllers.set(args, controller);
      try { result(ctx, 'child', await ctx.ui.confirm('Held child confirmation', 'Abort only this child', { signal: controller.signal })); }
      finally { controllers.delete(args); }
    }
  });
  pi.registerCommand('qualification-abort', {
    description: 'Private admitted original source cancellation trigger',
    handler: async args => {
      if (args !== 'held-child' || !controllers.has(args)) throw Error('No actual source child');
      controllers.get(args).abort();
    }
  });
  pi.registerCommand('qualification-parent', {
    description: 'Private admitted original held parent-cancellation consumer',
    handler: async (_args, ctx) => result(ctx, 'parent', await ctx.ui.input('Held actual parent cancellation'))
  });
  pi.registerCommand('qualification-session', {
    description: 'Private admitted original held session-retirement consumer',
    handler: async (_args, ctx) => result(ctx, 'session', await ctx.ui.input('Held actual session retirement'))
  });
}
