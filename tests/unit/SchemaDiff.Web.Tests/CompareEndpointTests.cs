using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace SchemaDiff.Web.Tests;

public sealed class CompareEndpointTests : IClassFixture<WebAppFactory>
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly WebAppFactory _factory;
    private readonly HttpClient _client;

    public CompareEndpointTests(WebAppFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private static CompareRequest Request(string sourceServer = "dev01", CompareOptionsDto? options = null) => new(
        new ConnectionDto(sourceServer, "Sales", "SqlLogin", "deployer", "Pa55word!", RememberPassword: false),
        new ConnectionDto("prod01", "Sales"),
        options);

    private async Task<string> StartAsync(CompareRequest request)
    {
        var response = await _client.PostAsJsonAsync("/api/compare", request);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("runId").GetString()!;
    }

    private async Task<JsonElement> WaitUntilFinishedAsync(string runId)
    {
        using var timeout = new CancellationTokenSource(Wait);
        while (true)
        {
            var run = await _client.GetFromJsonAsync<JsonElement>($"/api/runs/{runId}", timeout.Token);
            if (run.GetProperty("finished").GetBoolean()) return run;
            await Task.Delay(20, timeout.Token);
        }
    }

    /// <summary>Reads the SSE stream until an event with one of the given names arrives.</summary>
    private async Task<(string Name, JsonElement Data)> ReadEventAsync(StreamReader reader, params string[] names)
    {
        using var timeout = new CancellationTokenSource(Wait);
        string? name = null;
        while (await reader.ReadLineAsync(timeout.Token) is { } line)
        {
            if (line.StartsWith("event: ", StringComparison.Ordinal)) name = line[7..];
            else if (line.StartsWith("data: ", StringComparison.Ordinal) && names.Contains(name))
                return (name!, JsonDocument.Parse(line[6..]).RootElement);
        }
        throw new InvalidOperationException($"The stream ended before any of: {string.Join(", ", names)}");
    }

    private async Task<StreamReader> OpenEventsAsync(string runId)
    {
        var response = await _client.GetAsync($"/api/runs/{runId}/events", HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        return new StreamReader(await response.Content.ReadAsStreamAsync());
    }

    [Fact]
    public async Task Both_servers_are_required()
    {
        var request = Request() with { Target = new ConnectionDto(" ", "Sales") };

        var response = await _client.PostAsJsonAsync("/api/compare", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Source and target server are required", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Both_databases_are_required()
    {
        var request = Request() with { Source = new ConnectionDto("dev01") };

        var response = await _client.PostAsJsonAsync("/api/compare", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Select a source and a target database", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Starting_a_comparison_returns_the_run_and_never_the_password()
    {
        var response = await _client.PostAsJsonAsync("/api/compare", Request());
        var body = await response.Content.ReadAsStringAsync();

        response.EnsureSuccessStatusCode();
        Assert.Contains("\"runId\":", body);
        Assert.Contains("\"source\":\"dev01.Sales\"", body);
        Assert.Contains("\"target\":\"prod01.Sales\"", body);
        Assert.DoesNotContain("Pa55word", body);
    }

    [Fact]
    public async Task Both_sides_are_remembered_without_the_password_unless_asked()
    {
        await StartAsync(Request());

        var dev = Assert.Single(_factory.Recent.All, e => e.Server == "dev01");
        Assert.False(dev.HasStoredPassword);
        Assert.Contains(_factory.Recent.All, e => e.Server == "prod01");
    }

    [Fact]
    public async Task The_result_counts_and_lists_the_differences()
    {
        var run = await WaitUntilFinishedAsync(await StartAsync(Request()));
        var result = run.GetProperty("result");

        Assert.Equal(1, result.GetProperty("addCount").GetInt32());
        Assert.Equal(1, result.GetProperty("changeCount").GetInt32());
        Assert.Equal(1, result.GetProperty("deleteCount").GetInt32());
        Assert.Equal(1, result.GetProperty("equal").GetInt32());
        Assert.False(result.GetProperty("truncated").GetBoolean());
        Assert.Contains("Sales: row counts could not be read", result.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()));

        var changes = result.GetProperty("changes").EnumerateArray().ToList();
        var table = Assert.Single(changes, c => c.GetProperty("action").GetString() == "Change");
        Assert.Equal("Table", table.GetProperty("objectType").GetString());
        Assert.Equal("Customer", table.GetProperty("name").GetString());
        Assert.Equal(["columns"], table.GetProperty("changedParts").EnumerateArray().Select(p => p.GetString()));
        Assert.Contains(changes, c => c.GetProperty("action").GetString() == "Add" && c.GetProperty("objectType").GetString() == "View");
        Assert.Contains(changes, c => c.GetProperty("action").GetString() == "Delete" && c.GetProperty("name").GetString() == "OldProc");
    }

    [Fact]
    public async Task Compare_options_reach_the_comparison()
    {
        var options = new CompareOptionsDto(IgnoreColumnOrder: true, IgnoreWhitespace: false, CaseSensitiveNames: true, MaxQueries: 3);

        await WaitUntilFinishedAsync(await StartAsync(Request("options-check", options)));

        var call = Assert.Single(_factory.Compare.Calls, c => c.Source.Contains("options-check"));
        Assert.True(call.Options.IgnoreColumnOrder);
        Assert.False(call.Options.Normalization.IgnoreWhitespace);
        Assert.True(call.Options.CaseSensitiveNames);
        Assert.True(call.Options.KeepDisplayScripts);
        Assert.Equal(3, call.MaxQueries);
    }

    [Fact]
    public async Task A_finished_run_streams_its_result()
    {
        var runId = await StartAsync(Request());
        await WaitUntilFinishedAsync(runId);

        using var reader = await OpenEventsAsync(runId);
        var (name, data) = await ReadEventAsync(reader, "result", "failed");

        Assert.Equal("result", name);
        Assert.Equal(runId, data.GetProperty("runId").GetString());
    }

    [Fact]
    public async Task A_failed_run_streams_the_first_line_of_the_error()
    {
        var runId = await StartAsync(Request("fails"));

        using var reader = await OpenEventsAsync(runId);
        var (name, data) = await ReadEventAsync(reader, "result", "failed");

        Assert.Equal("failed", name);
        Assert.Equal("Login failed for user 'deployer'.", data.GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_running_comparison_streams_progress_and_can_be_cancelled()
    {
        var runId = await StartAsync(Request("slow-cancel"));
        using var reader = await OpenEventsAsync(runId);

        var (_, progress) = await ReadEventAsync(reader, "progress");
        while (progress.GetProperty("phase").GetString() != "extracting" || progress.GetProperty("percent").GetInt32() == 0)
            (_, progress) = await ReadEventAsync(reader, "progress");
        Assert.Equal("slow-cancel.Sales", progress.GetProperty("source").GetString());

        var cancel = await _client.PostAsync($"/api/runs/{runId}/cancel", null);
        cancel.EnsureSuccessStatusCode();

        var (name, data) = await ReadEventAsync(reader, "failed", "result");
        Assert.Equal("failed", name);
        Assert.Equal("The comparison was cancelled.", data.GetProperty("error").GetString());
        await _factory.Compare.Cancelled("slow-cancel").WaitAsync(Wait);
    }

    [Fact]
    public async Task Disposing_a_session_releases_it_and_stops_its_comparison()
    {
        var runId = await StartAsync(Request("slow-dispose"));

        var response = await _client.PostAsync($"/api/runs/{runId}/dispose", null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/runs/{runId}")).StatusCode);
        await _factory.Compare.Cancelled("slow-dispose").WaitAsync(Wait);
    }

    [Fact]
    public async Task Disposing_an_unknown_session_still_answers_no_content()
    {
        var response = await _client.PostAsync("/api/runs/unknown/dispose", null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Theory]
    [InlineData("GET", "/api/runs/unknown")]
    [InlineData("GET", "/api/runs/unknown/events")]
    [InlineData("POST", "/api/runs/unknown/touch")]
    [InlineData("POST", "/api/runs/unknown/cancel")]
    public async Task An_unknown_run_is_not_found(string method, string url)
    {
        var response = await _client.SendAsync(new HttpRequestMessage(new HttpMethod(method), url));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Sessions_without_a_sign_of_life_are_swept_when_a_new_one_starts()
    {
        var forgotten = await StartAsync(Request());
        var touched = await StartAsync(Request());
        await WaitUntilFinishedAsync(forgotten);
        await WaitUntilFinishedAsync(touched);

        _factory.Compare.Clock.Advance(TimeSpan.FromMinutes(20));
        Assert.Equal(HttpStatusCode.NoContent, (await _client.PostAsync($"/api/runs/{touched}/touch", null)).StatusCode);
        _factory.Compare.Clock.Advance(TimeSpan.FromMinutes(20));

        var fresh = await StartAsync(Request());

        Assert.Null(_factory.Sessions.Get(forgotten));
        Assert.NotNull(_factory.Sessions.Get(touched));
        Assert.NotNull(_factory.Sessions.Get(fresh));
    }
}
