using PiSharp.Contracts;

namespace PiSharp.Extensions.Facade.Context;

/// <summary>Optional exact immutable construction inputs. Not rendered prompt replay or a publisher.</summary>
public interface IExtensionSystemPromptOptionsReadHost : IExtensionContextReadHost
{
    JsonData GetSystemPromptOptions(IExtensionContext context);
}
public interface IExtensionSystemPromptOptionsReadFacade
{
    JsonData GetSystemPromptOptions();
}
