// PiSharp-only (decision 0008): no upstream counterpart. The codes are durable-pi's v1 error codes, so a device server maps each exception
// to its envelope without reading messages (durable-pi classifies some errors by matching their text).
namespace PiSharp.Durable.Hosting;

/// <summary>A host operation failed. <see cref="Code"/> is the device API's error code: <c>not_found</c>, <c>ambiguous</c>,
/// <c>invalid</c>, <c>busy</c> or <c>unavailable</c>.</summary>
public class DurableHostException(string code, string message, Exception? innerException = null) : Exception(message, innerException)
{
    /// <summary>The device API's error code.</summary>
    public string Code { get; } = code;
}

/// <summary>No session, agent, directory or entry matches the address (<c>not_found</c>).</summary>
public sealed class HostNotFoundException(string message) : DurableHostException("not_found", message);

/// <summary>A name matches more than one session or agent (<c>ambiguous</c>).</summary>
public sealed class HostAmbiguousException(string message) : DurableHostException("ambiguous", message);

/// <summary>A request is malformed: an unknown model, a bad name, a missing directory or an out-of-range setting (<c>invalid</c>).</summary>
public sealed class HostInvalidException(string message) : DurableHostException("invalid", message);

/// <summary>A message sent with <see cref="WhenBusy.Reject"/> found the agent busy; nothing was admitted (<c>busy</c>).</summary>
public sealed class HostBusyException(string message, Exception innerException) : DurableHostException("busy", message, innerException);

/// <summary>The session could not be opened, or its Harness failed after opening (<c>unavailable</c>). Other sessions are not
/// affected.</summary>
public sealed class SessionUnavailableException(string message, Exception? innerException) : DurableHostException("unavailable", message, innerException);

/// <summary>Another host holds the data directory: only one process may host a device's sessions at a time.</summary>
public sealed class DeviceLockedException(string message) : IOException(message);
