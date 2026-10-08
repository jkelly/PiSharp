using PiSharp.Contracts;
using PiSharp.Extensions;

namespace PiSharp.SdkPluginEcho.PluginA;

/// <summary>Approved fixture only: the host explicitly admits one BCL dictionary through AppContext.</summary>
public sealed class EchoSender : IPiSharpExtension
{
    public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bus = ((IExtensionEventBusRegistry)registry).Events;
        bus.On("sdk-echo:return", data =>
        {
            var payload = (Dictionary<string, object?>)data!;
            payload["returned"] = data;
            ((List<string>)payload["order"]!).Add("sender-return");
        }); // Deliberately do not retain/dispose the user subscription handle.
        registry.RegisterCommand(new("sdk-echo-send", "sdk-echo-send", "Fixture object echo", (arguments, context, token) =>
        {
            token.ThrowIfCancellationRequested();
            var payload = (Dictionary<string, object?>)(AppContext.GetData("PiSharp.SdkPluginEcho.Payload")
                ?? throw new InvalidOperationException("Explicit fixture payload not admitted."));
            payload["senderAssembly"] = typeof(EchoSender).Assembly;
            ((List<string>)payload["order"]!).Add("sender-emit");
            bus.Emit("sdk-echo:request", payload);
            return ValueTask.CompletedTask;
        }));
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
