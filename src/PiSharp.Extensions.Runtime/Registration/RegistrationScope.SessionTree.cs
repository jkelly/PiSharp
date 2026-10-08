namespace PiSharp.Extensions.Runtime;

public sealed partial class RegistrationScope
{
    public IExtensionRegistration RegisterSessionBeforeTreeHandler(ExtensionSessionBeforeTreeHandlerDescriptor descriptor)
        => Registry.Register(this,descriptor);
}
