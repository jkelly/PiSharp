namespace PiSharp.Contracts;

/// <summary>Registration reachability, independent of final-action authorization.</summary>
public enum ToolExposure { Direct, ModelOnly, Codemode, Deferred, Hidden }

public static class ToolExposureSemantics
{
    public static bool ActivatesOnRegistration(ToolExposure exposure, bool defaultActive) =>
        defaultActive && exposure is ToolExposure.Direct or ToolExposure.ModelOnly;
    public static bool IsCallable(ToolExposure exposure, bool active) =>
        exposure is ToolExposure.Codemode or ToolExposure.Deferred || exposure == ToolExposure.Direct && active;
}
