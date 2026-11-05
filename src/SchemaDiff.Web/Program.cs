var builder = WebApplication.CreateBuilder(args);

// The tool is single-user and local. It only ever listens on the loopback interface.
builder.WebHost.UseUrls("http://127.0.0.1:5290");

var app = builder.Build();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

app.Run();
