using SchemaDiff.Core.Extraction;
using SchemaDiff.Core.Normalization;

namespace SchemaDiff.Cli;

internal enum CliCommand { Help, Compare, Script, List, Init }

/// <summary>Parsed command line. Each command accepts only the options that mean something for it.</summary>
internal sealed class CliArguments
{
    public CliCommand Command { get; private init; }

    /// <summary>Command whose help was asked for with <c>help &lt;command&gt;</c> or <c>&lt;command&gt; --help</c>.</summary>
    public CliCommand? HelpTopic { get; private init; }

    public string? Source { get; private init; }
    public string? Target { get; private init; }
    public string? Config { get; private init; }
    public IReadOnlyList<string> Databases { get; private init; } = [];

    public string? Selection { get; private init; }
    public bool All { get; private init; }
    public bool Interactive { get; private init; }

    /// <summary>Script file for <c>script</c>, configuration file for <c>init</c>.</summary>
    public string? OutputPath { get; private init; }

    public int Details { get; private init; } = 30;
    public int MaxQueries { get; private init; } = 16;

    public bool CaseSensitive { get; private init; }
    public bool KeepSystemNames { get; private init; }
    public bool RespectWhitespace { get; private init; }
    public bool RespectComments { get; private init; }
    public bool IgnoreIndexPhysicalOptions { get; private init; }
    public bool IgnoreColumnOrder { get; private init; }
    public bool Verbose { get; private init; }

    public SnapshotOptions ToSnapshotOptions() => new()
    {
        Normalization = new NormalizationOptions(
            IgnoreWhitespace: !RespectWhitespace,
            IgnoreComments: !RespectComments),
        IgnoreSystemNamedConstraints = !KeepSystemNames,
        IgnoreIndexPhysicalOptions = IgnoreIndexPhysicalOptions,
        IgnoreColumnOrder = IgnoreColumnOrder,
        CaseSensitiveNames = CaseSensitive,
        // The script needs the original text of the objects it creates.
        KeepDisplayScripts = Command == CliCommand.Script,
    };

    private static readonly Dictionary<string, CliCommand> Commands = new(StringComparer.OrdinalIgnoreCase)
    {
        ["compare"] = CliCommand.Compare,
        ["script"] = CliCommand.Script,
        ["list"] = CliCommand.List,
        ["init"] = CliCommand.Init,
        ["help"] = CliCommand.Help,
    };

    public static bool TryParseCommand(string name, out CliCommand command) =>
        Commands.TryGetValue(name, out command);

    /// <summary>Returns the parsed arguments, or null with an error message for the user.</summary>
    public static (CliArguments? Arguments, string? Error) Parse(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h" or "/?")
            return (new CliArguments { Command = CliCommand.Help }, null);

        if (!TryParseCommand(args[0], out var command))
            return (null, $"Unknown command '{args[0]}'.");

        if (command == CliCommand.Help)
        {
            if (args.Length == 1) return (new CliArguments { Command = CliCommand.Help }, null);
            if (args.Length == 2 && TryParseCommand(args[1], out var topic) && topic != CliCommand.Help)
                return (new CliArguments { Command = CliCommand.Help, HelpTopic = topic }, null);
            return (null, $"There is no help topic '{string.Join(' ', args[1..])}'.");
        }

        return command == CliCommand.Init ? ParseInit(args) : ParseJobCommand(command, args);
    }

    private static (CliArguments?, string?) ParseInit(string[] args)
    {
        if (args.Skip(1).Any(a => a is "--help" or "-h"))
            return (new CliArguments { Command = CliCommand.Help, HelpTopic = CliCommand.Init }, null);

        if (args.Length != 2 || args[1].StartsWith('-'))
            return (null, "init needs exactly one argument, the path of the file to write.");

        return (new CliArguments { Command = CliCommand.Init, OutputPath = args[1] }, null);
    }

    private static (CliArguments?, string?) ParseJobCommand(CliCommand command, string[] args)
    {
        string? source = null, target = null, config = null, selection = null, output = null;
        var databases = new List<string>();
        int details = 30, maxQueries = 16;
        bool all = false, interactive = false, verbose = false;
        bool caseSensitive = false, keepSystemNames = false, respectWhitespace = false;
        bool respectComments = false, ignoreIndexPhysical = false, ignoreColumnOrder = false;

        var runsJobs = command is CliCommand.Compare or CliCommand.Script;

        for (var i = 1; i < args.Length; i++)
        {
            var name = args[i];
            string? error = null;

            string? Value()
            {
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) return args[++i];
                error = $"{name} needs a value.";
                return null;
            }

            int Number(int minimum)
            {
                var text = Value();
                if (text is null) return minimum;
                if (int.TryParse(text, out var parsed) && parsed >= minimum) return parsed;
                error = $"{name} needs a whole number of at least {minimum}, '{text}' is not one.";
                return minimum;
            }

            switch (name)
            {
                case "--help" or "-h":
                    return (new CliArguments { Command = CliCommand.Help, HelpTopic = command }, null);

                case "--source" or "-s": source = Value(); break;
                case "--target" or "-t": target = Value(); break;
                case "--config" or "-c": config = Value(); break;
                case "--databases":
                    databases.AddRange((Value() ?? string.Empty)
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;
                case "--verbose" or "-v": verbose = true; break;

                case "--jobs" or "-j" when runsJobs: selection = Value(); break;
                case "--all" or "-a" when runsJobs: all = true; break;
                case "--select" when runsJobs: interactive = true; break;
                case "--max-queries" when runsJobs: maxQueries = Number(1); break;
                case "--case-sensitive" when runsJobs: caseSensitive = true; break;
                case "--keep-system-names" when runsJobs: keepSystemNames = true; break;
                case "--respect-whitespace" when runsJobs: respectWhitespace = true; break;
                case "--respect-comments" when runsJobs: respectComments = true; break;
                case "--ignore-index-physical" when runsJobs: ignoreIndexPhysical = true; break;
                case "--ignore-column-order" when runsJobs: ignoreColumnOrder = true; break;

                case "--details" or "-d" when command == CliCommand.Compare: details = Number(0); break;
                case "--out" or "-o" when command == CliCommand.Script: output = Value(); break;

                default:
                    return (null, $"Unknown option '{name}' for {command.ToString().ToLowerInvariant()}.");
            }

            if (error is not null) return (null, error);
        }

        if (config is not null && (source is not null || target is not null))
            return (null, "Use either --config or --source with --target, not both.");

        if (config is null && (source is null || target is null))
            return (null, "Give the jobs with --config <file>, or a single pair with --source and --target.");

        if (databases.Count > 0 && config is not null)
            return (null, "--databases only works with --source and --target.");

        if (all && selection is not null)
            return (null, "Use either --all or --jobs, not both.");

        if (command == CliCommand.Script && output is null)
            return (null, "script needs --out <file> for the generated script.");

        return (new CliArguments
        {
            Command = command,
            Source = source,
            Target = target,
            Config = config,
            Databases = databases,
            Selection = selection,
            All = all,
            Interactive = interactive,
            OutputPath = output,
            Details = details,
            MaxQueries = maxQueries,
            CaseSensitive = caseSensitive,
            KeepSystemNames = keepSystemNames,
            RespectWhitespace = respectWhitespace,
            RespectComments = respectComments,
            IgnoreIndexPhysicalOptions = ignoreIndexPhysical,
            IgnoreColumnOrder = ignoreColumnOrder,
            Verbose = verbose,
        }, null);
    }
}
