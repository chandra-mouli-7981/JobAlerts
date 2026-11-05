using System.Net;
using System.Text;

namespace JobAlertAgent.Tests.Helpers;

/// <summary>HttpMessageHandler that answers from a delegate and records every request.</summary>
public sealed class FakeHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

    public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

    public List<string> Requests { get; } = new();
    public List<string?> RequestBodies { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request.RequestUri!.ToString());
        RequestBodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(ct));
        return _respond(request);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Status(HttpStatusCode code) => new(code);

    public static (HttpClient Client, FakeHandler Handler) Create(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new FakeHandler(respond);
        return (new HttpClient(handler), handler);
    }
}
