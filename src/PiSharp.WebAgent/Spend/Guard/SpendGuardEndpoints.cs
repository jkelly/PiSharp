// Decision 0011 (run W3, S3): the Guard's JSON API under /api/spend, for the composer's estimate and the confirm modal (S5 on the chat pages,
// W4 on Playground and Compare). POST /api/spend/preflight answers "what would happen if I sent this now" without reserving anything: the
// estimate (with the chip, the worst case worked out, the tier and output notes), the verdict and its causes (each with its confirm-modal
// pane), the warnings, the output cap that would fit, the user's unused approval of that run when there is one, and, on request, cheaper
// models as suggestions (never switched to). POST /api/spend/approvals records the user's one-time answer (proceed once, or cap the
// output) for the run's next stopped call; GET /api/spend/approvals?run= lists the unused ones. GET /api/spend/stops/{op} answers the
// decision on a call the guard stopped inside a run (its error message names the op), GET /api/spend/stops?run= the run's latest stops.
// Writes are POSTs with JSON bodies (HostGuard's cross-origin and JSON-only rules cover them); every answer is Cache-Control: no-store
// and written with SettingsStore.JsonOptions (camelCase, enums by wire name). Errors are {error: {code, message}} with 400 (invalid) or
// 404 (not_found), as the Pages unit's /api/spend answers. Nothing here is a secret: ids, amounts, and the request's size, never its text.
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Credentials;
using PiSharp.WebAgent.Data;

namespace PiSharp.WebAgent.Spend;

/// <summary>Maps the Guard's part of <c>/api/spend</c>.</summary>
public static class SpendGuardEndpoints
{
    /// <summary>The route prefix (shared with the Pages unit's endpoints).</summary>
    public const string Prefix = "/api/spend";

    /// <summary>The most cheaper models a preflight suggests.</summary>
    public const int CheaperCount = 3;

    /// <summary>Maps <c>preflight</c>, <c>approvals</c> and <c>stops</c> under <see cref="Prefix"/>.</summary>
    public static IEndpointRouteBuilder MapSpendGuardEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var api = routes.MapGroup(Prefix);
        api.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context).ConfigureAwait(false);
        });

        api.MapPost("/preflight", (SpendPreflightBody? body, SpendGuard guard, IServiceProvider services, CancellationToken token) =>
            RunAsync(async () => Json(await PreflightAsync(guard, Required(body).ToCall(), body!.Cheaper == true, services, token).ConfigureAwait(false))));
        api.MapPost("/approvals", (SpendApprovalBody? body, SpendGuard guard, CancellationToken token) =>
            RunAsync(async () => Json(new SpendApprovalList([SpendApprovalAnswer.From(await guard.ApproveStoredAsync(Required(body).ToApproval(), token).ConfigureAwait(false))]))));
        api.MapGet("/approvals", (string? run, SpendGuard guard, CancellationToken token) =>
            RunAsync(async () =>
            {
                if (string.IsNullOrWhiteSpace(run)) throw new ArgumentException("Name the run (?run=).");
                var waiting = await guard.Approvals.WaitingAsync(run.Trim(), token).ConfigureAwait(false);
                return Json(new SpendApprovalList([.. waiting.Select(SpendApprovalAnswer.From)]));
            }));
        api.MapGet("/stops/{op}", (string op, SpendGuard guard, CancellationToken token) =>
            RunAsync(async () => await guard.StoppedAsync(op, token).ConfigureAwait(false) is { } stop
                ? Json(SpendStopAnswer.From(stop)) : Error(StatusCodes.Status404NotFound, SpendErrors.NotFound, $"No stopped call {op}.")));
        api.MapGet("/stops", (string? run, int? count, SpendGuard guard, CancellationToken token) =>
            RunAsync(async () =>
            {
                if (string.IsNullOrWhiteSpace(run)) throw new ArgumentException("Name the run (?run=).");
                var stops = await guard.Approvals.StopsAsync(run.Trim(), Math.Clamp(count ?? 20, 1, 100), token).ConfigureAwait(false);
                return Json(new SpendStopList([.. stops.Select(SpendStopAnswer.From)]));
            }));
        return routes;
    }

    /// <summary>The preflight answer for <paramref name="call"/>: <see cref="SpendGuard.PreflightAsync"/> (nothing reserved), the run's unused
    /// approval, and, when <paramref name="cheaper"/> and the call is not allowed as it is, up to <see cref="CheaperCount"/> cheaper models
    /// that fit what is left. Server-side callers (S5's chat pages) use it as the endpoint does.</summary>
    public static async Task<SpendPreflightAnswer> PreflightAsync(SpendGuard guard, SpendCall call, bool cheaper, IServiceProvider? services = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(call);
        var decision = await guard.PreflightAsync(call, cancellationToken).ConfigureAwait(false);
        var approval = await guard.Approvals.PeekAsync(call.Run, call.Op, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<SpendEstimate> suggestions = [];
        if (cheaper && !decision.Allowed && guard.Estimator is CostEstimator estimator)
        {
            var within = decision.Causes.Where(cause => cause.LeftMicros is not null).Select(cause => cause.LeftMicros!.Value).DefaultIfEmpty().Min();
            suggestions = estimator.Cheaper(call.EstimateInput, decision.Causes.Any(cause => cause.LeftMicros is not null) ? within : null, CheaperCount);
        }
        var models = services?.GetService<IModelCatalog>();
        return SpendPreflightAnswer.From(decision, approval, suggestions, (provider, model) => Name(models, provider, model));
    }

    private static string? Name(IModelCatalog? catalog, string provider, string model)
    {
        try
        {
            return catalog?.FindModel(provider, model)?.Name;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return null;
        }
    }

    private static T Required<T>(T? body) where T : class => body ?? throw new ArgumentException("Send a JSON body.");

    private static IResult Json(object value, int status = StatusCodes.Status200OK) => Results.Json(value, SettingsStore.JsonOptions, statusCode: status);

    private static IResult Error(int status, string code, string message) => Json(new SpendErrorBody(new SpendErrorText(code, SecretText.Scrub(message))), status);

    private static async Task<IResult> RunAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (SpendException error)
        {
            return Error(error.Status, error.Code, error.Message);
        }
        catch (ArgumentException error)
        {
            return Error(StatusCodes.Status400BadRequest, SpendErrors.Invalid, error.Message);
        }
        catch (KeyNotFoundException error)
        {
            return Error(StatusCodes.Status404NotFound, SpendErrors.NotFound, error.Message);
        }
    }

    private sealed record SpendErrorBody(SpendErrorText Error);

    private sealed record SpendErrorText(string Code, string Message);
}

/// <summary>POST /api/spend/preflight: the call to check. Only the request's size is sent, never its text: <see cref="InputBytes"/> (the
/// whole request: system prompt, transcript, tools) plus the UTF-8 size of <see cref="Text"/> (the draft) when given.</summary>
/// <param name="Provider">The provider id.</param>
/// <param name="Model">The model id.</param>
/// <param name="Account">The account id, or null.</param>
/// <param name="Source">The source's wire name (<c>chat</c> when null).</param>
/// <param name="Run">The run key (the chat id), or null for a chat not created yet.</param>
/// <param name="Op">The op of a call stopped inside a run, or null.</param>
/// <param name="InputBytes">The request's size in UTF-8 bytes, without the draft.</param>
/// <param name="Text">The draft, counted by its UTF-8 size only (not stored, not logged), or null.</param>
/// <param name="MaxTokens">The request's <c>maxTokens</c> (or the output cap the user is about to choose), or null.</param>
/// <param name="Approved">Whether the user approved it once (Ask causes pass; Block still blocks).</param>
/// <param name="Cheaper">Whether to suggest cheaper models when it is not allowed.</param>
public sealed record SpendPreflightBody(string? Provider, string? Model, string? Account, string? Source, string? Run, string? Op, long? InputBytes,
    string? Text, int? MaxTokens, bool? Approved, bool? Cheaper)
{
    /// <summary>The call this body describes.</summary>
    /// <exception cref="ArgumentException">A value is not allowed.</exception>
    public SpendCall ToCall()
    {
        if (InputBytes is < 0) throw new ArgumentException("inputBytes is never negative.");
        var bytes = (InputBytes ?? 0) + (string.IsNullOrEmpty(Text) ? 0 : System.Text.Encoding.UTF8.GetByteCount(Text));
        return new SpendCall
        {
            Provider = Provider?.Trim() ?? "",
            Model = Model?.Trim() ?? "",
            Account = string.IsNullOrWhiteSpace(Account) ? null : Account.Trim(),
            Source = string.IsNullOrWhiteSpace(Source) ? SpendSource.Chat : SpendSources.Parse(Source),
            Run = string.IsNullOrWhiteSpace(Run) ? null : Run.Trim(),
            Op = Op?.Trim() ?? "",
            InputBytes = bytes,
            MaxTokens = MaxTokens,
            Approved = Approved == true,
        };
    }

    /// <inheritdoc/>
    public override string ToString() => $"SpendPreflightBody ({Provider}/{Model}, {InputBytes} bytes) [draft not shown]";
}

/// <summary>POST /api/spend/approvals: the user's one-time answer.</summary>
/// <param name="Run">The run key (the chat id).</param>
/// <param name="Choice"><c>proceed-once</c> or <c>cap-output</c>.</param>
/// <param name="MaxOutputTokens">The output cap, for <c>cap-output</c>.</param>
/// <param name="Op">Only this stopped call's op, or null for the run's next stopped call.</param>
public sealed record SpendApprovalBody(string? Run, string? Choice, int? MaxOutputTokens, string? Op)
{
    /// <summary>The approval this body describes.</summary>
    /// <exception cref="ArgumentException">A value is not allowed.</exception>
    public SpendApproval ToApproval()
    {
        var choice = Choice?.Trim() switch
        {
            "proceed-once" => SpendChoice.ProceedOnce,
            "cap-output" => SpendChoice.CapOutput,
            _ => throw new ArgumentException("Choose proceed-once or cap-output."),
        };
        return new SpendApproval(Run ?? "", choice) { MaxOutputTokens = MaxOutputTokens, Op = Op }.Normalize();
    }
}

/// <summary>One cause of a preflight answer, with its confirm-modal pane.</summary>
/// <param name="Kind"><c>cap</c> or <c>unknown-price</c>.</param>
/// <param name="Action"><c>block</c> or <c>ask</c>.</param>
/// <param name="Cap">The cap, or null for an unknown price.</param>
/// <param name="SpentMicros">What already counts toward the cap in its window.</param>
/// <param name="LeftMicros">What is left, or null for an unknown price.</param>
/// <param name="FitOutputTokens">The most output tokens that fit this cap, or null.</param>
/// <param name="Pane"><c>sc-request</c>, <c>sc-chat</c>, <c>sc-period</c> or <c>sc-unknown</c>.</param>
public sealed record SpendPreflightCause(SpendCauseKind Kind, CapAction Action, SpendCap? Cap, long SpentMicros, long? LeftMicros, int? FitOutputTokens, string Pane)
{
    /// <summary>The cause as answered.</summary>
    public static SpendPreflightCause From(SpendCause cause)
    {
        ArgumentNullException.ThrowIfNull(cause);
        return new(cause.Kind, cause.Action, cause.Cap, cause.SpentMicros, cause.LeftMicros, cause.FitOutputTokens, cause.Pane);
    }
}

/// <summary>A cheaper model a preflight suggests (the user picks it; the guard never switches).</summary>
/// <param name="Provider">The provider id.</param>
/// <param name="Model">The model id.</param>
/// <param name="Name">The model's display name, or null.</param>
/// <param name="Account">The account id, or null.</param>
/// <param name="Billing">How it is billed.</param>
/// <param name="WorstMicros">Its worst case for the same request, or null.</param>
/// <param name="Chip">Its chip text (<c>≈ $0.01</c>), or null.</param>
public sealed record SpendCheaperModel(string Provider, string Model, string? Name, string? Account, BillingKind Billing, long? WorstMicros, string? Chip);

/// <summary>An approval as answered.</summary>
/// <param name="Id">Its id.</param>
/// <param name="Run">The run it is for.</param>
/// <param name="Op">The op it is for, or null for the run's next stopped call.</param>
/// <param name="Choice">Proceed once, or cap the output.</param>
/// <param name="MaxOutputTokens">The output cap, for cap-output.</param>
/// <param name="CreatedAt">When it was given (UTC Unix milliseconds).</param>
/// <param name="ExpiresAt">When it lapses unused (UTC Unix milliseconds).</param>
/// <param name="Used">Whether a stopped call used it.</param>
public sealed record SpendApprovalAnswer(long Id, string Run, string? Op, SpendChoice Choice, int? MaxOutputTokens, long CreatedAt, long ExpiresAt, bool Used)
{
    /// <summary>The stored approval as answered.</summary>
    public static SpendApprovalAnswer From(StoredApproval stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        return new(stored.Id, stored.Approval.Run, stored.Approval.Op, stored.Approval.Choice, stored.Approval.MaxOutputTokens,
            stored.Created.ToUnixTimeMilliseconds(), stored.Expires.ToUnixTimeMilliseconds(), stored.Used is not null);
    }
}

/// <summary>A list of approvals.</summary>
/// <param name="Approvals">The approvals, newest first.</param>
public sealed record SpendApprovalList(IReadOnlyList<SpendApprovalAnswer> Approvals);

/// <summary>What the guard would do with a call now (POST /api/spend/preflight), or did with a stopped one.</summary>
public sealed record SpendPreflightAnswer
{
    /// <summary><c>allow</c>, <c>ask</c> or <c>block</c>.</summary>
    public required SpendVerdict Verdict { get; init; }

    /// <summary>Whether it may be sent as it is.</summary>
    public bool Allowed { get; init; }

    /// <summary><c>spend_blocked</c>, <c>spend_ask</c> or <c>spend_unknown_price</c>; null when allowed.</summary>
    public string? Code { get; init; }

    /// <summary>Why it is stopped (<see cref="SpendErrors.Describe"/>); null when allowed.</summary>
    public string? Sentence { get; init; }

    /// <summary>Whether the user may answer it (proceed once, or cap the output): not for a Block.</summary>
    public bool CanProceed { get; init; }

    /// <summary>The first cause's confirm-modal pane, or null when allowed.</summary>
    public string? Pane { get; init; }

    /// <summary>The estimate.</summary>
    public required SpendEstimate Estimate { get; init; }

    /// <summary>The composer's chip (<c>≈ $0.03</c>, <c>≈ $0.21 list</c>, <c>price unknown</c>); null for a request that costs nothing.</summary>
    public string? Chip { get; init; }

    /// <summary>The worst case worked out, or null.</summary>
    public string? Formula { get; init; }

    /// <summary>The price-tier note, or null.</summary>
    public string? TierNote { get; init; }

    /// <summary>Where the output tokens came from.</summary>
    public string? OutputNote { get; init; }

    /// <summary>Every cause, Block first.</summary>
    public IReadOnlyList<SpendPreflightCause> Causes { get; init; } = [];

    /// <summary>Caps at or over the warning threshold with this call counted.</summary>
    public IReadOnlyList<SpendCapStatus> Warnings { get; init; } = [];

    /// <summary>The output cap with which it fits every cap it is over, or null.</summary>
    public int? FitOutputTokens { get; init; }

    /// <summary>The user's unused approval for this run (or op), which the next stopped call will use; null when there is none.</summary>
    public SpendApprovalAnswer? Approval { get; init; }

    /// <summary>Cheaper models that fit, as suggestions.</summary>
    public IReadOnlyList<SpendCheaperModel> Cheaper { get; init; } = [];

    /// <summary>The answer for <paramref name="decision"/>.</summary>
    public static SpendPreflightAnswer From(SpendDecision decision, StoredApproval? approval = null, IReadOnlyList<SpendEstimate>? cheaper = null,
        Func<string, string, string?>? names = null)
    {
        ArgumentNullException.ThrowIfNull(decision);
        var estimate = decision.Estimate;
        var name = names?.Invoke(estimate.Provider, estimate.Model);
        return new SpendPreflightAnswer
        {
            Verdict = decision.Verdict,
            Allowed = decision.Allowed,
            Code = decision.ErrorCode,
            Sentence = decision.Allowed ? null : SpendErrors.Describe(decision),
            CanProceed = !decision.Allowed && decision.Verdict != SpendVerdict.Block,
            Pane = decision.Allowed ? null : decision.Causes.FirstOrDefault()?.Pane,
            Estimate = estimate,
            Chip = SpendEstimateText.Chip(estimate),
            Formula = SpendEstimateText.Formula(estimate),
            TierNote = SpendEstimateText.TierNote(estimate, name),
            OutputNote = SpendEstimateText.OutputNote(estimate, name),
            Causes = [.. decision.Causes.Select(SpendPreflightCause.From)],
            Warnings = decision.Warnings,
            FitOutputTokens = decision.FitOutputTokens,
            Approval = approval is null ? null : SpendApprovalAnswer.From(approval),
            Cheaper = [.. (cheaper ?? []).Select(model => new SpendCheaperModel(model.Provider, model.Model, names?.Invoke(model.Provider, model.Model), model.Account,
                model.Billing, model.WorstMicros, SpendEstimateText.Chip(model)))],
        };
    }
}

/// <summary>A call the guard stopped (GET /api/spend/stops/{op}).</summary>
/// <param name="Op">The call's op.</param>
/// <param name="Run">Its run, or null.</param>
/// <param name="Source">Its source.</param>
/// <param name="Provider">The provider id.</param>
/// <param name="Model">The model id.</param>
/// <param name="Account">The account id, or null.</param>
/// <param name="InputBytes">The request's size.</param>
/// <param name="MaxTokens">The request's own <c>maxTokens</c>, or null.</param>
/// <param name="CreatedAt">When it was stopped (UTC Unix milliseconds).</param>
/// <param name="Decision">The decision, as a preflight answers it.</param>
public sealed record SpendStopAnswer(string Op, string? Run, SpendSource Source, string Provider, string Model, string? Account, long InputBytes, int? MaxTokens,
    long CreatedAt, SpendPreflightAnswer Decision)
{
    /// <summary>The stop as answered.</summary>
    public static SpendStopAnswer From(SpendStop stop)
    {
        ArgumentNullException.ThrowIfNull(stop);
        var call = stop.Call;
        return new(stop.Op, call.Run, call.Source, call.Provider, call.Model, call.Account, call.InputBytes, call.MaxTokens, stop.Created.ToUnixTimeMilliseconds(),
            SpendPreflightAnswer.From(stop.Decision));
    }
}

/// <summary>A list of stopped calls.</summary>
/// <param name="Stops">The stops, newest first.</param>
public sealed record SpendStopList(IReadOnlyList<SpendStopAnswer> Stops);
