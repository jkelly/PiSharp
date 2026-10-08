namespace PiSharp.Extensions.Facade.Context;

public enum ExtensionLifecycleHandoffKind { Shutdown, Reload }

/// <summary>Admits a request only. No retirement task is returned or impersonated.</summary>
public interface IExtensionLifecycleHandoffHost : IExtensionContextReadHost
{
    void RequestLifecycleHandoff(IExtensionCommandContext originating,
        ExtensionLifecycleHandoffKind kind, CancellationToken admissionCancellation);
}

/// <summary>Command-only post-origin request capability. Existing awaited ReloadAsync remains separate.</summary>
public interface IExtensionLifecycleHandoffFacade : IExtensionCommandFacade
{
    void RequestShutdown(CancellationToken admissionCancellation = default);
    void RequestReload(CancellationToken admissionCancellation = default);
}
