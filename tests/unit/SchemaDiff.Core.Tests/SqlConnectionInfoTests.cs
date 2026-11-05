using Microsoft.Data.SqlClient;
using SchemaDiff.Core.Connections;

namespace SchemaDiff.Core.Tests;

public class SqlConnectionInfoTests
{
    private static SqlConnectionStringBuilder Parse(SqlConnectionInfo info, string? database = null) =>
        new(info.ToConnectionString(database));

    [Fact]
    public void Windows_authentication_uses_integrated_security()
    {
        var builder = Parse(new SqlConnectionInfo { Server = "localhost", Database = "Sales" });

        Assert.True(builder.IntegratedSecurity);
        Assert.Equal("localhost", builder.DataSource);
        Assert.Equal("Sales", builder.InitialCatalog);
        Assert.Equal("SchemaDiff", builder.ApplicationName);
    }

    [Fact]
    public void Sql_login_passes_user_and_password()
    {
        var builder = Parse(new SqlConnectionInfo
        {
            Server = "db01",
            Authentication = SqlAuthentication.SqlLogin,
            UserName = "deployer",
            Password = "secret",
        });

        Assert.False(builder.IntegratedSecurity);
        Assert.Equal("deployer", builder.UserID);
        Assert.Equal("secret", builder.Password);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Encrypt_option_maps_to_mandatory_or_optional(bool encrypt)
    {
        var builder = Parse(new SqlConnectionInfo { Server = "db01", Encrypt = encrypt });

        Assert.Equal(encrypt ? SqlConnectionEncryptOption.Mandatory : SqlConnectionEncryptOption.Optional, builder.Encrypt);
    }

    [Fact]
    public void Database_can_be_overridden_for_server_level_queries()
    {
        var builder = Parse(new SqlConnectionInfo { Server = "db01", Database = "Sales" }, "master");

        Assert.Equal("master", builder.InitialCatalog);
    }

    [Fact]
    public void Key_and_label_never_contain_the_password()
    {
        var info = new SqlConnectionInfo
        {
            Server = "db01",
            Database = "Sales",
            Authentication = SqlAuthentication.SqlLogin,
            UserName = "deployer",
            Password = "secret",
        };

        Assert.DoesNotContain("secret", info.Key);
        Assert.DoesNotContain("secret", info.Label);
        Assert.Equal("db01.Sales", info.Label);
    }
}
