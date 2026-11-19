using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SchemaDiff.Core.Connections;

namespace SchemaDiff.Web.Tests;

/// <summary>
/// Hosts the web backend in memory. The recent connections store points to a temporary file and
/// uses a fake protector, so tests never touch the real profile of the user.
/// </summary>
public sealed class WebAppFactory : WebApplicationFactory<Program>
{
    public string StorePath { get; } = Path.Combine(Path.GetTempPath(), $"schemadiff-web-{Guid.NewGuid():N}", "recent.json");

    public RecentConnections Recent => Services.GetRequiredService<RecentConnections>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.AddSingleton(new RecentConnections(StorePath, new FakeProtector()));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        var directory = Path.GetDirectoryName(StorePath)!;
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private sealed class FakeProtector : ISecretProtector
    {
        public string Protect(string secret) => "enc:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(secret));

        public string? Unprotect(string value) =>
            value.StartsWith("enc:", StringComparison.Ordinal) ? Encoding.UTF8.GetString(Convert.FromBase64String(value[4..])) : null;
    }
}
