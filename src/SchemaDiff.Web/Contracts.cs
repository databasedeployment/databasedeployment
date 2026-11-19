namespace SchemaDiff.Web;

// Types exchanged with the browser. A password only ever travels from the client to the server
// and is never part of any response.

public sealed record ConnectionDto(
    string Server,
    string? Database = null,
    string Authentication = "Windows",
    string? UserName = null,
    string? Password = null,
    bool RememberPassword = false,
    bool Encrypt = false,
    bool TrustServerCertificate = true,
    string? Id = null);

public sealed record RecentConnectionDto(
    string Id,
    string Server,
    string? Database,
    string Authentication,
    string? UserName,
    bool Encrypt,
    bool TrustServerCertificate,
    bool HasStoredPassword,
    string Label,
    DateTimeOffset LastUsed);
