namespace SchemaDiff.Cli;

/// <summary>The streams a command talks to. Tests pass string readers and writers instead of the real console.</summary>
internal sealed record CliConsole(TextReader In, TextWriter Out, TextWriter Error, bool InputRedirected)
{
    public static CliConsole System => new(Console.In, Console.Out, Console.Error, Console.IsInputRedirected);
}
