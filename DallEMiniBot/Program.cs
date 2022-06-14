using System.Collections.Concurrent;
using System.Text.RegularExpressions;

var settings = (
    // The maximum number of parallel workers. See also !max_workers
    MaxWorkers: 3,
    // The folder where the output of this bot will be stored. See also !output_dir
    OutputDirectory: Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "images\\"),
    // The amount of time after which a waiting request will be dropped and recycled. See also !retry_timeout
    RetryTimeout: TimeSpan.FromMinutes(5)
);
var promptAvailable = new AutoResetEvent(false);
var prompts = new ConcurrentQueue<string>();
var awaiting = new HashSet<(string Prompt, DateTime StartedOn)>();
var processing = new HashSet<(string Prompt, DateTime StartedOn)>();
var cts = new CancellationTokenSource();

Notification.SetupEvents();
Console.CancelKeyPress += (s, e) =>
{
    e.Cancel = true;
    cts.Cancel(true);
    promptAvailable.Set();
};

var getBackgroundWorker = async (string prompt, Action onRequestHeld) =>
{
    var rand = new Random();
    var client = new DallEClient();

    var maybeImages = default(IEnumerable<byte[]>?);
    while (!cts.IsCancellationRequested)
    {
        var newCts = new CancellationTokenSource();
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(newCts.Token, cts.Token);

        var request = client.TryGetImages(prompt!, onRequestHeld, linkedCts.Token);
        var waitAny = Task.WaitAny(new[] { request, Task.Delay(settings.RetryTimeout, cts.Token) }, cts.Token);

        switch (waitAny)
        {
            // If the request ended before the timeout, that's good
            default:
                maybeImages = request.Result;
                break;
            // If we're left hanging for several minutes, drop this request
            case 1:
                new Notification("Retrying", prompt).Show();
                prompts.Enqueue(prompt);
                promptAvailable.Set();
                newCts.Cancel(true);
                return;
        }

        if (maybeImages is null)
        {
            await Task.Delay(rand.Next(25, 75), cts.Token);
            continue;
        }

        break;
    }

    if (cts.IsCancellationRequested)
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
        await Save(image, fn, cts.Token);
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
            semaphore.Wait();
            new Notification("Running worker", prompt).Show();
            _ = Task.Run(async () =>
            {
                var awaitingDesc = (prompt, DateTime.Now);
                lock (awaiting) awaiting.Add(awaitingDesc);
                await getBackgroundWorker(prompt, () =>
                {
                    lock (awaiting)
                        awaiting.Remove(awaitingDesc);
                    lock (processing)
                        processing.Add(awaitingDesc);

                    new Notification("Generation in progress", prompt!).Show();
                });
                processing.Remove(awaitingDesc);
                semaphore.Release();
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
                Help: "!retry_timeout: gets or sets the amount of time after which a waiting request will be dropped and recycled (in seconds).")
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
        lock (awaiting)
        {
            if (awaiting.Count == 0)
            {
                WriteLine($"There are no retrying workers.");
            }
            else
            {
                WriteLine($"Retrying:");
                foreach (var worker in awaiting.OrderBy(x => x.StartedOn))
                    WriteLine($"\t- {worker.Prompt} (Elapsed: {DateTime.Now - worker.StartedOn:hh\\:mm\\:ss})");
            }
        }

        lock (processing)
        {
            if (processing.Count == 0)
            {
                WriteLine($"There are no waiting workers.");
            }
            else
            {
                WriteLine($"Waiting:");
                foreach (var worker in processing.OrderBy(x => x.StartedOn))
                    WriteLine($"\t- {worker.Prompt} (Elapsed: {DateTime.Now - worker.StartedOn:hh\\:mm\\:ss})");
            }
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
        WriteLine($"Max workers: {oldVal}->{newVal}. This change will only reflect once all workers terminate.");
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

    void UnknownCommand(Match m) => Console.WriteLine($"Unknown command: {m.Groups[1].Value}");
    void ListCommands(Match m)
    {
        WriteLine($"Defined commands:");
        foreach (var d in definitions)
        {
            WriteLine($"\t- {d.Help}");
        }
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

static void WriteLine(string msg)
{
    Console.ForegroundColor = ConsoleColor.Gray;
    Console.WriteLine($"SYS {msg}");
    Console.ForegroundColor = ConsoleColor.White;
}