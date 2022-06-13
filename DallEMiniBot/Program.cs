using System.Collections.Concurrent;
using System.Text.RegularExpressions;

var settings = (MaxWorkers: 3, OutputDirectory: Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "images/"));

var promptAvailable = new AutoResetEvent(false);
var prompts = new ConcurrentQueue<string>();
var awaiting = new HashSet<string>();
var cts = new CancellationTokenSource();

Notification.SetupEvents();
Console.CancelKeyPress += (s, e) =>
{
    e.Cancel = true;
    cts.Cancel(false);
    promptAvailable.Set();
};

var getBackgroundWorker = async (string prompt) =>
{
    var rand = new Random();
    var client = new DallEClient();

    var maybeImages = default(IEnumerable<byte[]>?);
    while (!cts.IsCancellationRequested)
    {
        if ((maybeImages = await client.TryGetImages(prompt!, cts.Token)) is null)
        {
            // Wait for a randomized amount of time to hide the fact that this is a bot
            await Task.Delay(rand.Next(250, 1000), cts.Token);
            continue;
        }

        break;
    }

    if (cts.IsCancellationRequested)
        return;

    var outputDir = settings.OutputDirectory;
    if (!Directory.Exists(outputDir))
        Directory.CreateDirectory(outputDir);

    var baseFn = Path.Combine(outputDir, new SanitizedFileName(prompt!, replacement: "_").Value);
    foreach (var (image, index) in maybeImages!.Select((e, i) => (e, i)))
    {
        var fn = $"{baseFn} ({index + 1}).png";
        await Save(image, fn, cts.Token);
    }

    new Notification("Generated", prompt!, $"{baseFn} (1).png").Show();

    static async Task Save(ReadOnlyMemory<byte> image, string fn, CancellationToken ct)
    {
        using var fs = File.OpenWrite(fn);
        await fs.WriteAsync(image, ct);
    }
};

Task.Run(() =>
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
            new Notification("Running", prompt).Show();
            _ = Task.Run(async () =>
            {
                lock (awaiting) awaiting.Add(prompt);
                await getBackgroundWorker(prompt);
                lock (awaiting) awaiting.Remove(prompt);
                semaphore.Release();
            }, cts.Token);
        }
    }
});

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

var runCommands = (string prompt) =>
{
    var definitions = new List<(Func<bool> Command, string Help)>
    {
        {
            (Command: () => command(prompt)(new(@"^\s*!workers\s*$", RegexOptions.Compiled), PrintRunningWorkers),
            Help: "!workers: shows the list of running workers")
        },
        {
            (Command: () => command(prompt)(new(@"^\s*::max_workers\s+(\d+)?\s*$", RegexOptions.Compiled), GetOrSetMaxWorkers),
            Help: "!max_workers: gets or sets the maximum amount of running workers")
        },
    };

    definitions.Add(
        (Command: () => command(prompt)(new(@"^\s*(!help)\s*$", RegexOptions.Compiled), ListCommands),
            Help: "!help: shows this list"));

    return definitions.Any(d => d.Command())
        || command(prompt)(new(@"^\s*::([^\s]*).*?\s*$", RegexOptions.Compiled), UnknownCommand)
        ;

    void PrintRunningWorkers(Match _)
    {
        lock (awaiting)
        {
            if (awaiting.Count == 0)
            {
                WriteLine($"There are no running workers.");
            }
            else
            {
                WriteLine($"Running workers:");
                foreach (var worker in awaiting)
                    WriteLine($"\t- {worker}");
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

        var newVal = settings.MaxWorkers = int.Parse(m.Groups[1].Value);
        WriteLine($"Max workers: {oldVal}->{newVal}");
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
    if (string.IsNullOrWhiteSpace(prompt))
        continue;
    if (cts.IsCancellationRequested)
        break;
    if (!runCommands(prompt))
    {
        WriteLine($"Enqueued: {prompt}");
        prompts.Enqueue(prompt);
        promptAvailable.Set();
        new Notification("Enqueued", prompt).Show();
    }
}

static void WriteLine(string msg)
{
    Console.ForegroundColor = ConsoleColor.Gray;
    Console.WriteLine($"SYS {msg}");
    Console.ForegroundColor = ConsoleColor.White;
}