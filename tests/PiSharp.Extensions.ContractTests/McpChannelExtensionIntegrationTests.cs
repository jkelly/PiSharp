using System.Collections.Concurrent;
using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.Runtime;
using PiSharp.Extensions.Mcp.Transport;
using PiSharp.Extensions.Mcp.IncomingRequests;
using PiSharp.Extensions.Runtime.Mcp.Transport;

internal static class McpChannelExtensionIntegrationTests
{
    internal const string Prefix = "mcp-channel-extension.";
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        (Prefix + "dynamic-roots-capability-and-per-wire-request", DynamicRoots),
        (Prefix + "generic-handler-replacement-and-physical-response", GenericRequests),
        (Prefix + "close-joins-held-roots-and-physical-response", HeldRoots),
        (Prefix + "wire-cancellation-stops-and-joins-owned-original", IncomingCancellation),
        (Prefix + "static-roots-and-default-ping-remain-reachable", StaticDefaults)
    ];
    private static void Require(bool value) { if (!value) throw new IOException("MCP channel integration assertion failed."); }
    private static TaskCompletionSource<T> Gate<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static bool RetainsOriginal(Exception error, Task original) =>
        error is McpIncomingOriginalException witness && ReferenceEquals(witness.Original, original) ||
        error is AggregateException aggregate && aggregate.InnerExceptions.Any(child => RetainsOriginal(child, original)) ||
        error.InnerException is { } inner && RetainsOriginal(inner, original);
    private static async Task Cleanup(Exception? primary, Action release, McpJsonRpcRequestChannel channel,
        Func<Exception, bool>? expectedClose = null)
    {
        var errors = new List<Exception>(); if (primary is not null) errors.Add(primary);
        try { release(); } catch (Exception error) { errors.Add(error); }
        Task? original = null;
        try { original = channel.CloseAsync(); await original; }
        catch (Exception error) { if (expectedClose?.Invoke(error) != true) errors.Add(original is { IsFaulted: true } ? original.Exception! : error); }
        if (errors.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(errors[0]).Throw();
        if (errors.Count > 1) throw new AggregateException(errors);
    }
    private sealed class Wire : IMcpWireTransport
    {
        private McpWireCallbacks callbacks = null!;
        private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonData>> responses = new();
        internal readonly TaskCompletionSource<bool> ResponseRelease = Gate<bool>();
        internal bool HoldResponses;
        internal JsonData? Initialize;
        internal int Closes;
        private Task? close;
        public Task StartAsync(McpWireCallbacks value, CancellationToken token) { callbacks = value; return Task.CompletedTask; }
        public Task SetProtocolVersionAsync(string version, CancellationToken token) => Task.CompletedTask;
        internal async Task Incoming(long id, string method, string? parameters = null)
        {
            var original = callbacks.Receive(JsonData.Parse("{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"" + method + "\"" +
                (parameters is null ? "" : ",\"params\":" + parameters) + "}")).AsTask();
            await original;
        }
        internal async Task Cancel(long id)
        {
            var original = callbacks.Receive(JsonData.Parse("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/cancelled\",\"params\":{\"requestId\":" + id + "}}")).AsTask();
            await original;
        }
        internal Task<JsonData> Response(long id) => responses.GetOrAdd(id, _ => Gate<JsonData>()).Task;
        public async Task SendAsync(JsonData message, CancellationToken token)
        {
            if (message.Value.TryGetProperty("method", out var method))
            {
                if (method.GetString() == "initialize")
                {
                    Initialize = message;
                    var original = callbacks.Receive(JsonData.Parse("{\"jsonrpc\":\"2.0\",\"id\":" + message.Value.GetProperty("id").GetRawText() + ",\"result\":{}}")).AsTask();
                    await original;
                }
                return;
            }
            var id = message.Value.GetProperty("id").GetInt64();
            responses.GetOrAdd(id, _ => Gate<JsonData>()).TrySetResult(message);
            if (HoldResponses) await ResponseRelease.Task;
        }
        public Task CloseAsync() { if (close is not null) return close; Closes++; return close = Task.CompletedTask; }
    }
    private static async Task DynamicRoots()
    {
        var wire = new Wire(); var channel = new McpJsonRpcRequestChannel(wire); var calls = 0; Exception? primary = null;
        try
        {
            channel.ConfigureDynamicRoots(_ => ValueTask.FromResult(JsonData.Parse("[{\"uri\":\"file:///inert-" + ++calls + "\",\"unknown\":9007199254740993}]")));
            await channel.StartAsync(default);
            var initialize = channel.RequestAsync("initialize", JsonData.Parse("{\"capabilities\":{\"custom\":{}}}"), new(0), default).AsTask(); await initialize;
            Require(wire.Initialize!.Value.GetProperty("params").GetProperty("capabilities").TryGetProperty("roots", out _) &&
                wire.Initialize.Value.GetProperty("params").GetProperty("capabilities").TryGetProperty("custom", out _));
            await wire.Incoming(101, "roots/list"); var first = await wire.Response(101).WaitAsync(TimeSpan.FromSeconds(5));
            await wire.Incoming(102, "roots/list"); var second = await wire.Response(102).WaitAsync(TimeSpan.FromSeconds(5));
            Require(calls == 2 && first.Value.GetProperty("result").GetProperty("roots")[0].GetProperty("uri").GetString() == "file:///inert-1" &&
                second.Value.GetProperty("result").GetProperty("roots")[0].GetProperty("unknown").GetRawText() == "9007199254740993");
        }
        catch (Exception error) { primary = error; }
        finally { await Cleanup(primary, () => wire.ResponseRelease.TrySetResult(true), channel); }
        Require(wire.Closes == 1);
    }
    private static async Task GenericRequests()
    {
        var wire = new Wire(); var channel = new McpJsonRpcRequestChannel(wire); var calls = 0; Exception? primary = null;
        try
        {
            var old = channel.SetRequestHandler("custom", (_, _) => ValueTask.FromResult<JsonData?>(JsonData.Parse("1")));
            var remove = channel.SetRequestHandler("custom", (parameters, _) => { calls++; return ValueTask.FromResult(parameters); });
            old(); await channel.StartAsync(default); await wire.Incoming(101, "custom", "{\"opaque\":9007199254740993}");
            var response = await wire.Response(101).WaitAsync(TimeSpan.FromSeconds(5));
            Require(calls == 1 && response.Value.GetProperty("result").GetProperty("opaque").GetRawText() == "9007199254740993");
            remove(); await wire.Incoming(102, "custom"); response = await wire.Response(102).WaitAsync(TimeSpan.FromSeconds(5));
            Require(calls == 1 && response.Value.GetProperty("error").GetProperty("code").GetInt32() == -32601);
        }
        catch (Exception error) { primary = error; }
        finally { await Cleanup(primary, () => wire.ResponseRelease.TrySetResult(true), channel); }
    }
    private static async Task HeldRoots()
    {
        var wire = new Wire { HoldResponses = true }; var source = Gate<JsonData>(); var entered = Gate<bool>();
        var channel = new McpJsonRpcRequestChannel(wire); Task? close = null; Exception? primary = null;
        try
        {
            channel.ConfigureDynamicRoots(_ => { entered.TrySetResult(true); return new(source.Task); });
            await channel.StartAsync(default); await wire.Incoming(101, "roots/list");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); close = channel.CloseAsync(); Require(!close.IsCompleted);
            source.TrySetResult(JsonData.Parse("[]")); await wire.Response(101).WaitAsync(TimeSpan.FromSeconds(5));
            Require(!close.IsCompleted && ReferenceEquals(close, channel.CloseAsync()));
        }
        catch (Exception error) { primary = error; }
        finally { await Cleanup(primary, () => { source.TrySetResult(JsonData.Parse("[]")); wire.ResponseRelease.TrySetResult(true); }, channel); }
        Require(close is null || close.IsCompletedSuccessfully);
    }
    private static async Task IncomingCancellation()
    {
        var wire = new Wire(); var original = Gate<JsonData?>(); var entered = Gate<bool>(); var stopped = Gate<bool>();
        CancellationToken owned = default; CancellationTokenRegistration registration = default;
        var channel = new McpJsonRpcRequestChannel(wire); Exception? primary = null; Exception? closeFailure = null;
        try
        {
            channel.SetRequestHandler("custom", (_, token) => { owned = token; registration = token.Register(() => stopped.TrySetResult(true)); entered.TrySetResult(true); return new(original.Task); });
            await channel.StartAsync(default); await wire.Incoming(101, "custom"); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await wire.Cancel(101); await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5)); Require(!original.Task.IsCompleted && owned.IsCancellationRequested);
            original.TrySetCanceled(owned); var response = await wire.Response(101).WaitAsync(TimeSpan.FromSeconds(5));
            Require(response.Value.GetProperty("error").GetProperty("code").GetInt32() == -32603);
        }
        catch (Exception error) { primary = error; }
        finally
        {
            try { await Cleanup(primary, () => { original.TrySetCanceled(owned); wire.ResponseRelease.TrySetResult(true); }, channel,
                error => { closeFailure = error; return RetainsOriginal(error, original.Task); }); }
            finally { registration.Dispose(); }
        }
        Require(original.Task.IsCanceled && closeFailure is not null && channel.CloseAsync().IsFaulted && wire.Closes == 1);
    }
    private static async Task StaticDefaults()
    {
        foreach (var optIn in new[] { false, true })
        {
            var wire = new Wire(); var channel = new McpJsonRpcRequestChannel(wire); Exception? primary = null;
            try
            {
                await channel.ConfigureRootsAsync(JsonData.Parse("[{\"uri\":\"file:///static\"}]"), default);
                if (optIn) channel.SetRequestHandler("custom", (_, _) => ValueTask.FromResult<JsonData?>(null));
                await channel.StartAsync(default); await wire.Incoming(101, "ping"); await wire.Incoming(102, "roots/list");
                var ping = await wire.Response(101).WaitAsync(TimeSpan.FromSeconds(5)); var roots = await wire.Response(102).WaitAsync(TimeSpan.FromSeconds(5));
                Require(ping.Value.GetProperty("result").GetRawText() == "{}" && roots.Value.GetProperty("result").GetProperty("roots")[0].GetProperty("uri").GetString() == "file:///static");
            }
            catch (Exception error) { primary = error; }
            finally { await Cleanup(primary, () => wire.ResponseRelease.TrySetResult(true), channel); }
        }
    }
}
