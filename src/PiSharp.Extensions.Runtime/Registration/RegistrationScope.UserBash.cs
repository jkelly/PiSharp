namespace PiSharp.Extensions.Runtime;

public sealed partial class RegistrationScope : IExtensionUserBashRegistry, IExtensionEventHandlerRegistry
{
    public IExtensionRegistration RegisterEventHandler(ExtensionEventHandlerDescriptor descriptor) => Registry.Register(this, descriptor);
    public IExtensionRegistration RegisterUserBashHandler(ExtensionUserBashHandlerDescriptor descriptor) => Registry.Register(this, descriptor);
}
