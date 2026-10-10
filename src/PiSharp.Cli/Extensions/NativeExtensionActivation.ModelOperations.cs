using PiSharp.AI.ModelOperations;

namespace PiSharp.Cli.Extensions;

internal sealed partial class NativeExtensionActivation
{
    internal void ConfigureModelOperations(ModelOperationsRegistry registry) => _facadeHost.ConfigureModelOperations(registry);
    internal void BindRunModelOperations(ModelOperationsRegistry registry) => _facadeHost.BindRunModelOperations(registry);
}
