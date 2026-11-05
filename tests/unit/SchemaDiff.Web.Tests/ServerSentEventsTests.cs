using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace SchemaDiff.Web.Tests;

public class ServerSentEventsTests
{
    [Fact]
    public async Task Events_follow_the_text_event_stream_format()
    {
        var context = new DefaultHttpContext();
        var body = new MemoryStream();
        context.Response.Body = body;

        ServerSentEvents.Prepare(context.Response);
        await ServerSentEvents.WriteAsync(context.Response, "progress", new { percent = 42, phase = "Extracting" },
            new JsonSerializerOptions(JsonSerializerDefaults.Web), CancellationToken.None);

        Assert.Equal("text/event-stream", context.Response.ContentType);
        Assert.Equal("no-cache", context.Response.Headers.CacheControl.ToString());
        Assert.Equal("event: progress\ndata: {\"percent\":42,\"phase\":\"Extracting\"}\n\n", Encoding.UTF8.GetString(body.ToArray()));
    }

    [Theory]
    [InlineData(new string[0], 5290)]
    [InlineData(new[] { "--port", "6100" }, 6100)]
    [InlineData(new[] { "-p", "6200" }, 6200)]
    [InlineData(new[] { "--port", "nope" }, 5290)]
    public void Port_comes_from_the_command_line_or_defaults_to_5290(string[] args, int expected)
    {
        Assert.Equal(expected, HostSettings.ResolvePort(args));
    }

    [Fact]
    public void The_backend_only_listens_on_the_loopback_interface()
    {
        Assert.Equal("http://127.0.0.1:6100", HostSettings.ListenUrl(["--port", "6100"]));
    }
}
