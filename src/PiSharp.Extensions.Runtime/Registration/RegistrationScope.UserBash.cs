namespace PiSharp.Extensions.Runtime;

public sealed partial class RegistrationScope : IExtensionUserBashRegistry
{
    public IExtensionRegistration RegisterUserBashHandler(ExtensionUserBashHandlerDescriptor descriptor) => Registry.Register(this, descriptor);
}
