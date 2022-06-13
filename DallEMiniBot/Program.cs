using System.Collections.Concurrent;

var promptAvailable = new AutoResetEvent(false);
var cts = new CancellationTokenSource();
Console.CancelKeyPress += (s, e) =>
{
    e.Cancel = true;
    cts.Cancel(false);
    promptAvailable.Set();
};

var prompts = new ConcurrentQueue<string>();
var outputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "images/");
if (!Directory.Exists(outputDir))
    Directory.CreateDirectory(outputDir);

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
    // Regulates the amount of parallel worker threads
    var semaphore = new SemaphoreSlim(3);
    while (!cts.IsCancellationRequested)
    {
        promptAvailable.WaitOne();
        while (prompts.TryDequeue(out var prompt))
        {
            semaphore.Wait();
            new Notification("Running", prompt).Show();
            _ = Task.Run(async () =>
            {
                await getBackgroundWorker(prompt);
                semaphore.Release();
            }, cts.Token);
        }
    }
});

while (!cts.IsCancellationRequested)
{
    Console.Write("dalle-mini> ");
    var prompt = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(prompt))
        continue;
    if (cts.IsCancellationRequested)
        break;
    new Notification("Enqueued", prompt).Show();
    prompts.Enqueue(prompt);
    promptAvailable.Set();
}