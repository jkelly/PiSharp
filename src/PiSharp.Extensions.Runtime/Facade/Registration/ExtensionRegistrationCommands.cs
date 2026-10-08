using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
using PiSharp.Contracts;
using PiSharp.Extensions.Facade.Execution;

namespace PiSharp.Extensions.Runtime;

/// <summary>Actual command admission owns every model/message action, including ignored returned tasks.</summary>
public static partial class ExtensionRegistrationCommands
{
    /// <summary>Resolves the action capability only through the internal, exact-frame native host seam.</summary>
    public static ExtensionCommandDescriptor Create(string id, string name, string description,
        ExtensionProviderRegistrationHost providers, ExtensionRegistrationCommandCallback callback) =>
        Create(id, name, description, providers, context =>
            ((ExtensionCommandContext)context).GetFacadeHostForAdapter() as IExtensionRegistrationActionHost ??
            throw new NotSupportedException("This native host has not supplied provider/model/message actions."), callback);

    public static ExtensionCommandDescriptor Create(string id, string name, string description,
        ExtensionProviderRegistrationHost providers, IExtensionRegistrationActionHost admittedHost,
        ExtensionRegistrationCommandCallback callback) => Create(id, name, description, providers, _ => admittedHost, callback);

    /// <summary>The resolver is host-owned composition, not an extension callback. The returned host must be
    /// the capability admitted for this exact application/session generation.</summary>
    public static ExtensionCommandDescriptor Create(string id, string name, string description,
        ExtensionProviderRegistrationHost providers, Func<IExtensionCommandContext, IExtensionRegistrationActionHost> admittedHostResolver,
        ExtensionRegistrationCommandCallback callback)
    {
        ArgumentNullException.ThrowIfNull(providers); ArgumentNullException.ThrowIfNull(admittedHostResolver); ArgumentNullException.ThrowIfNull(callback);
        if (admittedHostResolver.GetInvocationList().Length != 1 || callback.GetInvocationList().Length != 1) throw new ArgumentException("Single admitted callbacks required.");
        return new(id, name, description, async (arguments, context, token) =>
        {
            if (context is not ExtensionCommandContext actual || !ReferenceEquals(actual.Scope.Registry, providers.Registry))
                throw new InvalidOperationException("Actual admitted native command context required.");
            actual.ValidateFacadeInvocation();
            IExtensionRegistrationActionHost host;
            try { host = admittedHostResolver(context) ?? throw new InvalidOperationException("No admitted registration action host."); }
            catch (OperationCanceledException error) when (token.IsCancellationRequested && error.CancellationToken == token) { throw; }
            catch (Exception error) { throw new ExtensionProviderOriginalFaultException("resolve-host", null, error); }
            var lease = new Lease(context); var previous = Lease.Current.Value; Lease.Current.Value = lease;
            Task? original = null; Exception? primary = null;
            try
            {
                original = callback(arguments, new Actions(lease, providers, host), context, token).AsTask();
                await original.ConfigureAwait(false);
            }
            catch (Exception error) { primary = Preserve(original, error, token, "command"); }
            finally { Lease.Current.Value = previous; }
            var failures = await lease.CloseAsync().ConfigureAwait(false);
            if (primary is not null && !failures.Any(x => SameOriginal(x, primary))) failures.Insert(0, primary);
            if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
            if (failures.Count > 1) throw new AggregateException("Extension command/actions failed.", failures);
        });
    }
    private static bool SameOriginal(Exception left, Exception right) => ReferenceEquals(left, right) ||
        left is ExtensionProviderOriginalFaultException a && right is ExtensionProviderOriginalFaultException b && a.Original is not null && ReferenceEquals(a.Original, b.Original);
    private static Exception Preserve(Task? original, Exception error, CancellationToken token, string phase)
    {
        if (error is ExtensionProviderOriginalFaultException or ExtensionProviderCanceledOriginalException) return error;
        if (original is { IsCanceled: true } && error is OperationCanceledException canceled && token.IsCancellationRequested && canceled.CancellationToken == token)
            return new ExtensionProviderCanceledOriginalException(original, canceled);
        return new ExtensionProviderOriginalFaultException(phase, original, original is { IsFaulted: true } ? original.Exception! : error);
    }
    private sealed record Work(Task Original, Task Mapped);
    private sealed class Lease(IExtensionCommandContext context)
    {
        internal static readonly AsyncLocal<Lease?> Current = new();
        private readonly object gate = new(); private readonly List<TaskCompletionSource<Work>> slots = [];
        private readonly List<Func<Task<List<Exception>>>> facadeCloses=[];
        private bool closed;
        internal IExtensionCommandContext Context => context;
        internal void StructuralCheck()
        {
            lock (gate) if (closed || !ReferenceEquals(Current.Value, this)) throw new InvalidOperationException("Action belongs to its active originating command.");
        }
        internal void EnrollFacadeClose(Func<Task<List<Exception>>> close)
        {
            ArgumentNullException.ThrowIfNull(close);
            lock(gate){StructuralCheck();if(facadeCloses.Count==128)throw new InvalidOperationException("Borrowed facade bound exceeded.");facadeCloses.Add(close);}
        }
        internal void Check()
        {
            StructuralCheck();
            context.OperationCancellationToken.ThrowIfCancellationRequested(); context.SessionCancellationToken.ThrowIfCancellationRequested();
            context.ExtensionLifetimeCancellationToken.ThrowIfCancellationRequested();
        }
        internal Task<T> Start<T>(Func<CancellationToken, Task<T>> invoke, CancellationToken supplied, string phase)
        {
            supplied.ThrowIfCancellationRequested();
            var slot = new TaskCompletionSource<Work>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (gate) { Check(); if (slots.Count == 128) throw new InvalidOperationException("Command action bound exceeded."); slots.Add(slot); }
            CancellationTokenSource? owned = null; Task<T>? original = null;
            try
            {
                owned = CancellationTokenSource.CreateLinkedTokenSource(supplied, context.OperationCancellationToken, context.SessionCancellationToken, context.ExtensionLifetimeCancellationToken);
                original = invoke(owned.Token) ?? throw new InvalidOperationException("Host returned no original task.");
                var mapped = Map(original, owned, phase); slot.SetResult(new(original, mapped)); return mapped;
            }
            catch (Exception error)
            {
                owned?.Dispose();
                var failure = Task.FromException<T>(Preserve(original, error, supplied, phase)); slot.SetResult(new(failure, failure)); return failure;
            }
        }
        internal Task StartVoid(Func<CancellationToken, ValueTask> invoke, CancellationToken supplied, string phase)
        {
            supplied.ThrowIfCancellationRequested();
            var slot = new TaskCompletionSource<Work>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (gate) { Check(); if (slots.Count == 128) throw new InvalidOperationException("Command action bound exceeded."); slots.Add(slot); }
            CancellationTokenSource? owned = null; Task? original = null;
            try
            {
                owned = CancellationTokenSource.CreateLinkedTokenSource(supplied, context.OperationCancellationToken, context.SessionCancellationToken, context.ExtensionLifetimeCancellationToken);
                original = invoke(owned.Token).AsTask();
                var mapped = MapVoid(original, owned, phase); slot.SetResult(new(original, mapped)); return mapped;
            }
            catch (Exception error)
            {
                owned?.Dispose(); var failure = Task.FromException(Preserve(original, error, supplied, phase)); slot.SetResult(new(failure, failure)); return failure;
            }
        }
        private static async Task MapVoid(Task original, CancellationTokenSource owned, string phase)
        {
            try { await original.ConfigureAwait(false); }
            catch (Exception error) { ExceptionDispatchInfo.Capture(Preserve(original, error, owned.Token, phase)).Throw(); throw; }
            finally { owned.Dispose(); }
        }
        private static async Task<T> Map<T>(Task<T> original, CancellationTokenSource owned, string phase)
        {
            try { return await original.ConfigureAwait(false); }
            catch (Exception error) { ExceptionDispatchInfo.Capture(Preserve(original, error, owned.Token, phase)).Throw(); throw; }
            finally { owned.Dispose(); }
        }
        internal async Task<List<Exception>> CloseAsync()
        {
            Task<Work>[] captured;Func<Task<List<Exception>>>[] closers;
            lock (gate) { closed = true; captured = slots.Select(x => x.Task).ToArray();closers=facadeCloses.ToArray(); }
            var errors = new List<Exception>();
            var closes=new List<Task<List<Exception>>>();
            foreach(var closer in closers){try{closes.Add(closer());}catch(Exception error){errors.Add(error);}}
            foreach (var slot in captured)
            {
                var work = await slot.ConfigureAwait(false);
                try { await work.Mapped.ConfigureAwait(false); }
                catch (Exception error) { errors.Add(error); }
                if (!work.Original.IsCompleted)
                {
                    errors.Add(new InvalidOperationException("Actual host action remained pending after mapped join."));
                    try{await work.Original.ConfigureAwait(false);}catch(Exception error){errors.Add(Preserve(work.Original,error,default,"unsettled-action"));}
                }
            }
            foreach(var original in closes)
            {
                try{errors.AddRange(await original.ConfigureAwait(false));}
                catch(Exception error){errors.Add(Preserve(original,error,default,"session-facade-close"));}
            }
            return errors;
        }
    }
    private sealed partial class Actions(Lease lease, ExtensionProviderRegistrationHost providers, IExtensionRegistrationActionHost host)
        : IExtensionRegistrationActions, PiSharp.Extensions.Facade.Context.IExtensionSettingsThinkingReadFacade, IExtensionExecRegistrationActions, PiSharp.Extensions.Facade.Context.IExtensionSystemPromptOptionsReadFacade, PiSharp.Extensions.Facade.Context.IExtensionSessionBehaviorFacade
    {
        public string OwnerId => lease.Context.OwnerId;
        public long OwnerGeneration => lease.Context.OwnerGeneration;
        public ValueTask<ExtensionExecResult> ExecAsync(string command, ImmutableArray<string> arguments, ExtensionExecOptions? options = null)
        {
            lease.Check();
            var execution = host as IExtensionContextExecHost ?? throw new NotSupportedException("No admitted execution context host.");
            return new(lease.Start(owned => execution.ExecAsync(lease.Context, command, arguments,
                (options ?? new()) with { Signal = owned }), options?.Signal ?? default, "exec"));
        }
        public ImmutableArray<ExtensionProviderModel> GetModels() { lease.Check(); return providers.CaptureModels(); }
        private PiSharp.Extensions.Facade.Context.IExtensionSettingsThinkingReadHost Reads =>
            ((ExtensionCommandContext)lease.Context).GetFacadeHostForAdapter() as
                PiSharp.Extensions.Facade.Context.IExtensionSettingsThinkingReadHost ??
            throw new NotSupportedException("The admitted host has no settings/thinking read binding.");
        public JsonData GetSettings()
        { lease.Check(); var result = Reads.GetSettings(lease.Context); lease.Check(); return result; }
        public string GetThinkingLevel()
        { lease.Check(); var result = Reads.GetThinkingLevel(lease.Context); lease.Check(); return result; }
        public JsonData GetSystemPromptOptions()
        {
            lease.Check();
            var reads = ((ExtensionCommandContext)lease.Context).GetFacadeHostForAdapter() as
                PiSharp.Extensions.Facade.Context.IExtensionSystemPromptOptionsReadHost ??
                throw new NotSupportedException("The admitted host has no system prompt options read binding.");
            var result = reads.GetSystemPromptOptions(lease.Context);
            lease.Check(); return result;
        }
        public ValueTask<bool> SetModelAsync(ModelDescriptor model, CancellationToken token = default)
        {
            ArgumentNullException.ThrowIfNull(model); lease.Check();
            if (!providers.CaptureModels().Any(x => x.Model == model)) throw new ArgumentException("Model is not in the admitted catalog.");
            return new(lease.Start(owned => host.SetModelAsync(model, owned).AsTask(), token, "set-model"));
        }
        public ValueTask SendMessageAsync(ExtensionCustomMessage message, ExtensionMessageOptions? options = null, CancellationToken token = default)
        {
            ArgumentNullException.ThrowIfNull(message); lease.Check();
            if (message.CustomType is not { Length: > 0 and <= 128 } || message.Content is null ||
                options?.DeliverAs is { } delivery && !Enum.IsDefined(delivery)) throw new ArgumentException("Invalid custom message.");
            Content(message.Content); if (message.Details is { } details && details.ToString().Length > 1048576) throw new ArgumentException("Message details bound exceeded.");
            return new(lease.StartVoid(owned => host.SendMessageAsync(message, options, owned), token, "send-message"));
        }
        public ValueTask SendUserMessageAsync(JsonData content, ExtensionUserMessageOptions? options = null, CancellationToken token = default)
        {
            ArgumentNullException.ThrowIfNull(content); lease.Check(); Content(content);
            if (options?.DeliverAs is { } delivery && delivery is not (ExtensionMessageDelivery.Steer or ExtensionMessageDelivery.FollowUp))
                throw new ArgumentException("User message delivery must be steer or followUp.");
            return new(lease.StartVoid(owned => host.SendUserMessageAsync(content, options, owned), token, "send-user-message"));
        }
        private static void Content(JsonData content)
        {
            if (content.ToString().Length > 1048576 || content.Value.ValueKind is not (System.Text.Json.JsonValueKind.String or System.Text.Json.JsonValueKind.Array))
                throw new ArgumentException("Message content must be bounded text or content blocks.");
            if (content.Value.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                if (content.Value.GetArrayLength() > 4096) throw new ArgumentException("Message content block bound exceeded.");
                foreach (var block in content.Value.EnumerateArray())
                {
                    if (block.ValueKind != System.Text.Json.JsonValueKind.Object || !block.TryGetProperty("type", out var type) ||
                        type.ValueKind != System.Text.Json.JsonValueKind.String || type.GetString() is not ("text" or "image")) throw new ArgumentException("Invalid message content block.");
                    if (type.GetString() == "text" && (!block.TryGetProperty("text", out var text) || text.ValueKind != System.Text.Json.JsonValueKind.String) ||
                        type.GetString() == "image" && (!block.TryGetProperty("data", out var data) || data.ValueKind != System.Text.Json.JsonValueKind.String ||
                            !block.TryGetProperty("mimeType", out var mime) || mime.ValueKind != System.Text.Json.JsonValueKind.String))
                        throw new ArgumentException("Invalid message content payload.");
                }
            }
        }
    }
}
