using Newtonsoft.Json;
using System.Net;
using System.Text;

public sealed class DallEClient : IDisposable
{
    private readonly HttpClient HttpClient;
    public DallEClient()
    {
        HttpClient = new()
        {
            BaseAddress = new("https://bf.dallemini.ai"),
        };
        HttpClient.DefaultRequestHeaders.Add("Accept", "application/json");
        HttpClient.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate, br");
        HttpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:101.0) Gecko/20100101 Firefox/101.0");
    }

    public void Dispose() => ((IDisposable)HttpClient).Dispose();

    public async Task<IEnumerable<byte[]>?> TryGetImages(string query, TimeSpan timeout, Action? onRequestHeld = null, CancellationToken ct = default, int requestHoldDelayMs = 10000)
    {
        HttpClient.Timeout = timeout;

        var jsonQuery = JsonConvert.SerializeObject(new { prompt = query });
        var requestTask = HttpClient.PostAsync("/generate", new StringContent(jsonQuery, Encoding.UTF8, "application/json"), ct);
        var delayTask = Task.Delay(requestHoldDelayMs, ct);
        var waitAny = Task.WaitAny(new[] { requestTask, delayTask }, ct);

        var response = default(HttpResponseMessage?);
        switch (waitAny)
        {
            // If the request ended so soon, it's probably an error
            default:
                response = requestTask.Result;
                break;
            // If enough time passes and we're getting no errors, assume that the request went through and notify the caller
            case 1:
                onRequestHeld?.Invoke();
                response = await requestTask;
                break;
        }

        try
        {
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex) when (ex.StatusCode switch
        {
            HttpStatusCode.TooManyRequests => true,
            HttpStatusCode.ServiceUnavailable => true,
            _ => false
        })
        {
            return null;
        }

        var content = await response.Content.ReadAsStringAsync(ct);
        var jsonResponse = JsonConvert.DeserializeAnonymousType(content, new { images = default(string[])! })
            ?? throw new NotSupportedException("Response model changed");
        return Inner(jsonResponse.images);

        static IEnumerable<byte[]> Inner(string[] images)
        {
            foreach (var base64EncodedBlob in images)
            {
                var binaryBlob = Convert.FromBase64String(base64EncodedBlob);
                yield return binaryBlob;
            }
        }
    }

}