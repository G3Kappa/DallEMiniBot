using Microsoft.Extensions.Options;

Notification.SetupEvents();
var cts = new CancellationTokenSource();
var service = new DallEService(Options.Create(new DallEServiceSettings()), new DallEServiceHost());
Console.CancelKeyPress += (s, e) =>
{
    e.Cancel = true;
    cts.Cancel(true);
};

var workerManagerTask = service.Start(cts.Token);
service.Host.WriteLine("Type !help to see a list of available commands, or just type a prompt to get started.");
while (!cts.IsCancellationRequested)
{
    Console.Write("dalle-mini> ");
    var prompt = Console.ReadLine();
    if (cts.IsCancellationRequested || prompt == null) // prompt = null when Ctrl+C is pressed, so cts.IsCancellationRequested is about to become true
        break;
    if (string.IsNullOrWhiteSpace(prompt))
        continue;
    var promptEnqueued = service.Parse(prompt);
}

if (!workerManagerTask.IsCompleted)
    await workerManagerTask;

public sealed class DallEServiceHost : IDallEServiceHost
{
    public void Notification(string title, string prompt, string? thumbnail = null) => new Notification(title, prompt, thumbnail).Show();
    public void WriteLine(string msg, string tag = "SYS", ConsoleColor fg = ConsoleColor.Gray)
    {
        Console.ForegroundColor = fg;
        Console.WriteLine($"{tag} {msg}");
        Console.ForegroundColor = ConsoleColor.White;
    }
}