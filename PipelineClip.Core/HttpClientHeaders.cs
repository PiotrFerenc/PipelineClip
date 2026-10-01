namespace PipelineClip.Core;

public static class HttpClientHeaders
{
    public static void Apply(HttpClient client, HttpClientOptions options)
    {
        client.BaseAddress = new Uri(options.BaseAddress);
        client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        foreach (var (name, rawValue) in options.Headers)
        {
            var value = options.ApiKey is null ? rawValue : rawValue.Replace("{ApiKey}", options.ApiKey);
            client.DefaultRequestHeaders.Remove(name);
            client.DefaultRequestHeaders.Add(name, value);
        }
    }
}
