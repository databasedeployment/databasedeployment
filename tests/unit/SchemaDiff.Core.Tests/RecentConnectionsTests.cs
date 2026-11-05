using System.Text;
using SchemaDiff.Core.Connections;

namespace SchemaDiff.Core.Tests;

public sealed class RecentConnectionsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "schemadiff-tests-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_directory, "recent.json");

    /// <summary>Stand-in for DPAPI that works on every platform and is obviously not plain text.</summary>
    private sealed class FakeProtector : ISecretProtector
    {
        public string Protect(string secret) => "enc:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(new string(secret.Reverse().ToArray())));

        public string? Unprotect(string protectedValue) =>
            protectedValue.StartsWith("enc:", StringComparison.Ordinal)
                ? new string(Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue[4..])).Reverse().ToArray())
                : null;
    }

    private RecentConnections Store(ISecretProtector? protector = null) => new(FilePath, protector ?? new FakeProtector());

    private static SqlConnectionInfo SqlLogin(string server, string password = "S3cret!pass") => new()
    {
        Server = server,
        Database = "Sales",
        Authentication = SqlAuthentication.SqlLogin,
        UserName = "deployer",
        Password = password,
    };

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Password_is_never_written_in_plain_text()
    {
        Store().Remember(SqlLogin("db01"), rememberPassword: true);

        var onDisk = File.ReadAllText(FilePath);

        Assert.DoesNotContain("S3cret!pass", onDisk);
        Assert.Contains("deployer", onDisk);
    }

    [Fact]
    public void Remembered_password_can_be_resolved_by_a_new_instance()
    {
        Store().Remember(SqlLogin("db01"), rememberPassword: true);

        var reloaded = Store();
        var entry = Assert.Single(reloaded.All);

        Assert.True(entry.HasStoredPassword);
        Assert.Equal("S3cret!pass", reloaded.ResolvePassword(entry.Id));
    }

    [Fact]
    public void Password_is_dropped_when_the_user_unticks_remember()
    {
        var store = Store();
        store.Remember(SqlLogin("db01"), rememberPassword: true);
        store.Remember(SqlLogin("db01"), rememberPassword: false);

        Assert.False(Assert.Single(store.All).HasStoredPassword);
    }

    [Fact]
    public void Windows_authentication_never_stores_a_password()
    {
        var store = Store();
        store.Remember(new SqlConnectionInfo { Server = "db01", Password = "ignored" }, rememberPassword: true);

        Assert.False(Assert.Single(store.All).HasStoredPassword);
    }

    [Fact]
    public void List_is_most_recent_first_and_capped()
    {
        var store = Store();
        for (var i = 0; i < 25; i++) store.Remember(SqlLogin($"db{i:00}"), rememberPassword: false);

        Assert.Equal(20, store.All.Count);
        Assert.Equal("db24", store.All[0].Server);
    }

    [Fact]
    public void Forget_removes_the_entry_from_disk()
    {
        var store = Store();
        store.Remember(SqlLogin("db01"), rememberPassword: true);
        store.Forget(store.All[0].Id);

        Assert.Empty(Store().All);
    }

    [Fact]
    public void A_corrupt_file_starts_an_empty_list_instead_of_failing()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, "{ this is not json");

        Assert.Empty(Store().All);
    }

    [SkippableFact]
    public void Dpapi_round_trips_for_the_current_user_on_windows()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "DPAPI is only available on Windows.");

        var protector = new DpapiSecretProtector();
        var protectedValue = protector.Protect("S3cret!pass");

        Assert.NotNull(protectedValue);
        Assert.DoesNotContain("S3cret!pass", protectedValue);
        Assert.Equal("S3cret!pass", protector.Unprotect(protectedValue!));
    }

    [Fact]
    public void Dpapi_protector_stores_nothing_on_other_platforms()
    {
        if (OperatingSystem.IsWindows()) return;

        Assert.Null(new DpapiSecretProtector().Protect("S3cret!pass"));
    }
}
