public sealed class DallEServiceSettings
{
    public int MaxWorkers { get; set; } = 3;
    public string OutputDirectory { get; set; } = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "images\\");
    public TimeSpan RetryTimeout { get; set; } = TimeSpan.FromMinutes(4);

    public DallEServiceSettings()
    {
    }
}
