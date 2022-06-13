
using Microsoft.Toolkit.Uwp.Notifications;

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

    public void Show()
    {
        var builder = new ToastContentBuilder()
            .AddText(Title)
            .AddText(Prompt);
        if (Image != null)
            builder = builder.AddInlineImage(new Uri(Image));
        builder.Show();
    }
}
