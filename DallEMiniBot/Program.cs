using System.Collections.Concurrent;
using System.Text.RegularExpressions;

var settings = (
    // The maximum number of parallel workers. See also !max_workers
    MaxWorkers: 3,
    // The folder where the output of this bot will be stored. See also !output_dir
    OutputDirectory: Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "images\\"),
    // The amount of time after which a running request will be dropped and recycled. See also !retry_timeout
    RetryTimeout: TimeSpan.FromMinutes(4)
);
var prompts = new ConcurrentQueue<string>();
var promptAvailable = new AutoResetEvent(false);
var done = new HashSet<Worker>();
var failing = new HashSet<Worker>();
var running = new HashSet<Worker>();
var pending = new HashSet<Worker>();
var cts = new CancellationTokenSource();

Notification.SetupEvents();
Console.CancelKeyPress += (s, e) =>
{
    e.Cancel = true;
    cts.Cancel(true);
    promptAvailable.Set();
};

var getBackgroundWorker = async (string prompt, Action onRequestHeld, CancellationToken ct) =>
{
    var rand = new Random();

    var maybeImages = default(IEnumerable<byte[]>?);
    while (!ct.IsCancellationRequested)
    {
        using var client = new DallEClient();
        var request = client.TryGetImages(prompt!, settings.RetryTimeout, onRequestHeld, ct);
        var delay = Task.Delay(settings.RetryTimeout, ct);
        var waitAny = Task.WaitAny(new[] { request, delay }, ct);

        switch (waitAny)
        {
            // If the request ended before the timeout, that's good
            default:
                if (request.IsFaulted)
                {
                    WriteLine($"\r\n{request.Exception!.Message}", tag: "ERR", fg: ConsoleColor.Red);
                    return;
                }
                else
                {
                    maybeImages = request.Result;
                }

                break;
            // If we're left hanging for several minutes, drop this request
            case 1:
                new Notification("Retrying", prompt).Show();
                prompts.Enqueue(prompt);
                promptAvailable.Set();
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

    var outputDir = settings.OutputDirectory;
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

    new Notification("Generated", prompt!, $"{baseFn}1.png").Show();

    static async Task Save(ReadOnlyMemory<byte> image, string fn, CancellationToken ct)
    {
        using var fs = File.OpenWrite(fn);
        await fs.WriteAsync(image, ct);
    }
};

var workerManagerTask = Task.Run(() =>
{
    var maxWorkers = settings.MaxWorkers;
    // Regulates the amount of parallel worker threads
    var semaphore = new SemaphoreSlim(maxWorkers);
    while (!cts.IsCancellationRequested)
    {
        promptAvailable.WaitOne();
        if (semaphore.CurrentCount == maxWorkers && settings.MaxWorkers != maxWorkers)
        {
            semaphore.Dispose();
            semaphore = new SemaphoreSlim(maxWorkers = settings.MaxWorkers);
        }

        while (prompts.TryDequeue(out var prompt))
        {
            var workerCts = new CancellationTokenSource();
            var worker = new Worker(prompt, DateTime.Now, null, workerCts);
            pending.Add(worker);
            semaphore.Wait();
            if (!pending.Contains(worker))
            {
                // Worker was removed by the !kill command
                continue;
            }

            pending.Remove(worker);

            new Notification("Running worker", prompt).Show();
            _ = Task.Run(async () =>
            {
                var linkedToken = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, workerCts.Token).Token;
                lock (failing) failing.Add(worker);
                try
                {
                    await getBackgroundWorker(prompt, () =>
                    {
                        lock (failing)
                            failing.Remove(worker);
                        lock (running)
                            running.Add(worker);

                        new Notification("Generation in progress", prompt!).Show();
                    }, linkedToken);
                    lock (done)
                    {
                        done.Add(new(worker.Prompt, worker.StartedOn, DateTime.Now, worker.KillSource));
                    }
                }
                catch (OperationCanceledException) when (!workerCts.IsCancellationRequested) { }
                finally
                {
                    lock (failing)
                    {
                        failing.Remove(worker);
                    }

                    lock (running)
                    {
                        running.Remove(worker);
                    }

                    semaphore.Release();
                }
            }, cts.Token);
        }
    }
});

var runCommands = (string prompt) =>
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
        },
    };

    definitions.Add(
        (Command: () => command(prompt)(new(@"^\s*(!help)\s*$", RegexOptions.Compiled), ListCommands),
            Help: "!help: shows this list"));

    return definitions.Any(d => d.Command())
        || command(prompt)(new(@"^\s*!([^\s]*).*?\s*$", RegexOptions.Compiled), UnknownCommand)
        ;

    void PrintRunningWorkers(Match _)
    {
        lock (done)
        {
            if (done.Count == 0)
            {
                WriteLine($"No prompts have finished yet.");
            }
            else
            {
                WriteLine($"Finished:");
                foreach (var worker in done.OrderBy(x => x.StartedOn))
                    WriteLine($"\t- {worker.Prompt} (Elapsed: {worker.EndedOn! - worker.StartedOn:hh\\:mm\\:ss})", fg: ConsoleColor.Green);
            }
        }

        lock (running)
        {
            if (running.Count == 0)
            {
                WriteLine($"There are no running prompts.");
            }
            else
            {
                WriteLine($"Running:");
                foreach (var worker in running.OrderBy(x => x.StartedOn))
                    WriteLine($"\t- {worker.Prompt} (Elapsed: {DateTime.Now - worker.StartedOn:hh\\:mm\\:ss})", fg: ConsoleColor.Cyan);
            }
        }

        lock (failing)
        {
            if (failing.Count == 0)
            {
                WriteLine($"There are no failing prompts.");
            }
            else
            {
                WriteLine($"Failing:");
                foreach (var worker in failing.OrderBy(x => x.StartedOn))
                    WriteLine($"\t- {worker.Prompt} (Elapsed: {DateTime.Now - worker.StartedOn:hh\\:mm\\:ss})", fg: ConsoleColor.Red);
            }
        }

        lock (pending)
        {
            if (pending.Count == 0)
            {
                WriteLine($"There are no pending prompts.");
            }
            else
            {
                WriteLine($"Pending:");
                foreach (var worker in pending.OrderBy(x => x.StartedOn))
                    WriteLine($"\t- {worker.Prompt} (Elapsed: {DateTime.Now - worker.StartedOn:hh\\:mm\\:ss})", fg: ConsoleColor.Blue);
            }
        }

        var enqueued = prompts.ToArray();
        if (enqueued.Length == 0)
        {
            WriteLine($"There are no waiting prompts.");
        }
        else
        {
            WriteLine($"Waiting:");
            foreach (var prompt in enqueued)
                WriteLine($"\t- {prompt}", fg: ConsoleColor.DarkGray);
        }

    }

    void GetOrSetMaxWorkers(Match m)
    {
        var oldVal = settings.MaxWorkers;
        if (!m.Groups[1].Success)
        {
            WriteLine($"Max workers: {oldVal}");
            return;
        }

        var newVal = settings.MaxWorkers = int.Parse(m.Groups[1].Value.Trim());
        if (newVal <= 0)
        {
            WriteLine($"Can only set a positive amount of workers.", "ERR", fg: ConsoleColor.Red);
            return;
        }

        WriteLine($"Max workers: {oldVal}->{newVal}. This change will only reflect once all workers terminate.");
        promptAvailable.Set();
    }

    void GetOrSetRetryTimeout(Match m)
    {
        var oldVal = settings.RetryTimeout;
        if (!m.Groups[1].Success)
        {
            WriteLine($"Retry timeout: {oldVal.TotalSeconds}");
            return;
        }

        var newVal = settings.RetryTimeout = TimeSpan.FromSeconds(int.Parse(m.Groups[1].Value.Trim()));
        WriteLine($"Retry timeout: {oldVal}->{newVal}");
    }

    void Kill(Match m)
    {
        if (!m.Groups[1].Success)
        {
            WriteLine($"Usage: !kill <start of prompt>");
            return;
        }

        LockQueues(() =>
        {
            var enqueued = prompts.ToArray();
            var matches = failing.Concat(running).Concat(pending).Concat(enqueued.Select(p => new Worker(p, default, default, new())))
                .Where(x => x.Prompt.StartsWith(m.Groups[1].Value.Trim(), StringComparison.OrdinalIgnoreCase));
            var count = matches.Count();

            if (count == 0)
            {
                WriteLine($"No matches.", "ERR", ConsoleColor.Red);
                return;
            }

            foreach (var worker in matches)
            {
                if (enqueued.Contains(worker.Prompt))
                {
                    for (var i = 0; i < enqueued.Length && prompts.TryDequeue(out var p) && !p.Equals(worker.Prompt); ++i)
                    {
                        prompts.Enqueue(p!);
                    }
                }

                worker.KillSource.Cancel();
                WriteLine($"Discarded: {worker.Prompt}");
                new Notification("Discarded", worker.Prompt).Show();
                pending.Remove(worker);
            }

            promptAvailable.Set();

        });
    }

    void UnknownCommand(Match m) => Console.WriteLine($"Unknown command: {m.Groups[1].Value}");
    void ListCommands(Match m)
    {
        WriteLine($"Defined commands:");
        foreach (var d in definitions)
        {
            WriteLine($"\t- {d.Help}");
        }
    }

    void LockQueues(Action callback)
    {
        lock (failing!)
            lock (running!)
                lock (pending!)
                    callback();
    }
};

WriteLine("Type !help to see a list of available commands, or just type a prompt to get started.");
while (!cts.IsCancellationRequested)
{
    Console.Write("dalle-mini> ");
    var prompt = Console.ReadLine();
    if (cts.IsCancellationRequested || prompt == null) // prompt = null when Ctrl+C is pressed, so cts.IsCancellationRequested is about to become true
        break;
    if (string.IsNullOrWhiteSpace(prompt))
        continue;
    if (!runCommands(prompt))
    {
        WriteLine($"Enqueued: {prompt}");
        prompts.Enqueue(prompt);
        promptAvailable.Set();
    }
}

if (!workerManagerTask.IsCompleted)
    await workerManagerTask;

static void WriteLine(string msg, string tag = "SYS", ConsoleColor fg = ConsoleColor.Gray)
{
    Console.ForegroundColor = fg;
    Console.WriteLine($"{tag} {msg}");
    Console.ForegroundColor = ConsoleColor.White;
}

public readonly record struct Worker(string Prompt, DateTime StartedOn, DateTime? EndedOn, CancellationTokenSource KillSource);