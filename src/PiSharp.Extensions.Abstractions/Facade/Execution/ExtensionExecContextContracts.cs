using System.Collections.Immutable;
using PiSharp.Extensions.Facade.Context;
namespace PiSharp.Extensions.Facade.Execution;
public interface IExtensionContextExecHost
{
    Task<ExtensionExecResult> ExecAsync(IExtensionCommandContext context, string command,
        ImmutableArray<string> arguments, ExtensionExecOptions? options = null);
}
public interface IExtensionExecCommandFacade : IExtensionCommandFacade
{
    ValueTask<ExtensionExecResult> ExecAsync(string command, ImmutableArray<string> arguments, ExtensionExecOptions? options = null);
}
public interface IExtensionExecRegistrationActions : IExtensionRegistrationActions
{
    ValueTask<ExtensionExecResult> ExecAsync(string command, ImmutableArray<string> arguments, ExtensionExecOptions? options = null);
}