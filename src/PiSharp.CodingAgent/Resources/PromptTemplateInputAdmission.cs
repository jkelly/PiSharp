using System.Collections.Immutable;
using PiSharp.Agent;

namespace PiSharp.CodingAgent.Resources;

public enum PromptTemplateInputOperation { Prompt, Steer, FollowUp, ExtensionMessage }

/// <summary>Borrowed command boundary. The owner retains registration identity, execution and error reporting.
/// Interpret raw input with the pinned command syntax, not the template argument parser.</summary>
public interface IPromptTemplateCommandAdmission
{
    bool IsRegisteredCommand(string rawText);
    ValueTask<bool> TryExecuteAsync(string rawText, CancellationToken cancellationToken);
}

/// <summary>Operation-specific Pi command/input/template order; no routing, session, queue or callback ownership.</summary>
public sealed class PromptTemplateInputAdmission : IPromptInputAdmission
{
    private readonly ImmutableArray<PromptTemplate> templates;
    private readonly PromptTemplateInputOperation operation;
    private readonly bool expandTemplates;
    private readonly IPromptInputAdmission? inputHandlers;
    private readonly IPromptTemplateCommandAdmission? commands;
    private readonly PromptInputAdmissionOptions? limits;

    public PromptTemplateInputAdmission(PromptTemplateCatalogSnapshot snapshot, PromptTemplateInputOperation operation,
        IPromptInputAdmission? inputHandlers = null, IPromptTemplateCommandAdmission? commands = null,
        bool? expandTemplates = null, PromptInputAdmissionOptions? limits = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Resources.IsDefault || !Enum.IsDefined(operation)) throw new ArgumentException("Invalid prompt template admission configuration.");
        if ((operation is PromptTemplateInputOperation.Steer or PromptTemplateInputOperation.FollowUp) && expandTemplates is false)
            throw new ArgumentException("Pinned explicit queue inputs always expand templates.", nameof(expandTemplates));
        templates = snapshot.Resources.Select(resource => resource.Template).ToImmutableArray();
        this.operation = operation;
        this.expandTemplates = expandTemplates ?? operation != PromptTemplateInputOperation.ExtensionMessage;
        this.inputHandlers = inputHandlers; this.commands = commands; this.limits = limits;
    }

    public async ValueTask<PromptInputDecision> ReduceAsync(PromptInput input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        input = PromptInputValue.Own(input, limits);
        if (input.Text.StartsWith('/') && commands is not null)
        {
            if (operation is PromptTemplateInputOperation.Steer or PromptTemplateInputOperation.FollowUp)
            {
                var registered = commands.IsRegisteredCommand(input.Text);
                cancellationToken.ThrowIfCancellationRequested();
                if (registered) throw new PromptInputAdmissionException(PromptInputAdmissionFailure.InvalidInput);
            }
            else if (expandTemplates)
            {
                var handled = await commands.TryExecuteAsync(input.Text, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (handled) return new(PromptInputAction.Handled);
            }
        }
        var decision = inputHandlers is null ? new PromptInputDecision(PromptInputAction.Continue)
            : await inputHandlers.ReduceAsync(input, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var effective = PromptInputValue.Apply(input, decision, limits);
        if (decision.Action == PromptInputAction.Handled) return decision;
        var expanded = expandTemplates ? PromptTemplateExpander.ExpandPromptTemplate(effective.Text, templates) : effective.Text;
        cancellationToken.ThrowIfCancellationRequested();
        if (decision.Action != PromptInputAction.Transform && string.Equals(expanded, effective.Text, StringComparison.Ordinal))
            return new(PromptInputAction.Continue);
        var final = PromptInputValue.Own(effective with { Text = expanded }, limits);
        cancellationToken.ThrowIfCancellationRequested();
        return new(PromptInputAction.Transform, final.Text, final.Images);
    }
}
