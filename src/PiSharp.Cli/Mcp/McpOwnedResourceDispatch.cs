using System.Collections.Immutable;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Mcp.Resources;
using PiSharp.Extensions.Mcp.Runtime;
using PiSharp.Extensions.Runtime;
using PiSharp.Extensions.Runtime.Mcp.Resources;

namespace PiSharp.Cli.Mcp;

public sealed record McpOwnedResourceFailureEvidence(McpInvocationIdentity Invocation,
    ImmutableArray<McpResourceCallbackException> Failures);

/// <summary>Prepared resource callbacks bound to one actual attachment and scope. Registration and
/// durable publication remain in the existing host catalog pipeline. That prepared invocation owner
/// joins the entire leaf operation, including output saves, before scope/session retirement.</summary>
public sealed class McpOwnedResourceDispatch
{
    public const int MaximumRetainedFaultedInvocations = 128;
    private readonly Func<IExtensionToolInvocationContext, IReadOnlyList<McpResourceServer>> capture;
    private readonly McpResourceOutputSaver saver;
    private readonly McpResourceLimits? limits;
    private readonly object gate = new();
    private readonly AsyncLocal<bool> inside = new();
    private readonly List<McpOwnedResourceFailureEvidence> failures = [];
    private ReplaceableAgentSession? owner;
    private AgentSessionAttachment? attachment;
    private RegistrationScope? scope;
    private int active;
    public McpOwnedResourceDispatch(Func<IExtensionToolInvocationContext, IReadOnlyList<McpResourceServer>> capture,
        McpResourceOutputSaver admittedSaver, McpResourceLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(capture); ArgumentNullException.ThrowIfNull(admittedSaver);
        if (capture.GetInvocationList().Length != 1 || admittedSaver.GetInvocationList().Length != 1)
            throw new ArgumentException("One captured server provider and one joined output saver required.");
        this.capture = capture; saver = admittedSaver; this.limits = limits;
        _ = new McpResourceTools(() => [], saver, limits); // Validate bounded options before registration.
    }
    public void Bind(ReplaceableAgentSession actualOwner, AgentSessionAttachment actualAttachment, RegistrationScope actualScope)
    {
        ArgumentNullException.ThrowIfNull(actualOwner); ArgumentNullException.ThrowIfNull(actualAttachment); ArgumentNullException.ThrowIfNull(actualScope);
        if (inside.Value) throw new InvalidOperationException("Resource callback cannot bind its own owner.");
        actualAttachment.LifetimeToken.ThrowIfCancellationRequested(); actualScope.ExtensionLifetimeCancellationToken.ThrowIfCancellationRequested();
        if (!ReferenceEquals(actualOwner.Current, actualAttachment)) throw new InvalidOperationException("Resource dispatch requires the actual current attachment.");
        lock (gate)
        {
            if (owner is not null) throw new InvalidOperationException("Resource dispatch is already bound.");
            owner = actualOwner; attachment = actualAttachment; scope = actualScope;
        }
    }
    /// <summary>Ordinary resource descriptors. Their names do not mint codemode/tool_search identity.</summary>
    public ImmutableArray<ExtensionToolDescriptor> CreateDescriptors(string registrationPrefix, ToolExposure exposure = ToolExposure.Direct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registrationPrefix);
        if (registrationPrefix.Length + 1 + McpResourceTools.ListTemplates.Length > 128 ||
            registrationPrefix.Any(character => !(character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-')))
            throw new ArgumentException("A bounded native registration identifier prefix required.", nameof(registrationPrefix));
        if (!Enum.IsDefined(exposure) || exposure == ToolExposure.Hidden) throw new ArgumentException("Visible prepared resource exposure required.", nameof(exposure));
        // resources.ts LIST_PARAMETERS and READ_PARAMETERS.
        var list = JsonData.Parse("""{"type":"object","properties":{"server":{"type":"string","description":"MCP server name. Omit to list every server with resources."},"cursor":{"type":"string","description":"Opaque cursor from a previous call with the same server; omit for the first page."}},"additionalProperties":false}""");
        var read = JsonData.Parse("""{"type":"object","properties":{"server":{"type":"string","description":"MCP server name exactly as configured. Must match the 'server' field returned by list_mcp_resources."},"uri":{"type":"string","description":"Resource URI to read. Must be one of the URIs returned by list_mcp_resources."}},"required":["server","uri"],"additionalProperties":false}""");
        ExtensionToolDescriptor Descriptor(string name, string description, JsonData schema) => new(
            registrationPrefix + "." + name, name, description, schema,
            (arguments, context, token) => new(ExecutePreparedAsync(name, arguments, context, token)))
            { Exposure = exposure, DefaultActive = exposure is ToolExposure.Direct or ToolExposure.ModelOnly };
        // resources.ts createMcpResourceToolDefinitions descriptions.
        return [Descriptor(McpResourceTools.ListResources, "Lists resources provided by MCP servers. Resources allow servers to share data that provides context to language models, such as files, database schemas, or application-specific information. Prefer resources over web search when possible.", list),
            Descriptor(McpResourceTools.ListTemplates, "Lists resource templates provided by MCP servers. Parameterized resource templates allow servers to share data that takes parameters and provides context to language models, such as files, database schemas, or application-specific information. Prefer resource templates over web search when possible.", list),
            Descriptor(McpResourceTools.ReadResource, "Read a specific resource from an MCP server given the server name and resource URI.", read)];
    }
    private async Task<JsonData> ExecutePreparedAsync(string tool, JsonData arguments, IExtensionToolContext context, CancellationToken token)
    {
        if (context is not IExtensionToolInvocationContext invocation) throw new InvalidOperationException("Resource execution requires the actual prepared invocation.");
        var result = await ExecuteAsync(tool, arguments, invocation, token).ConfigureAwait(false);
        return result.ToToolResult();
    }
    public async Task<McpResourceResult> ExecuteAsync(string tool, JsonData arguments, IExtensionToolInvocationContext invocation,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        if (inside.Value) throw new InvalidOperationException("Resource callback cannot reenter its owning dispatch.");
        Validate(invocation); token.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (active + failures.Count >= MaximumRetainedFaultedInvocations)
                throw new InvalidOperationException("Resource fault evidence must be transferred before further admission.");
            active++;
        }
        var previous = inside.Value; inside.Value = true;
        Task<McpResourceResult>? original = null;
        try
        {
            var leaf = new McpResourceTools(() =>
            {
                Validate(invocation);
                // Capture is a synchronous borrowed callback, with no canceled Task original.
                // A thrown OCE is consequently fault evidence, before an async leaf can convert
                // that exception into its own canceled Task state.
                try { return capture(invocation); }
                catch (Exception failure) { throw new McpResourceCallbackException("server capture", null, failure); }
            }, saver, limits);
            original = leaf.ExecuteAsync(tool, arguments, invocation, token);
            var result = await original.ConfigureAwait(false);
            if (!result.OriginalCallbackFailures.IsEmpty)
            {
                var identity = new McpInvocationIdentity(invocation.OwnerId, invocation.OwnerGeneration, invocation.SessionGeneration, invocation.ToolCallId, invocation.ParentToolCallId);
                lock (gate) failures.Add(new(identity, result.OriginalCallbackFailures));
            }
            return result;
        }
        catch (Exception error)
        {
            // Prepared executors may convert the terminal exception to a model error. Transfer
            // native originals here, before that conversion, while this admission still owns them.
            // The leaf proves physical cancellation provenance. Its canceled original is distinct
            // from faulted OCEs, which it wraps as faults; retained prior faults also make it faulted.
            if (original?.IsCanceled != true)
            {
                Exception evidence = original?.Exception ?? error;
                var identity = new McpInvocationIdentity(invocation.OwnerId, invocation.OwnerGeneration, invocation.SessionGeneration, invocation.ToolCallId, invocation.ParentToolCallId);
                // Keep the entire terminal aggregate, including nested callback wrappers carrying
                // their physical originals and any sibling non-callback failures.
                lock (gate) failures.Add(new(identity, [new("dispatch", original, evidence)]));
            }
            throw;
        }
        finally { inside.Value = previous; lock (gate) active--; }
    }
    /// <summary>Transfer native callback evidence to its host owner; model/script payloads do not serialize exceptions.</summary>
    public ImmutableArray<McpOwnedResourceFailureEvidence> TakeOriginalCallbackFailures()
    {
        if (inside.Value) throw new InvalidOperationException("Resource callback cannot transfer its owning evidence.");
        lock (gate) { var result = failures.ToImmutableArray(); failures.Clear(); return result; }
    }
    private void Validate(IExtensionToolInvocationContext invocation)
    {
        ReplaceableAgentSession? currentOwner; AgentSessionAttachment? current; RegistrationScope? currentScope;
        lock (gate) { currentOwner = owner; current = attachment; currentScope = scope; }
        if (currentOwner is null || current is null || currentScope is null || !ReferenceEquals(currentOwner.Current, current) ||
            current.LifetimeToken.IsCancellationRequested || currentScope.ExtensionLifetimeCancellationToken.IsCancellationRequested ||
            invocation.OwnerId != currentScope.OwnerId || invocation.OwnerGeneration != currentScope.OwnerGeneration ||
            invocation.SessionGeneration != current.Generation)
            throw new InvalidOperationException("Resource dispatch is unbound or belongs to a stale owner/session generation.");
    }
}
