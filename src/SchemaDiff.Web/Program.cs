using System.Text.Json;
using SchemaDiff.Core.Connections;
using SchemaDiff.Web;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(HostSettings.ListenUrl(args));
builder.Services.AddSingleton(_ => RecentConnections.ForCurrentUser());
builder.Services.AddSingleton(_ => new CompareService());

var app = builder.Build();

app.UseDefaultFiles();

// Static files are revalidated on every request. With the default heuristic caching the browser may
// serve an old app.js, and a fixed bug would still look broken. "no-cache" does not download the file
// again, it only checks the ETag.
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
        context.Context.Response.Headers.CacheControl = "no-cache, must-revalidate",
});

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

// Connections

app.MapGet("/api/connections/recent", (RecentConnections recent) =>
    Results.Ok(recent.All.Select(ConnectionMapping.ToDto)));

app.MapDelete("/api/connections/recent/{id}", (string id, RecentConnections recent) =>
{
    recent.Forget(id);
    return Results.NoContent();
});

app.MapPost("/api/connections/test", async (ConnectionDto dto, RecentConnections recent, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(dto.Server))
        return Results.BadRequest(new { error = "Server name is required." });

    var probe = await SqlServerExplorer.TestAsync(ConnectionMapping.ToConnectionInfo(dto, recent), ct);
    return Results.Json(probe, json);
});

app.MapPost("/api/connections/databases", async (ConnectionDto dto, RecentConnections recent, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(dto.Server))
        return Results.BadRequest(new { error = "Server name is required." });

    try
    {
        var databases = await SqlServerExplorer.ListDatabasesAsync(ConnectionMapping.ToConnectionInfo(dto, recent), ct);
        return Results.Ok(databases);
    }
    catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or InvalidOperationException)
    {
        return Results.BadRequest(new { error = ex.Message.Split('\n')[0].Trim() });
    }
});

// Comparison

app.MapPost("/api/compare", (CompareRequest request, CompareService compare, RecentConnections recent) =>
{
    if (string.IsNullOrWhiteSpace(request.Source?.Server) || string.IsNullOrWhiteSpace(request.Target?.Server))
        return Results.BadRequest(new { error = "Source and target server are required." });

    if (string.IsNullOrWhiteSpace(request.Source.Database) || string.IsNullOrWhiteSpace(request.Target.Database))
        return Results.BadRequest(new { error = "Select a source and a target database." });

    var source = ConnectionMapping.ToConnectionInfo(request.Source, recent);
    var target = ConnectionMapping.ToConnectionInfo(request.Target, recent);

    recent.Remember(source, request.Source.RememberPassword);
    recent.Remember(target, request.Target.RememberPassword);

    var session = compare.Start(source, target, request.Options ?? new CompareOptionsDto());
    return Results.Ok(new { runId = session.Id, source = source.Label, target = target.Label });
});

app.MapGet("/api/runs/{id}", (string id, CompareService compare) =>
{
    var session = compare.Get(id);
    if (session is null) return Results.NotFound();
    if (session.Error is not null) return Results.Ok(new { finished = true, error = session.Error });
    return Results.Ok(new { finished = session.Finished, result = session.Dto });
});

// The tab was closed. Everything the session holds (two full snapshots and the result) is released
// and a running comparison is cancelled. The browser calls this with navigator.sendBeacon while the
// tab closes. A beacon never reads the response, so the answer is always 204, and a session that no
// longer exists counts as already gone.
app.MapPost("/api/runs/{id}/dispose", (string id, CompareService compare) =>
{
    compare.Dispose(id);
    return Results.NoContent();
});

// The tab is still open. This is the only way to tell apart sessions whose close notification
// never arrives because the browser crashed or was killed.
app.MapPost("/api/runs/{id}/touch", (string id, CompareService compare) =>
{
    var session = compare.Get(id);
    if (session is null) return Results.NotFound();
    session.Touch();
    return Results.NoContent();
});

app.MapPost("/api/runs/{id}/cancel", (string id, CompareService compare) =>
{
    var session = compare.Get(id);
    if (session is null) return Results.NotFound();
    session.Cancel();
    return Results.Ok(new { cancelled = true });
});

// Progress of a comparison as Server-Sent Events: "progress" while it runs, then a single
// "result" or "failed" event, after which the stream ends.
app.MapGet("/api/runs/{id}/events", async (string id, HttpContext context, CompareService compare, CancellationToken ct) =>
{
    var session = compare.Get(id);
    if (session is null)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    ServerSentEvents.Prepare(context.Response);

    try
    {
        while (!ct.IsCancellationRequested)
        {
            // Take the signal before reading the state, otherwise a run that finishes in between is missed.
            var changed = session.Changed;

            if (session.Finished)
            {
                if (session.Error is not null)
                    await ServerSentEvents.WriteAsync(context.Response, "failed", new { error = session.Error }, json, ct);
                else if (session.Dto is { } dto)
                    await ServerSentEvents.WriteAsync(context.Response, "result", dto, json, ct);
                break;
            }

            await ServerSentEvents.WriteAsync(context.Response, "progress", new
            {
                source = session.SourceLabel,
                target = session.TargetLabel,
                percent = session.Percent,
                etaSeconds = session.EtaSeconds,
                phase = session.Phase,
            }, json, ct);

            await changed.WaitAsync(ct);
        }
    }
    catch (OperationCanceledException)
    {
        // The browser closed the stream. The comparison goes on in the background.
    }
});

app.Run();

/// <summary>Entry point type, public so integration tests can host the app in memory.</summary>
public partial class Program;
