// Decision 0011 (run W5, integration fix 2): the cases that would have caught the second W5 review's finding in its integration area. A W4
// review server (--faux-metered, started from the integration worktree's bin) was left running for more than twelve hours after its review;
// while it ran it held every assembly of that bin, so the worktree's bin stayed at the old build and a build there failed on locked files.
// A faux app now stops by itself after an hour with no request (WebAgentIdleExit; --idle-exit sets the time, 0 never), as a normal shutdown
// that lets its data folder (and with the process, its build output) go; real use never stops unless --idle-exit asks. The cases hold the
// switch, the idle clock (a request in progress holds it, a long one does not read as idle when it ends), the registration, and the built
// app end to end: requests and an open page stream keep it running past its limit, and once nothing happens it exits 0 on its own, says why
// and leaves its data folder free. Faux models only; nothing calls a provider.
using System.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PiSharp.WebAgent.Hosting;

internal static partial class Program
{
    internal static IEnumerable<(string, Func<Task>)> W5IntegrationFix2Cases() =>
    [
        Case("w5-integration-fix2.idle-exit.a-faux-app-stops-after-an-hour-of-no-requests-and-a-real-one-never", () =>
        {
            var local = Temp("w5f2-options");
            try
            {
                TimeSpan? Idle(params string[] args) => WebAgentOptions.FromCommandLine(args, _ => null, local).Options.IdleExit;
                Equal(TimeSpan.FromHours(1), WebAgentOptions.FauxIdleExit, "the faux default");
                Equal<TimeSpan?>(null, Idle(), "real use never stops by itself");
                Equal<TimeSpan?>(TimeSpan.FromHours(1), Idle("--faux"), "--faux stops after an hour");
                Equal<TimeSpan?>(TimeSpan.FromHours(1), Idle("--faux-metered"), "--faux-metered (a faux app too) stops after an hour");
                Equal<TimeSpan?>(null, Idle("--faux=false"), "--faux=false is real use");
                Equal<TimeSpan?>(null, Idle("--faux", "--idle-exit", "0"), "--idle-exit 0 keeps a faux app running");
                Equal<TimeSpan?>(null, Idle("--faux-metered", "--idle-exit=0"), "--idle-exit=0");
                Equal<TimeSpan?>(TimeSpan.FromMinutes(15), Idle("--faux", "--idle-exit", "15"), "whole minutes");
                Equal<TimeSpan?>(TimeSpan.FromMinutes(90), Idle("--idle-exit=90"), "real use stops only when asked");
                Equal<TimeSpan?>(TimeSpan.FromSeconds(30), Idle("--idle-exit", "00:00:30", "--faux"), "hh:mm:ss, before or after --faux");
                Equal<TimeSpan?>(TimeSpan.FromSeconds(3725), Idle("--idle-exit=1:02:05"), "h:mm:ss");
                var (options, rest) = WebAgentOptions.FromCommandLine(["--faux", "--idle-exit", "5", "--urls", "http://127.0.0.1:1"], _ => null, local);
                Equal("--urls|http://127.0.0.1:1", string.Join("|", rest), "--idle-exit never reaches ASP.NET Core");
                Equal(true, options.Faux, "still faux");
                foreach (var bad in new[] { "-5", "soon", "1.5", "25:00:00", "00:61:00", "" })
                {
                    var error = Throws<ArgumentException>(() => WebAgentOptions.FromCommandLine(["--idle-exit=" + bad], _ => null, local), "--idle-exit=" + bad);
                    Check(error.Message.Contains("--idle-exit takes whole minutes", StringComparison.Ordinal), "the refusal says what it takes: " + error.Message);
                }
                Throws<ArgumentException>(() => WebAgentOptions.FromCommandLine(["--idle-exit"], _ => null, local), "--idle-exit without a time");
                Throws<ArgumentException>(() => WebAgentOptions.FromCommandLine(["--idle-exit", "--faux"], _ => null, local), "--idle-exit followed by a switch");
            }
            finally { DeleteTemp(local); }
        }),

        Case("w5-integration-fix2.idle-exit.a-request-in-progress-holds-the-idle-time", () =>
        {
            var clock = new W5f2Clock();
            var activity = new WebAgentActivity(clock);
            Equal(TimeSpan.Zero, activity.Idle, "idle from the start");
            clock.Advance(TimeSpan.FromMinutes(10));
            Equal(TimeSpan.FromMinutes(10), activity.Idle, "nothing happened for ten minutes");
            activity.Begin();
            clock.Advance(TimeSpan.FromHours(3));
            Equal(1, activity.InProgress, "one request in progress");
            Equal(TimeSpan.Zero, activity.Idle, "a page's open stream, three hours long, is not idle time");
            activity.Begin();
            activity.End();
            Equal(TimeSpan.Zero, activity.Idle, "while the stream is still open");
            activity.End();
            Equal(0, activity.InProgress, "nothing in progress");
            Equal(TimeSpan.Zero, activity.Idle, "a request that ran three hours does not read as three idle hours when it ends");
            clock.Advance(TimeSpan.FromSeconds(42));
            Equal(TimeSpan.FromSeconds(42), activity.Idle, "idle since the last request finished");
            activity.Touch();
            Equal(TimeSpan.Zero, activity.Idle, "the app's start starts the idle time again");

            Equal(TimeSpan.FromSeconds(30), WebAgentIdleExit.CheckInterval(TimeSpan.FromHours(1)), "an hour is looked at every 30 seconds");
            Equal(TimeSpan.FromSeconds(15), WebAgentIdleExit.CheckInterval(TimeSpan.FromMinutes(1)), "a quarter of a short limit");
            Equal(TimeSpan.FromMilliseconds(250), WebAgentIdleExit.CheckInterval(TimeSpan.FromMilliseconds(100)), "never more often than 250 ms");
            Check(WebAgentIdleExit.StopMessage(TimeSpan.FromHours(1)).Contains("no request for 60 minutes", StringComparison.Ordinal), "the hour, in minutes");
            Check(WebAgentIdleExit.StopMessage(TimeSpan.FromSeconds(2)).Contains("no request for 2 seconds", StringComparison.Ordinal), "seconds");
            Check(WebAgentIdleExit.StopMessage(TimeSpan.FromHours(1)).Contains("--idle-exit 0", StringComparison.Ordinal), "the log says how to keep it running");

            var off = new ServiceCollection().AddWebAgentIdleExit(new WebAgentOptions { DataDirectory = Path.GetTempPath() });
            Equal(0, off.Count, "real use registers nothing: no filter, no service");
            var on = new ServiceCollection().AddWebAgentIdleExit(new WebAgentOptions { DataDirectory = Path.GetTempPath(), Faux = true, IdleExit = TimeSpan.FromHours(1) });
            Check(on.Any(item => item.ServiceType == typeof(WebAgentActivity)), "the activity");
            Check(on.Any(item => item.ServiceType == typeof(IStartupFilter)), "a startup filter counts the requests around the whole pipeline");
            Check(on.Any(item => item.ServiceType == typeof(IHostedService) && item.ImplementationType == typeof(WebAgentIdleExit)), "the idle exit");
        }),

        Case("w5-integration-fix2.idle-exit.a-forgotten-faux-server-stops-by-itself-and-lets-its-data-folder-go", async () =>
        {
            var root = Temp("w5f2-idle");
            try
            {
                var data = Path.Join(root, "data");
                var limit = TimeSpan.FromSeconds(2);
                var origin = $"http://127.0.0.1:{FreePort()}";
                var run = WebAgentApp.RunToExitAsync(new AppStart { DataDirectory = data, Arguments = ["--idle-exit", "00:00:02"] }, ["--urls", origin],
                    TimeSpan.FromSeconds(100));
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                await WaitUntilAsync(async () =>
                {
                    if (run.IsCompleted) throw new InvalidOperationException("The app exited before it answered: " + (await run).Output);
                    try { return (await http.GetAsync(origin + "/api/health")).IsSuccessStatusCode; }
                    catch (HttpRequestException) { return false; }
                }, "the app answers /api/health", TimeSpan.FromSeconds(60));

                // Requests keep it running: one every 400 ms for twice its limit.
                for (var poll = 0; poll < 10; poll++)
                {
                    Equal(200, (int)(await http.GetAsync(origin + "/api/health")).StatusCode, "a request while it runs");
                    await Task.Delay(400);
                }
                Check(!run.IsCompleted, "requests every 400 ms keep a 2 second idle exit running");

                // A page's open event stream is a request in progress: with no other request for 2.5 times the limit, it keeps running.
                using (var stream = await http.GetAsync(origin + "/api/stream", HttpCompletionOption.ResponseHeadersRead))
                {
                    Equal(200, (int)stream.StatusCode, "a tab's stream opens");
                    await Task.Delay(TimeSpan.FromSeconds(5));
                    Check(!run.IsCompleted, "an open page stream keeps the app running past its limit");
                }

                // Then nothing: it stops by itself, as a normal shutdown, not before its limit, and says why.
                var closed = Stopwatch.StartNew();
                var (exitCode, output) = await run;
                closed.Stop();
                Equal(0, exitCode, "an idle exit is a normal shutdown: " + output);
                Check(output.Contains(WebAgentIdleExit.StopMessage(limit), StringComparison.Ordinal), "the log says why it stopped: " + output);
                Check(closed.Elapsed >= TimeSpan.FromSeconds(1.5), $"not before its limit (it stopped {closed.Elapsed.TotalSeconds:0.0} s after the stream closed)");

                // It let its data folder go (and, having exited, the build output it ran from): another app could start on it now.
                using (var free = WebAgentDataLock.TryAcquire(data)) Check(free is not null, "the data folder's webagent.lock is free");
                await using var next = await WebAgentApp.StartAsync(root, new AppStart { DataDirectory = data, Arguments = ["--idle-exit", "0"] });
                Equal(200, (await next.Get("/api/health")).Status, "a new app starts on the same data folder");
            }
            finally { DeleteTemp(root); }
        }),
    ];

    /// <summary>A clock whose timestamps move only when told to.</summary>
    private sealed class W5f2Clock : TimeProvider
    {
        private long ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref ticks);

        public void Advance(TimeSpan by) => Interlocked.Add(ref ticks, by.Ticks);
    }
}
