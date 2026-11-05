using JobAlertAgent.Infrastructure;

namespace JobAlertAgent.Sources;

/// <summary>Looks up the <see cref="IJobSource"/> implementation for a provider name.</summary>
public sealed class SourceRegistry
{
    private readonly Dictionary<string, IJobSource> _byProvider;

    public SourceRegistry(IEnumerable<IJobSource> sources) =>
        _byProvider = sources.ToDictionary(s => s.Provider, StringComparer.OrdinalIgnoreCase);

    public IJobSource Get(string provider) =>
        _byProvider.TryGetValue(provider, out var s)
            ? s
            : throw new InvalidOperationException($"No source implementation for provider '{provider}'.");

    /// <summary>Creates the registry with all built-in providers sharing one cookie-enabled client.</summary>
    public static SourceRegistry CreateDefault(HttpClient client)
    {
        var http = new ResilientHttp(client);
        return new SourceRegistry(new IJobSource[]
        {
            new GreenhouseSource(http),
            new AshbySource(http),
            new LeverSource(http),
            new WorkableSource(http),
            new WorkdaySource(http),
            new AmazonSource(http),
            new MicrosoftSource(http),
        });
    }

    public static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            UseCookies = true,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(120) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; JobAlertAgent/1.0)");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, text/html, */*");
        return client;
    }
}
