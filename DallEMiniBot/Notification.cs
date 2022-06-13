
using Microsoft.Toolkit.Uwp.Notifications;
using System.Diagnostics;

public readonly struct Notification
{
    public readonly string Title;
    public readonly string Prompt;
    public readonly string? Image;

    public Notification(string title, string prompt, string? image = null)
    {
        Title = title;
        Prompt = prompt;
        Image = image;
    }

    public static void SetupEvents()
    {
        ToastNotificationManagerCompat.OnActivated += (source) =>
        {
            var args = source.Argument.Split(';')
                .Select(eq => eq.Split('='))
                .ToDictionary(s => s[0], s => s[1]);
            if (args.TryGetValue("Preview", out var previewFile) && File.Exists(previewFile))
            {
                var argument = $"/select, \"{previewFile}\"";
                Process.Start("explorer.exe", argument);
            }
        };
    }

    public void Show()
    {
        var builder = new ToastContentBuilder()
            .AddText(Title)
            .AddText(Prompt);
        if (Image != null)
        {
            builder = builder.AddInlineImage(new Uri(Image));
            builder.AddArgument("Preview", Image);
        }

        builder.Show();
    }
}
