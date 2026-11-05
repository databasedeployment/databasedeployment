using System.IO.Hashing;
using System.Text;
using System.Text.Json;

namespace SchemaDiff.Core.Connections;

public sealed class StoredConnection
{
    public required string Id { get; set; }
    public required string Server { get; set; }
    public string? Database { get; set; }
    public SqlAuthentication Authentication { get; set; }
    public string? UserName { get; set; }
    public bool Encrypt { get; set; }
    public bool TrustServerCertificate { get; set; } = true;
    public DateTimeOffset LastUsed { get; set; }

    /// <summary>Password protected for the current user (base64). Never plain text.</summary>
    public string? ProtectedPassword { get; set; }

    public bool HasStoredPassword => !string.IsNullOrEmpty(ProtectedPassword);
}

/// <summary>
/// Recently used connections, the equivalent of the "Recent Connections" list in SSDT.
///
/// A password is only stored when the user asks for it, and only in protected form. It is
/// resolved on the server side when the user picks a saved connection and is never sent back
/// to the browser.
/// </summary>
public sealed class RecentConnections
{
    private const int MaxEntries = 20;

    private readonly string _path;
    private readonly ISecretProtector _protector;
    private readonly object _gate = new();
    private List<StoredConnection> _entries = [];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public RecentConnections(string path, ISecretProtector protector)
    {
        _path = path;
        _protector = protector;
        Load();
    }

    /// <summary>Store under the application data folder of the current user, protected with DPAPI.</summary>
    public static RecentConnections ForCurrentUser() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SchemaDiff", "recent.json"),
            new DpapiSecretProtector());

    public IReadOnlyList<StoredConnection> All
    {
        get { lock (_gate) return [.. _entries.OrderByDescending(e => e.LastUsed)]; }
    }

    public void Remember(SqlConnectionInfo info, bool rememberPassword)
    {
        var id = MakeId(info.Key);

        lock (_gate)
        {
            var entry = _entries.FirstOrDefault(e => e.Id == id);
            if (entry is null)
            {
                entry = new StoredConnection { Id = id, Server = info.Server };
                _entries.Add(entry);
            }

            entry.Server = info.Server;
            entry.Database = info.Database;
            entry.Authentication = info.Authentication;
            entry.UserName = info.UserName;
            entry.Encrypt = info.Encrypt;
            entry.TrustServerCertificate = info.TrustServerCertificate;
            entry.LastUsed = DateTimeOffset.UtcNow;

            if (rememberPassword && info.Authentication == SqlAuthentication.SqlLogin
                && !string.IsNullOrEmpty(info.Password))
            {
                entry.ProtectedPassword = _protector.Protect(info.Password);
            }
            else if (!rememberPassword)
            {
                entry.ProtectedPassword = null;
            }

            _entries = [.. _entries.OrderByDescending(e => e.LastUsed).Take(MaxEntries)];
            Save();
        }
    }

    /// <summary>Resolves the password of a saved connection on the server side.</summary>
    public string? ResolvePassword(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        lock (_gate)
        {
            var entry = _entries.FirstOrDefault(e => e.Id == id);
            return entry?.ProtectedPassword is null ? null : _protector.Unprotect(entry.ProtectedPassword);
        }
    }

    public void Forget(string id)
    {
        lock (_gate)
        {
            _entries.RemoveAll(e => e.Id == id);
            Save();
        }
    }

    // Persistence

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            _entries = JsonSerializer.Deserialize<List<StoredConnection>>(File.ReadAllText(_path), JsonOptions) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A broken file must not crash the application. The list starts empty and is fixed on the next save.
            _entries = [];
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_entries, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Failing to save the list must never block a comparison.
        }
    }

    private static string MakeId(string key) =>
        Convert.ToHexString(XxHash128.Hash(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
}
