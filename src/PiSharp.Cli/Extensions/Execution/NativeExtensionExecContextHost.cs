using System.Collections.Immutable;
using PiSharp.Extensions;
using PiSharp.Extensions.Facade.Execution;
namespace PiSharp.Cli.Extensions.Execution;
// Constructed only by actual native loader composition; no session or registry replacement.
internal sealed class NativeExtensionExecContextHost(NativeExtensionContextFacadeHost reads,
    Func<NativeExtensionExecInstallation?> installation) : IExtensionContextExecHost
{
    internal NativeExtensionExecOriginals Originals { get; } = new();
    public Task<ExtensionExecResult> ExecAsync(IExtensionCommandContext context, string command,
        ImmutableArray<string> arguments, ExtensionExecOptions? options = null)
    {
        var exactInstallation = installation() ?? throw new NotSupportedException("No admitted execution installation.");
        var exactHost = exactInstallation.ResolveContext(context);
        var attachment = reads.CaptureExecutionAttachment(context);
        void Validate()
        {
            attachment.Validate();
            if (!ReferenceEquals(exactInstallation, installation()) ||
                !ReferenceEquals(exactHost, exactInstallation.ResolveContext(context)))
                throw new InvalidOperationException("Execution owner changed before effect or result admission.");
        }
        Validate();
        var admittedOptions = (options ?? new()) with { Cwd = options?.Cwd ?? reads.GetCwd(context) };
        return RunAsync(); // explicit bridge mapping; not the underlying process Task
        async Task<ExtensionExecResult> RunAsync()
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(admittedOptions.Signal,
                context.OperationCancellationToken, context.SessionCancellationToken,
                context.ExtensionLifetimeCancellationToken, attachment.Lifetime);
            Task<ExtensionExecResult>? guarded = null;
            try
            {
                Validate(); linked.Token.ThrowIfCancellationRequested();
                guarded = exactHost.ExecGuardedAsync(command, arguments, admittedOptions with { Signal = linked.Token }, Validate);
                var result = await guarded.ConfigureAwait(false);
                Originals.Record("actual-context-guarded-exec", guarded, null);
                Validate(); return result;
            }
            catch (Exception error)
            {
                if (guarded is not null)
                {
                    var retained = Originals.Record("actual-context-guarded-exec", guarded, error);
                    if (retained is not null) throw retained;
                    // A successful original stays successful even if later attachment admission fails.
                    throw new NativeExtensionExecFault("context result admission", guarded, error);
                }
                throw new NativeExtensionExecFault("synchronous context execution", null, error);
            }
        }
    }
}