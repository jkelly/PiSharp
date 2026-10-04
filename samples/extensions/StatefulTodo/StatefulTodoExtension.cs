/*
 * Derived ordinary tool behavior from pi v0.99.1 examples/extensions/todo.ts.
 * MIT License
 * Copyright (c) 2025 Mario Zechner
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in all
 * copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
 * SOFTWARE.
 */
using System.Globalization;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Extensions;

namespace StatefulTodo;

/// <summary>State comes exclusively from the current acknowledged branch, including preceding calls in a batch.</summary>
public sealed class StatefulTodoExtension : IPiSharpExtension
{
    public const string ParametersJson = """
        {"type":"object","properties":{"action":{"type":"string","enum":["list","add","toggle","clear"]},"text":{"type":"string","description":"Todo text (for add)"},"id":{"type":"number","description":"Todo ID (for toggle)"}},"required":["action"]}
        """;
    private const string Description = "Manage a todo list. Actions: list, add (text), toggle (id), clear";
    private int initialized, disposed;
    private sealed record Todo(long Id, string Text, bool Done);

    public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registry); cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref disposed) != 0 || Interlocked.Exchange(ref initialized, 1) != 0)
            throw new InvalidOperationException("Todo extension is already initialized or disposed.");
        registry.RegisterTool(new("todo", "todo", Description, JsonData.Parse(ParametersJson), ExecuteAsync));
        return ValueTask.CompletedTask;
    }

    private ValueTask<JsonData> ExecuteAsync(JsonData arguments, IExtensionToolContext context, CancellationToken token)
    {
        CheckLifetime(context, token);
        var snapshot = (context as IExtensionSessionContext)?.SessionSnapshot
            ?? throw new InvalidOperationException("Todo requires an active acknowledged session snapshot.");
        var todos = new List<Todo>(); long nextId = 1;
        foreach (var raw in snapshot.BranchEntries)
        {
            token.ThrowIfCancellationRequested(); var entry = raw.Value;
            if (!entry.TryGetProperty("type", out var type) || type.GetString() != "message" ||
                !entry.TryGetProperty("message", out var message) ||
                !message.TryGetProperty("role", out var role) || role.GetString() != "toolResult" ||
                !message.TryGetProperty("toolName", out var name) || name.GetString() != "todo" ||
                (message.TryGetProperty("isError", out var failed) && failed.ValueKind == JsonValueKind.True) ||
                !message.TryGetProperty("details", out var details) || details.ValueKind == JsonValueKind.Null) continue;
            // Copy the last complete tool-owned snapshot; no JsonDocument, list or object alias survives a call.
            if (details.ValueKind != JsonValueKind.Object || !details.TryGetProperty("todos", out var list) || list.ValueKind != JsonValueKind.Array ||
                !details.TryGetProperty("nextId", out var next) || !next.TryGetInt64(out var restoredNext) || restoredNext is < 1 or > 9_007_199_254_740_991)
                throw new InvalidOperationException("Acknowledged Todo details are malformed.");
            var restored = new List<Todo>();
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var id) || !id.TryGetInt64(out var todoId) ||
                    todoId is < 1 or > 9_007_199_254_740_991 || !item.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String ||
                    !item.TryGetProperty("done", out var done) || done.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new InvalidOperationException("Acknowledged Todo item is malformed.");
                restored.Add(new(todoId, text.GetString()!, done.GetBoolean()));
            }
            todos = restored; nextId = restoredNext;
        }
        var args = arguments.Value; var action = args.GetProperty("action").GetString()!;
        string content; string? error = null;
        switch (action)
        {
            case "list":
                content = todos.Count == 0 ? "No todos" : string.Join('\n', todos.Select(todo => $"[{(todo.Done ? "x" : " ")}] #{todo.Id}: {todo.Text}"));
                break;
            case "add":
                if (!args.TryGetProperty("text", out var suppliedText) || suppliedText.GetString() is not { Length: > 0 } text)
                { content = "Error: text required for add"; error = "text required"; break; }
                if (nextId >= 9_007_199_254_740_991) throw new InvalidOperationException("Todo ID exceeds its exact integer profile.");
                var added = new Todo(nextId++, text, false); todos.Add(added); content = $"Added todo #{added.Id}: {added.Text}";
                break;
            case "toggle":
                if (!args.TryGetProperty("id", out var requestedId))
                { content = "Error: id required for toggle"; error = "id required"; break; }
                var requested = requestedId.GetDouble(); var index = todos.FindIndex(todo => todo.Id == requested);
                if (index < 0)
                {
                    var idText = requested == 0 ? "0" : requested.ToString(CultureInfo.InvariantCulture);
                    content = $"Todo #{idText} not found"; error = $"#{idText} not found"; break;
                }
                var toggled = todos[index] with { Done = !todos[index].Done }; todos[index] = toggled;
                content = $"Todo #{toggled.Id} {(toggled.Done ? "completed" : "uncompleted")}";
                break;
            case "clear":
                content = $"Cleared {todos.Count} todos"; todos = []; nextId = 1;
                break;
            default:
                content = $"Unknown action: {action}"; error = $"unknown action: {action}"; action = "list";
                break;
        }
        var items = todos.Select(todo => new { id = todo.Id, text = todo.Text, done = todo.Done }).ToArray();
        object detailsResult = error is null ? new { action, todos = items, nextId } : new { action, todos = items, nextId, error };
        var result = JsonData.Parse(JsonSerializer.Serialize(new { content = new[] { new { type = "text", text = content } }, details = detailsResult }));
        CheckLifetime(context, token); return ValueTask.FromResult(result);
    }

    private void CheckLifetime(IExtensionToolContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); context.OperationCancellationToken.ThrowIfCancellationRequested();
        context.SessionCancellationToken.ThrowIfCancellationRequested(); context.ExtensionLifetimeCancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref disposed) != 0) throw new ObjectDisposedException(nameof(StatefulTodoExtension));
    }
    public ValueTask DisposeAsync() { Interlocked.Exchange(ref disposed, 1); return ValueTask.CompletedTask; }
}
