using System.Net;
using System.Net.Http.Json;
using SchemaDiff.Core.Connections;

namespace SchemaDiff.Web.Tests;

public sealed class ConnectionEndpointTests : IClassFixture<WebAppFactory>
{
    private readonly WebAppFactory _factory;
    private readonly HttpClient _client;

    public ConnectionEndpointTests(WebAppFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Health_endpoint_answers()
    {
        var response = await _client.GetAsync("/api/health");

        response.EnsureSuccessStatusCode();
    }

    [Theory]
    [InlineData("/api/connections/test")]
    [InlineData("/api/connections/databases")]
    public async Task A_server_name_is_required(string url)
    {
        var response = await _client.PostAsJsonAsync(url, new ConnectionDto(Server: " "));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Server name is required", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_failed_connection_test_is_reported_in_the_body()
    {
        var response = await _client.PostAsJsonAsync("/api/connections/test",
            new ConnectionDto(Server: "tcp:127.0.0.1,1", Authentication: "SqlLogin", UserName: "x", Password: "y"));

        response.EnsureSuccessStatusCode();
        var probe = await response.Content.ReadFromJsonAsync<ServerProbe>();
        Assert.False(probe!.Ok);
        Assert.False(string.IsNullOrWhiteSpace(probe.Error));
    }

    [Fact]
    public async Task Recent_connections_never_include_the_password()
    {
        _factory.Recent.Remember(new SqlConnectionInfo
        {
            Server = "db01",
            Database = "Sales",
            Authentication = SqlAuthentication.SqlLogin,
            UserName = "deployer",
            Password = "S3cret!pass",
        }, rememberPassword: true);

        var json = await _client.GetStringAsync("/api/connections/recent");

        Assert.Contains("db01.Sales", json);
        Assert.Contains("\"hasStoredPassword\":true", json);
        Assert.DoesNotContain("S3cret", json);
    }

    [Fact]
    public async Task A_recent_connection_can_be_forgotten()
    {
        _factory.Recent.Remember(new SqlConnectionInfo { Server = "to-forget" }, rememberPassword: false);
        var id = _factory.Recent.All.First(e => e.Server == "to-forget").Id;

        var response = await _client.DeleteAsync($"/api/connections/recent/{id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.DoesNotContain(_factory.Recent.All, e => e.Server == "to-forget");
    }

    [Fact]
    public void A_saved_password_is_used_when_the_dialog_sends_none()
    {
        _factory.Recent.Remember(new SqlConnectionInfo
        {
            Server = "db02",
            Authentication = SqlAuthentication.SqlLogin,
            UserName = "deployer",
            Password = "Stored!1",
        }, rememberPassword: true);
        var id = _factory.Recent.All.First(e => e.Server == "db02").Id;

        var info = ConnectionMapping.ToConnectionInfo(
            new ConnectionDto(Server: " db02 ", Authentication: "sqllogin", UserName: "deployer", Id: id), _factory.Recent);

        Assert.Equal("db02", info.Server);
        Assert.Equal(SqlAuthentication.SqlLogin, info.Authentication);
        Assert.Equal("Stored!1", info.Password);
    }
}
