using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

public interface IDallEServiceHost
{
    void WriteLine(string msg, string tag = "SYS", ConsoleColor fg = ConsoleColor.Gray);
    void Notification(string title, string prompt, string? thumbnail = null);
}

public sealed class DallEService
{
    public readonly record struct Worker(string Prompt, DateTime StartedOn, DateTime? EndedOn, CancellationTokenSource KillSource);

    public readonly DallEServiceSettings Settings;
    public readonly IDallEServiceHost Host;

    public DallEService(IOptions<DallEServiceSettings> options, IDallEServiceHost host)
    {
        Settings = options.Value;
        Host = host;
    }

    private readonly AutoResetEvent PromptAvailable = new(false);
    private readonly ConcurrentQueue<string> Prompts = new();
    private readonly HashSet<Worker> Finished = new();
    private readonly HashSet<Worker> Failing = new();
    private readonly HashSet<Worker> Running = new();
    private readonly HashSet<Worker> Pending = new();

    async Task GetBackgroundWorker(string prompt, Action onRequestHeld, CancellationToken ct)
    {
        var rand = new Random();

        var maybeImages = default(IEnumerable<byte[]>?);
        while (!ct.IsCancellationRequested)
        {
            using var client = new DallEClient();
            var request = client.TryGetImages(prompt!, Settings.RetryTimeout, onRequestHeld, ct);
            var delay = Task.Delay(Settings.RetryTimeout, ct);
            var waitAny = Task.WaitAny(new[] { request, delay }, ct);

            switch (waitAny)
            {
                // If the request ended before the timeout, that's good
                default:
                    if (request.IsFaulted)
                    {
                        Host.WriteLine($"\r\n{request.Exception!.Message}", tag: "ERR", fg: ConsoleColor.Red);
                        return;
                    }
                    else
                    {
                        maybeImages = request.Result;
                    }

                    break;
                // If we're left hanging for several minutes, drop this request
                case 1:
                    Host.Notification("Retrying", prompt);
                    Prompts.Enqueue(prompt);
                    PromptAvailable.Set();
                    return;
            }

            if (maybeImages is null)
            {
                await Task.Delay(rand.Next(250, 750), ct);
                continue;
            }

            break;
        }

        if (ct.IsCancellationRequested)
            return;

        var outputDir = Settings.OutputDirectory;
        if (!Directory.Exists(outputDir))
            Directory.CreateDirectory(outputDir);

        var sanitizedPrompt = new SanitizedFileName(prompt!, replacement: "_").Value;
        var baseFn = Path.Combine(outputDir, $"{sanitizedPrompt}\\");
        if (!Directory.Exists(baseFn))
            Directory.CreateDirectory(baseFn);

        foreach (var (image, index) in maybeImages!.Select((e, i) => (e, i)))
        {
            var fn = $"{baseFn}{index + 1}.png";
            await Save(image, fn, ct);
        }

        Host.Notification("Generated", prompt!, $"{baseFn}1.png");

        static async Task Save(ReadOnlyMemory<byte> image, string fn, CancellationToken ct)
        {
            using var fs = File.OpenWrite(fn);
            await fs.WriteAsync(image, ct);
        }
    }

    public Task Start(CancellationToken ct) => Task.Run(() =>
    {
        // Prevent a deadlock when the bot is terminated
        ct.Register(() => PromptAvailable.Set());

        var maxWorkers = Settings.MaxWorkers;
        // Regulates the amount of parallel worker threads
        var semaphore = new SemaphoreSlim(maxWorkers);
        while (!ct.IsCancellationRequested)
        {
            PromptAvailable.WaitOne();
            if (semaphore.CurrentCount == maxWorkers && Settings.MaxWorkers != maxWorkers)
            {
                semaphore.Dispose();
                semaphore = new SemaphoreSlim(maxWorkers = Settings.MaxWorkers);
            }

            while (Prompts.TryDequeue(out var prompt))
            {
                var workerCts = new CancellationTokenSource();
                var worker = new Worker(prompt, DateTime.Now, null, workerCts);
                Pending.Add(worker);
                semaphore.Wait();
                if (!Pending.Contains(worker))
                {
                    // Worker was removed by the !kill command
                    continue;
                }

                Pending.Remove(worker);

                Host.Notification("Running worker", prompt);
                _ = Task.Run(async () =>
                {
                    var linkedToken = CancellationTokenSource.CreateLinkedTokenSource(ct, workerCts.Token).Token;
                    lock (Failing) Failing.Add(worker);
                    try
                    {
                        await GetBackgroundWorker(prompt, () =>
                        {
                            lock (Failing)
                                Failing.Remove(worker);
                            lock (Running)
                                Running.Add(worker);

                            Host.Notification("Generation in progress", prompt!);
                        }, linkedToken);
                        lock (Finished)
                        {
                            Finished.Add(new(worker.Prompt, worker.StartedOn, DateTime.Now, worker.KillSource));
                        }
                    }
                    catch (OperationCanceledException) when (!workerCts.IsCancellationRequested) { }
                    finally
                    {
                        lock (Failing)
                        {
                            Failing.Remove(worker);
                        }

                        lock (Running)
                        {
                            Running.Remove(worker);
                        }

                        semaphore.Release();
                    }
                }, ct);
            }
        }
    }, ct);

    public bool Parse(string prompt)
    {
        var command = (string prompt) => (Regex exp, Action<Match> evalMatch) =>
        {
            var match = exp.Match(prompt);
            if (match.Success)
            {
                evalMatch(match);
                return true;
            }

            return false;
        };

        var definitions = new List<(Func<bool> Command, string Help)>
        {
            {
                (Command: () => command(prompt)(new(@"^\s*!workers\s*$", RegexOptions.Compiled), PrintRunningWorkers),
                Help: "!workers: shows the list of running workers")
            },
            {
                (Command: () => command(prompt)(new(@"^\s*!max_workers(\s+\d+)?\s*$", RegexOptions.Compiled), GetOrSetMaxWorkers),
                Help: "!max_workers: gets or sets the maximum amount of running workers")
            },
            {
                (Command: () => command(prompt)(new(@"^\s*!retry_timeout(\s+\d+)?\s*$", RegexOptions.Compiled), GetOrSetRetryTimeout),
                Help: "!retry_timeout: gets or sets the amount of time after which a running worker will be killed and its prompt re-enqueued (in seconds)")
            },
            {
                (Command: () => command(prompt)(new(@"^\s*!kill(\s+[^\s]+?)?\s*$", RegexOptions.Compiled), Kill),
                Help: "!kill: kills a worker and stops all underlying requests")
            }
        };

        definitions.Add(
            (Command: () => command(prompt)(new(@"^\s*(!help)\s*$", RegexOptions.Compiled), ListCommands),
                Help: "!help: shows this list"));

        var cmdResult = definitions.Any(d => d.Command())
            || command(prompt)(new(@"^\s*!([^\s]*).*?\s*$", RegexOptions.Compiled), UnknownCommand) && false
            ;

        if (!cmdResult)
        {
            Host.WriteLine($"Enqueued: {prompt}");
            Prompts.Enqueue(prompt);
            PromptAvailable.Set();
            return true;
        }

        return false;

        void PrintRunningWorkers(Match _)
        {
            lock (Finished)
            {
                if (Finished.Count == 0)
                {
                    Host.WriteLine($"No prompts have finished yet.");
                }
                else
                {
                    Host.WriteLine($"Finished:");
                    foreach (var worker in Finished.OrderBy(x => x.StartedOn))
                        Host.WriteLine($"\t- {worker.Prompt} (Elapsed: {worker.EndedOn! - worker.StartedOn:hh\\:mm\\:ss})", fg: ConsoleColor.Green);
                }
            }

            lock (Running)
            {
                if (Running.Count == 0)
                {
                    Host.WriteLine($"There are no running prompts.");
                }
                else
                {
                    Host.WriteLine($"Running:");
                    foreach (var worker in Running.OrderBy(x => x.StartedOn))
                        Host.WriteLine($"\t- {worker.Prompt} (Elapsed: {DateTime.Now - worker.StartedOn:hh\\:mm\\:ss})", fg: ConsoleColor.Cyan);
                }
            }

            lock (Failing)
            {
                if (Failing.Count == 0)
                {
                    Host.WriteLine($"There are no failing prompts.");
                }
                else
                {
                    Host.WriteLine($"Failing:");
                    foreach (var worker in Failing.OrderBy(x => x.StartedOn))
                        Host.WriteLine($"\t- {worker.Prompt} (Elapsed: {DateTime.Now - worker.StartedOn:hh\\:mm\\:ss})", fg: ConsoleColor.Red);
                }
            }

            lock (Pending)
            {
                if (Pending.Count == 0)
                {
                    Host.WriteLine($"There are no pending prompts.");
                }
                else
                {
                    Host.WriteLine($"Pending:");
                    foreach (var worker in Pending.OrderBy(x => x.StartedOn))
                        Host.WriteLine($"\t- {worker.Prompt} (Elapsed: {DateTime.Now - worker.StartedOn:hh\\:mm\\:ss})", fg: ConsoleColor.Blue);
                }
            }

            var enqueued = Prompts.ToArray();
            if (enqueued.Length == 0)
            {
                Host.WriteLine($"There are no waiting prompts.");
            }
            else
            {
                Host.WriteLine($"Waiting:");
                foreach (var prompt in enqueued)
                    Host.WriteLine($"\t- {prompt}", fg: ConsoleColor.DarkGray);
            }

        }

        void GetOrSetMaxWorkers(Match m)
        {
            var oldVal = Settings.MaxWorkers;
            if (!m.Groups[1].Success)
            {
                Host.WriteLine($"Max workers: {oldVal}");
                return;
            }

            var newVal = Settings.MaxWorkers = int.Parse(m.Groups[1].Value.Trim());
            if (newVal <= 0)
            {
                Host.WriteLine($"Can only set a positive amount of workers.", "ERR", fg: ConsoleColor.Red);
                return;
            }

            Host.WriteLine($"Max workers: {oldVal}->{newVal}. This change will only reflect once all workers terminate.");
            PromptAvailable.Set();
        }

        void GetOrSetRetryTimeout(Match m)
        {
            var oldVal = Settings.RetryTimeout;
            if (!m.Groups[1].Success)
            {
                Host.WriteLine($"Retry timeout: {oldVal.TotalSeconds}");
                return;
            }

            var newVal = Settings.RetryTimeout = TimeSpan.FromSeconds(int.Parse(m.Groups[1].Value.Trim()));
            Host.WriteLine($"Retry timeout: {oldVal}->{newVal}");
        }

        void Kill(Match m)
        {
            if (!m.Groups[1].Success)
            {
                Host.WriteLine($"Usage: !kill <start of prompt>");
                return;
            }

            LockQueues(() =>
            {
                var enqueued = Prompts.ToArray();
                var matches = Failing.Concat(Running).Concat(Pending).Concat(enqueued.Select(p => new Worker(p, default, default, new())))
                    .Where(x => x.Prompt.StartsWith(m.Groups[1].Value.Trim(), StringComparison.OrdinalIgnoreCase));
                var count = matches.Count();

                if (count == 0)
                {
                    Host.WriteLine($"No matches.", "ERR", ConsoleColor.Red);
                    return;
                }

                foreach (var worker in matches)
                {
                    if (enqueued.Contains(worker.Prompt))
                    {
                        for (var i = 0; i < enqueued.Length && Prompts.TryDequeue(out var p) && !p.Equals(worker.Prompt); ++i)
                        {
                            Prompts.Enqueue(p!);
                        }
                    }

                    worker.KillSource.Cancel();
                    Host.WriteLine($"Discarded: {worker.Prompt}");
                    Host.Notification("Discarded", worker.Prompt);
                    Pending.Remove(worker);
                }

                PromptAvailable.Set();

            });
        }

        void UnknownCommand(Match m) => Host.WriteLine($"Unknown command: {m.Groups[1].Value}");
        void ListCommands(Match m)
        {
            Host.WriteLine($"Defined commands:");
            foreach (var (Command, Help) in definitions)
            {
                Host.WriteLine($"\t- {Help}");
            }
        }

        void LockQueues(Action callback)
        {
            lock (Failing!)
                lock (Running!)
                    lock (Pending!)
                        callback();
        }
    }

}
