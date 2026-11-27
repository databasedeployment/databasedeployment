namespace SchemaDiff.Cli;

internal static class HelpText
{
    public static string For(CliCommand? command) => command switch
    {
        CliCommand.Compare => Compare,
        CliCommand.Script => Script,
        CliCommand.List => List,
        CliCommand.Init => Init,
        _ => General,
    };

    private const string JobOptions = """
        Jobs:
          -c, --config <file>       jobs from a jobs.json file (recommended)
          -s, --source <conn>       source connection string, together with --target
          -t, --target <conn>       target connection string, together with --source
              --databases A,B,C     with --source and --target: one job per database name,
                                    for the same database names on two servers
        """;

    private const string SelectionOptions = """
        Selection:
          -j, --jobs <expr>         "1,3" | "2-5" | "NAME1,NAME2" | "1,4-6,NAME" | "all"
          -a, --all                 every job
              --select              choose interactively (the default in a terminal)
        A single job runs without asking. With redirected input a selection is required.
        """;

    private const string ComparisonOptions = """
        Comparison options:
              --case-sensitive         report differences in the casing of object names
              --keep-system-names      compare system generated constraint names too
              --respect-whitespace     count whitespace differences in module bodies
              --respect-comments       count comment differences in module bodies
              --ignore-index-physical  ignore fill factor and padding of indexes
              --ignore-column-order    ignore the order of table columns
              --max-queries <n>        catalog queries running at the same time (default 16)
        """;

    private const string Common = """
        Other:
          -v, --verbose             print the full error with its stack trace
          -h, --help                help for this command
        """;

    public static readonly string General = $"""
        SchemaDiff - SQL Server schema compare

        usage: schemadiff <command> [options]

        Commands:
          compare   compare the selected jobs and list the differences
          script    write a script that creates the objects missing on the target
          list      list the defined jobs
          init      write a starter jobs.json
          help      show help, "schemadiff help <command>" for one command

        A job is a named source and target pair. The two sides may be on different
        servers and may use different database names. The source is what the schema
        should be, the target is the database that gets changed.

        Exit code: 0 when the command completed, 2 on invalid arguments or a failed job.

        Examples:
          schemadiff init jobs.json
          schemadiff list -c jobs.json
          schemadiff compare -c jobs.json --jobs "1,3"
          schemadiff compare -s "<conn>" -t "<conn>" --databases Sales,Billing --all
          schemadiff script -c jobs.json -j Sales --out sales.sql
        """;

    public static readonly string Compare = $"""
        usage: schemadiff compare (--config <file> | --source <conn> --target <conn>) [options]

        Compares each selected job and prints the number of equal, added, removed, changed
        and indeterminate objects, followed by the differences. Objects whose definition
        cannot be read (encrypted, no VIEW DEFINITION) are indeterminate, never equal.

        {JobOptions}

        {SelectionOptions}

        Output:
          -d, --details <n>         differences listed per job (default 30, 0 = none)

        {ComparisonOptions}

        {Common}
        """;

    public static readonly string Script = $"""
        usage: schemadiff script (--config <file> | --source <conn> --target <conn>) --out <file> [options]

        Compares each selected job and writes a script that creates the objects which exist
        in the source but not in the target, in dependency order. Every changed, removed or
        indeterminate object that the script does not cover is listed by name.
        With more than one job the job name goes into the file name: sales.sql becomes
        sales.<job>.sql.

          -o, --out <file>          script file to write (required)

        {JobOptions}

        {SelectionOptions}

        {ComparisonOptions}

        {Common}
        """;

    public static readonly string List = $"""
        usage: schemadiff list (--config <file> | --source <conn> --target <conn>)

        Lists the defined jobs with their position. compare and script select jobs by
        position or by name.
        Only server and database names are shown, never credentials.

        {JobOptions}

        {Common}
        """;

    public static readonly string Init = """
        usage: schemadiff init <file>

        Writes a starter jobs.json with shared default connections, jobs that use the same
        database name on both sides and examples for different names and servers.
        Edit the connection strings, then use the file with --config.
        """;
}
