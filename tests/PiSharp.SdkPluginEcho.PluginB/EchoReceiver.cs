using PiSharp.Extensions;

namespace PiSharp.SdkPluginEcho.PluginB;

public sealed class EchoReceiver : IPiSharpExtension
{
    public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bus = (IAsyncExtensionEventBus)((IExtensionEventBusRegistry)registry).Events;
        bus.OnAsync("sdk-echo:request", data =>
        {
            var payload = (Dictionary<string, object?>)data!;
            payload["receiverAssembly"] = typeof(EchoReceiver).Assembly;
            payload["received"] = data;
            payload["count"] = (int)payload["count"]! + 1;
            payload["mutation"] = "changed-by-real-receiver";
            ((List<string>)payload["order"]!).Add("receiver-mutate");
            bus.Emit("sdk-echo:return", data);
            var original = (Task)payload["listenerOriginal"]!;
            ((Action<Task>)payload["captureListener"]!)(original);
            return original; // Actual held/completed user original, never a detached wrapper.
        }); // Owner retirement must withdraw this physical listener without user handle disposal.
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
