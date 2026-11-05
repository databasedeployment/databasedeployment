namespace SchemaDiff.Web;

public static class HostSettings
{
    public const int DefaultPort = 5290;

    /// <summary>Port from <c>--port</c> or <c>-p</c>, otherwise the default.</summary>
    public static int ResolvePort(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] is "--port" or "-p" && int.TryParse(args[i + 1], out var parsed))
                return parsed;

        return DefaultPort;
    }

    /// <summary>
    /// There is no authentication and connection details live on the server, so the backend must only
    /// be reachable from the local machine. Binding to 0.0.0.0 would let anyone on the network run
    /// queries with these connections.
    /// </summary>
    public static string ListenUrl(string[] args) => $"http://127.0.0.1:{ResolvePort(args)}";
}
