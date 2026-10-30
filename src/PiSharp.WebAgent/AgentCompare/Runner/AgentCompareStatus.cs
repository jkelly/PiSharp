// Decision 0011 (run W4, R5): the status JSON of an agent compare run (GET /api/compare/agent/{run}, the SSE "status" frame, and every write's
// answer), which R6's running and results pages render. It is the stored run plus what is live: each model's turns (agent_compare_turns),
// its spend (the Spend Guard ledger's total for its run key "<run>-<slot>": counted micro-USD, including reserved requests, and a
// subscription's list cost apart), the tool calls that wait for an approval (ApprovalBroker), what it waits on ("approval" or "budget"), and
// how long it has run. Shape (camelCase; absent values are omitted):
//   {id, requestId, state, task, projectPath, created, started, finished, error, elapsedMs,
//    limits: {maxTurns, maxMinutes, checkCommand, checkTimeoutSeconds, capMicros, capAction}, approval, isolation, baseRef,
//    order: ["b","a","c"], grants: [{tool, command?, prefix?}], workspace: {kind, label, baseLabel, ...},
//    spend: {countedMicros, listMicros, uncertainMicros, requests, text}, waiting: <calls waiting in all models>,
//    models: [{slot, label: "A", provider, model, name, thinking, account, accountLabel, billing, shareMicros, phase, outcome, message,
//              sessionId, workspacePath, workingDirectory, startedAt, endedAt, elapsedMs, turns, maxTurns, deadline,
//              spend: {countedMicros, listMicros, uncertainMicros, requests, text}, waiting: "approval"|"budget"|null,
//              approvals: [{taskId, agentId, callId, tool, command, arguments, since}], budgetAsk, budgetLifted, latestTool, touched,
//              answer, error, check, diff, chatId, chatUrl, kept, discarded}]}
// No credential is in it: accounts are ids and labels; the check's environment never had the app's keys.
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Spend;

namespace PiSharp.WebAgent.AgentCompare.Runner;

/// <summary>Builds the status JSON of a run (see the file comment).</summary>
public static class AgentCompareStatus
{
    /// <summary>The status JSON of <paramref name="run"/>.</summary>
    public static async Task<JsonObject> ToJsonAsync(AgentCompareRun run, AgentCompareRunner runner, IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(services);
        var now = DateTimeOffset.UtcNow;
        var ledger = services.GetService<ISpendLedger>();
        var pending = runner.Pending(run);
        var models = new JsonArray();
        long counted = 0, list = 0, uncertain = 0;
        var requests = 0;
        var waitingCalls = 0;
        foreach (var model in run.Models)
        {
            var spec = run.ModelSpec(model.Slot);
            var key = AgentCompareRun.ModelKey(run.Id, model.Slot);
            SpendTotal? total = null;
            if (ledger is not null && model.SessionId is not null)
            {
                try { total = await ledger.TotalAsync(new SpendLedgerQuery { Run = key }, cancellationToken).ConfigureAwait(false); }
                catch (Exception error) when (error is not OperationCanceledException) { total = null; }
            }
            if (total is not null)
            {
                counted += total.CountedMicros;
                list += total.ListMicros;
                uncertain += total.UncertainMicros;
                requests += total.Requests;
            }
            var approvals = pending.Where(item => item.Slot == model.Slot).ToList();
            waitingCalls += approvals.Count;
            var node = (JsonObject)JsonSerializer.SerializeToNode(model, SettingsStore.JsonOptions)!;
            node["label"] = model.Slot.ToUpperInvariant();
            if (spec is not null)
            {
                node["provider"] = spec.Provider;
                node["model"] = spec.Model;
                node["name"] = spec.Name ?? spec.Model;
                node["thinking"] = spec.Thinking;
                if (spec.Account is not null) node["account"] = spec.Account;
                if (spec.AccountLabel is not null) node["accountLabel"] = spec.AccountLabel;
                node["billing"] = JsonSerializer.SerializeToNode(spec.Billing, SettingsStore.JsonOptions);
                if (spec.ShareMicros is { } share) node["shareMicros"] = share;
            }
            node["turns"] = runner.Store.Turns(key);
            node["maxTurns"] = run.Spec.MaxTurns;
            if (model.StartedAt is { } started)
            {
                node["elapsedMs"] = (long)Math.Max(0, ((model.EndedAt ?? now) - started).TotalMilliseconds);
                node["deadline"] = started.AddMinutes(run.Spec.MaxMinutes);
            }
            node["spend"] = Spend(total);
            node["waiting"] = model.ChatId is not null || model.Phase != AgentModelPhase.Running ? null
                : model.BudgetAsk is not null ? AgentModelWaiting.Budget
                : approvals.Count > 0 ? AgentModelWaiting.Approval
                : null;
            node["approvals"] = new JsonArray([.. approvals.Select(item => (JsonNode)new JsonObject
            {
                ["taskId"] = item.Approval.Request.TaskId,
                ["agentId"] = item.Approval.Request.AgentId,
                ["callId"] = item.Approval.Request.CallId,
                ["tool"] = item.Approval.Request.Tool,
                ["command"] = item.Approval.Request.Command,
                ["arguments"] = item.Approval.Request.Arguments.DeepClone(),
                ["since"] = item.Approval.Since,
            })]);
            if (model.ChatId is { } chat) node["chatUrl"] = "/chat/" + Uri.EscapeDataString(chat);
            models.Add(node);
        }

        var json = new JsonObject
        {
            ["id"] = run.Id,
            ["requestId"] = run.RequestId,
            ["state"] = run.State,
            ["task"] = run.Spec.Task,
            ["projectPath"] = run.Spec.ProjectPath,
            ["created"] = run.Created,
            ["started"] = run.Started,
            ["finished"] = run.Finished,
            ["error"] = run.Error,
            ["elapsedMs"] = run.Started is { } begun ? (long)Math.Max(0, ((run.Finished ?? now) - begun).TotalMilliseconds) : null,
            ["limits"] = new JsonObject
            {
                ["maxTurns"] = run.Spec.MaxTurns,
                ["maxMinutes"] = run.Spec.MaxMinutes,
                ["checkCommand"] = run.Spec.CheckCommand,
                ["checkTimeoutSeconds"] = run.Spec.CheckTimeoutSeconds,
                ["capMicros"] = run.Spec.CapMicros,
                ["capAction"] = JsonSerializer.SerializeToNode(run.Spec.CapAction, SettingsStore.JsonOptions),
            },
            ["approval"] = JsonSerializer.SerializeToNode(run.Spec.Approval, SettingsStore.JsonOptions),
            ["isolation"] = JsonSerializer.SerializeToNode(run.Spec.Isolation, SettingsStore.JsonOptions),
            ["baseRef"] = run.Spec.BaseRef,
            ["order"] = new JsonArray([.. run.Order.Select(slot => (JsonNode)JsonValue.Create(slot)!)]),
            ["grants"] = JsonSerializer.SerializeToNode(run.Grants, SettingsStore.JsonOptions),
            ["workspace"] = run.Workspace is null ? null : JsonSerializer.SerializeToNode(run.Workspace, SettingsStore.JsonOptions),
            ["spend"] = Spend(new SpendTotal(counted, uncertain, list, requests, 0, 0)),
            ["waiting"] = waitingCalls,
            ["models"] = models,
        };
        return Strip(json);
    }

    /// <summary>A run's line in the list: {id, state, task, projectPath, created, finished, models: [{slot, label, name, phase, outcome}]}.</summary>
    public static JsonObject Summary(AgentCompareRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return Strip(new JsonObject
        {
            ["id"] = run.Id,
            ["state"] = run.State,
            ["task"] = run.Spec.Task.Length > 300 ? run.Spec.Task[..299] + "…" : run.Spec.Task,
            ["projectPath"] = run.Spec.ProjectPath,
            ["created"] = run.Created,
            ["finished"] = run.Finished,
            ["models"] = new JsonArray([.. run.Models.Select(model => (JsonNode)new JsonObject
            {
                ["slot"] = model.Slot,
                ["label"] = model.Slot.ToUpperInvariant(),
                ["name"] = run.ModelSpec(model.Slot) is { } spec ? spec.Name ?? spec.Model : model.Slot,
                ["phase"] = model.Phase,
                ["outcome"] = model.Outcome,
            })]),
        });
    }

    private static JsonObject Spend(SpendTotal? total) => total is null
        ? new JsonObject { ["countedMicros"] = 0, ["listMicros"] = 0, ["uncertainMicros"] = 0, ["requests"] = 0, ["text"] = SpendMoney.Format(0) }
        : new JsonObject
        {
            ["countedMicros"] = total.CountedMicros,
            ["listMicros"] = total.ListMicros,
            ["uncertainMicros"] = total.UncertainMicros,
            ["requests"] = total.Requests,
            ["text"] = SpendMoney.Format(total.CountedMicros) + (total.ListMicros > 0 ? " · " + SpendMoney.FormatList(total.ListMicros) : ""),
        };

    // Nulls are left out, as SettingsStore.JsonOptions leaves them out of serialized records.
    private static JsonObject Strip(JsonObject json)
    {
        foreach (var name in json.Where(pair => pair.Value is null).Select(pair => pair.Key).ToList()) json.Remove(name);
        foreach (var pair in json.ToList())
            switch (pair.Value)
            {
                case JsonObject child when pair.Key != "arguments": Strip(child); break;
                case JsonArray items:
                    foreach (var item in items.OfType<JsonObject>()) Strip(item);
                    break;
            }
        return json;
    }
}
