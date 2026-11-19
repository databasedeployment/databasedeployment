using SchemaDiff.Core.Connections;

namespace SchemaDiff.Web;

public static class ConnectionMapping
{
    /// <summary>
    /// Turns the fields of the connect dialog into a connection. When the dialog sends no password
    /// for a SQL login, the password saved for that recent connection is used on the server side.
    /// </summary>
    public static SqlConnectionInfo ToConnectionInfo(ConnectionDto dto, RecentConnections recent)
    {
        var authentication = string.Equals(dto.Authentication, "SqlLogin", StringComparison.OrdinalIgnoreCase)
            ? SqlAuthentication.SqlLogin
            : SqlAuthentication.Windows;

        var password = dto.Password;
        if (string.IsNullOrEmpty(password) && authentication == SqlAuthentication.SqlLogin)
            password = recent.ResolvePassword(dto.Id);

        return new SqlConnectionInfo
        {
            Server = dto.Server.Trim(),
            Database = string.IsNullOrWhiteSpace(dto.Database) ? null : dto.Database.Trim(),
            Authentication = authentication,
            UserName = dto.UserName,
            Password = password,
            Encrypt = dto.Encrypt,
            TrustServerCertificate = dto.TrustServerCertificate,
        };
    }

    public static RecentConnectionDto ToDto(StoredConnection e) => new(
        e.Id, e.Server, e.Database, e.Authentication.ToString(), e.UserName,
        e.Encrypt, e.TrustServerCertificate, e.HasStoredPassword,
        string.IsNullOrWhiteSpace(e.Database) ? e.Server : $"{e.Server}.{e.Database}",
        e.LastUsed);
}
