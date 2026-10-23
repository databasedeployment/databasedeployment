using SchemaDiff.Core.Connections;

namespace SchemaDiff.Core.Tests;

public class SqlServerExplorerTests
{
    [Fact]
    public async Task Failed_connection_is_reported_instead_of_thrown()
    {
        // Nothing listens on port 1, so the connection fails quickly on any machine.
        var info = new SqlConnectionInfo { Server = "tcp:127.0.0.1,1", ConnectTimeoutSeconds = 2 };

        var probe = await SqlServerExplorer.TestAsync(info);

        Assert.False(probe.Ok);
        Assert.False(string.IsNullOrWhiteSpace(probe.Error));
        Assert.DoesNotContain('\n', probe.Error);
    }
}
