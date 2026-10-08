using PiSharp.Agent;
using PiSharp.Tools.Files;

namespace PiSharp.Cli.Commands;

/// <summary>
/// Explicit borrowed host capabilities. Registration grants no search or read authority.
/// The host retains executor/operations lifetime and original operation/cleanup settlement.
/// Search admission is responsible for the complete search scope, including returned matches.
/// Context additionally requires the profile's exact read-target grant and reserved-target checks.
/// </summary>
internal sealed record OfflineGrepHost(IGrepExecutor Executor, IFileOperations ContextOperations,
    IToolActionPolicy SearchAdmission, GrepContextReadAdmission ContextAdmission);
