using System.Text.Json;

namespace SchemaDiff.Web;

/// <summary>
/// Writes Server-Sent Events. Progress of a comparison or a script run is pushed to the browser
/// as it happens, which an EventSource on the page reads without polling.
/// </summary>
public static class ServerSentEvents
{
    public static void Prepare(HttpResponse response)
    {
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        // Reverse proxies must not buffer the stream, otherwise events arrive in bursts.
        response.Headers["X-Accel-Buffering"] = "no";
    }

    public static async Task WriteAsync(
        HttpResponse response, string name, object payload, JsonSerializerOptions options, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload, options);
        await response.WriteAsync($"event: {name}\ndata: {json}\n\n", ct);
        await response.Body.FlushAsync(ct);
    }
}
