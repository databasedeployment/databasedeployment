using System.Text.Json;
using SchemaDiff.Core.Connections;
using SchemaDiff.Web;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:5290");
builder.Services.AddSingleton(_ => RecentConnections.ForCurrentUser());

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

app.Run();

/// <summary>Entry point type, public so integration tests can host the app in memory.</summary>
public partial class Program;
