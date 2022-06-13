
using Microsoft.Toolkit.Uwp.Notifications;
using System.Collections.Concurrent;

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (s, e) => cts.Cancel(false);

var promptAvailable = new AutoResetEvent(false);

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
        if ((maybeImages = await client.TryGetImages(prompt!)) is null)
        {
            // Wait for a randomized amount of time to hide the fact that this is a bot
            await Task.Delay(rand.Next(250, 1000));
            // Also, occasionally stop spamming requests for a longer while 
            if (rand.Next(25) == 0)
            {
                //await Task.Delay(rand.Next(1000, 5000));
            }

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

    ResultNotification(prompt!, baseFn, writeToConsole: false);

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
            GenericNotification("Running", prompt, writeToConsole: false);
            _ = Task.Run(async () =>
            {
                await getBackgroundWorker(prompt);
                semaphore.Release();
            });
        }
    }
});

while (!cts.IsCancellationRequested)
{
    Console.Write("dalle-mini> ");
    var prompt = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(prompt))
        continue;
    GenericNotification("Enqueued", prompt, writeToConsole: true);
    prompts.Enqueue(prompt);
    promptAvailable.Set();
}

static void ResultNotification(string prompt, string fn, bool writeToConsole)
{
    var title = "Result";
    if (writeToConsole)
        Console.WriteLine($"{title}: {prompt}");
    new ToastContentBuilder()
        .AddText(title)
        .AddText(prompt)
        .AddInlineImage(new Uri($"{fn} (1).png"))
        .Show();
}

static void GenericNotification(string title, string prompt, bool writeToConsole)
{
    if (writeToConsole)
        Console.WriteLine($"{title}: {prompt}");
    new ToastContentBuilder()
        .AddText(title)
        .AddText(prompt)
        .Show();
}