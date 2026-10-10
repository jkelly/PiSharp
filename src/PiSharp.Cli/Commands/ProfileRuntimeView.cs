using PiSharp.Agent;
using PiSharp.Cli.Extensions;
using PiSharp.Cli.Prompts;
using PiSharp.Cli.Skills;
using PiSharp.CodingAgent;
using PiSharp.CodingAgent.Resources;
using PiSharp.CodingAgent.Resources.Skills;
using PiSharp.Contracts;
using PiSharp.Rpc.Protocol;

namespace PiSharp.Cli.Commands;

/// <summary>One immutable metadata capture. Navigation may share its activation lifetime;
/// reload must prepare a distinct capture and lifetime before publication.</summary>
internal sealed class ProfileRuntimeView(NativeExtensionActivation? extension, PromptTemplateCliBinding? prompts,
    SkillCliBinding? skills, Func<SkillDiagnostic, CancellationToken, ValueTask>? report,
    ProfileViewLifetime lifetime, SessionRuntimeRegistry nativeRegistry)
{
    internal NativeExtensionActivation? Extension { get; } = extension;
    // A Pi reload (agent-session.ts reload) replaces the prompt templates and skills of the current view in place.
    internal PromptTemplateCliBinding? Prompts { get; set; } = prompts;
    internal SkillCliBinding? Skills { get; set; } = skills;
    internal ProfileViewLifetime Lifetime { get; } = lifetime;
    internal SessionRuntimeRegistry NativeRegistry { get; } = nativeRegistry;
    private Func<SkillDiagnostic, CancellationToken, ValueTask>? Report { get; } = report;
    internal JsonData CommandCatalog => Skills?.Commands(Prompts?.CommandCatalog ?? (IRpcExtensionCommandCatalog?)Extension).CommandCatalog ??
        Prompts?.CommandCatalog.CommandCatalog ?? Extension?.CommandCatalog ?? JsonData.Parse("[]");
    internal IPromptInputAdmission? Admission(string type)
    {
        var operation = type switch
        {
            "prompt" => PromptTemplateInputOperation.Prompt,
            "steer" => PromptTemplateInputOperation.Steer,
            "follow_up" => PromptTemplateInputOperation.FollowUp,
            _ => throw new ArgumentException("Unsupported prompt operation.", nameof(type))
        };
        return Skills?.AdmissionFor(operation, Prompts?.Templates.Catalog, Extension?.RawInputHandlers, Extension, Report) ??
            Prompts?.AdmissionFor(operation) ?? Extension?.InputAdmission;
    }
}

/// <summary>One activation disposal original, shared only by actual navigation runtime holds.</summary>
internal sealed class ProfileViewLifetime(IAsyncDisposable? resource)
{
    private readonly object gate = new();
    private int holds, users;
    private bool retired, bound;
    private TaskCompletionSource? idle;
    private Task? close;
    internal Hold Acquire()
    {
        lock (gate)
        {
            if (retired) throw new ObjectDisposedException(nameof(ProfileRuntimeView));
            holds++; return new(this);
        }
    }
    internal IDisposable Enter()
    {
        lock (gate)
        {
            if (retired) throw new ObjectDisposedException(nameof(ProfileRuntimeView));
            users++; return new Use(this);
        }
    }
    internal void BindOnce(Action bind)
    {
        lock (gate) { if (bound) return; bound = true; }
        bind();
    }
    private void Exit()
    { lock (gate) { if (--users == 0) idle?.TrySetResult(); } }
    private Task Release()
    {
        lock (gate)
        {
            if (--holds != 0) return Task.CompletedTask;
            retired = true;
            var drain = users == 0 ? Task.CompletedTask : (idle ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            return close ??= CloseAsync(drain);
        }
    }
    private async Task CloseAsync(Task drain)
    {
        await Task.Yield();
        // Start activation retirement before draining captured users: their callbacks
        // may need its cancellation signal before they can return their view hold.
        var original = resource is null ? Task.CompletedTask : ProfileViewOriginal.Join(resource);
        var failures = new List<Exception>();
        try { await original.ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
        try { await drain.ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
        if (failures.Count != 0) throw new AggregateException(failures);
    }
    internal sealed class Hold(ProfileViewLifetime owner) : IAsyncDisposable
    {
        private readonly object gate = new();
        private Task? close;
        public ValueTask DisposeAsync() { lock (gate) return new(close ??= owner.Release()); }
    }
    private sealed class Use(ProfileViewLifetime owner) : IDisposable
    { private int ended; public void Dispose() { if (Interlocked.Exchange(ref ended, 1) == 0) owner.Exit(); } }
}

internal sealed class ProfileViewOriginalException(Task? original, Exception cause)
    : IOException("Profile view original cleanup failed.", original?.Exception ?? cause)
{
    internal Task? Original { get; } = original;
    internal bool OriginalIsCanceled => Original?.IsCanceled == true;
}
internal static class ProfileViewOriginal
{
    internal static async Task Join(IAsyncDisposable resource)
    {
        Task original;
        try { original = resource.DisposeAsync().AsTask(); }
        catch (Exception error) { throw new ProfileViewOriginalException(null, error); }
        try { await original.ConfigureAwait(false); }
        catch (Exception error) { throw new ProfileViewOriginalException(original, error); }
    }
}

/// <summary>The exact combined native/view owner transferred to one runtime lease.</summary>
internal sealed class ProfileRuntimeViewOwnership : IAsyncDisposable
{
    private readonly OfflineSessionProfile profile;
    private readonly IAsyncDisposable? native;
    private readonly ProfileViewLifetime.Hold hold;
    private readonly Func<ProfileRuntimeView> capture;
    private readonly long generation;
    private readonly object gate = new();
    private readonly AsyncLocal<bool> inside = new();
    private Task? close;
    private bool bound;
    private Action? retire;
    internal ProfileRuntimeViewOwnership(OfflineSessionProfile profile, IAsyncDisposable? native,
        ProfileViewLifetime.Hold hold, Func<ProfileRuntimeView> capture, long generation, Action? retire = null)
    { this.profile = profile; this.native = native; this.hold = hold; this.capture = capture; this.generation = generation; this.retire = retire; }
    internal IAsyncDisposable Resources => this;
    internal void BindOwner(ReplaceableAgentSession owner, AgentSessionAttachment attachment)
    {
        lock (gate)
        {
            if (bound || close is not null || attachment.Generation != generation)
                throw new InvalidOperationException("View binding requires its exact unreleased generation.");
            bound = true;
        }
        var view = capture();
        retire = () => profile.ForgetRuntimeView(attachment, view);
        profile.BindRuntimeView(owner, attachment, view);
    }
    public ValueTask DisposeAsync()
    {
        if (inside.Value) throw new InvalidOperationException("View cleanup cannot join itself.");
        lock (gate) return new(close ??= CloseAsync());
    }
    private async Task CloseAsync()
    {
        await Task.Yield(); inside.Value = true;
        using var callback = profile.EnterViewCleanupCallback();
        var failures = new List<Exception>();
        try
        {
            var nativeOriginal = native is null ? Task.CompletedTask : ProfileViewOriginal.Join(native);
            var viewOriginal = ProfileViewOriginal.Join(hold);
            try { await nativeOriginal.ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
            try { await viewOriginal.ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
        }
        finally { retire?.Invoke(); inside.Value = false; }
        if (failures.Count != 0) throw new AggregateException("Native and profile-view cleanup failed.", failures);
    }
}
