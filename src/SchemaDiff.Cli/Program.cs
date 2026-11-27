using System.Text;
using SchemaDiff.Cli;

// Entry point for the SchemaDiff command line tool. The commands live in CliApp.
Console.OutputEncoding = Encoding.UTF8;

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancel.Cancel();
};

try
{
    return await new CliApp(CliConsole.System).RunAsync(args, cancel.Token);
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled.");
    return CliApp.Failed;
}
