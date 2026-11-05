using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SchemaDiff.Core.Jobs;

/// <summary>
/// The jobs.json file that lists the pairs to compare. Which databases are compared is a
/// configuration question, not a code question.
/// </summary>
public sealed class JobConfiguration
{
    /// <summary>Shared connection strings so they do not have to be repeated in every job.</summary>
    public ConnectionDefaults? Defaults { get; set; }

    public List<JobDefinition> Jobs { get; set; } = [];

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static async Task<IReadOnlyList<ComparisonJob>> LoadAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Configuration file not found: {path}", path);

        await using var stream = File.OpenRead(path);
        var configuration = await JsonSerializer.DeserializeAsync<JobConfiguration>(stream, ReadOptions, ct)
            ?? throw new InvalidDataException($"Configuration file is empty or unreadable: {path}");

        return configuration.Resolve();
    }

    public IReadOnlyList<ComparisonJob> Resolve()
    {
        if (Jobs.Count == 0)
            throw new InvalidDataException("No jobs are defined in the configuration (the 'jobs' list is empty).");

        var resolved = new List<ComparisonJob>(Jobs.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < Jobs.Count; i++)
        {
            var job = Jobs[i];
            var position = $"jobs[{i}]";

            var source = job.Source ?? Defaults?.Source
                ?? throw new InvalidDataException($"{position}: no 'source' given and no 'defaults.source' defined.");

            var target = job.Target ?? Defaults?.Target
                ?? throw new InvalidDataException($"{position}: no 'target' given and no 'defaults.target' defined.");

            var sourceDatabase = job.SourceDatabase ?? job.Database;
            var targetDatabase = job.TargetDatabase ?? job.Database;

            if (sourceDatabase is not null) source = ComparisonJob.WithDatabase(source, sourceDatabase);
            if (targetDatabase is not null) target = ComparisonJob.WithDatabase(target, targetDatabase);

            var name = job.Name ?? job.Database ?? sourceDatabase ?? $"job{i + 1}";

            if (!seen.Add(name))
                throw new InvalidDataException(
                    $"{position}: the name '{name}' is used by more than one job. " +
                    "Job names are keys for selection and reporting, so they must be unique.");

            resolved.Add(new ComparisonJob(name, source, target));
        }

        return resolved;
    }

    /// <summary>Writes a starter file the user can edit.</summary>
    public static async Task WriteTemplateAsync(string path, CancellationToken ct = default)
    {
        var template = new JobConfiguration
        {
            Defaults = new ConnectionDefaults
            {
                Source = "Server=SOURCE_SERVER;Integrated Security=true;TrustServerCertificate=true",
                Target = "Server=TARGET_SERVER;Integrated Security=true;TrustServerCertificate=true",
            },
            Jobs =
            [
                new JobDefinition { Database = "Database1" },
                new JobDefinition { Database = "Database2" },
                new JobDefinition
                {
                    Name = "different-names-example",
                    SourceDatabase = "NameOnSource",
                    TargetDatabase = "NameOnTarget",
                },
                new JobDefinition
                {
                    Name = "different-servers-example",
                    Source = "Server=OTHER_SERVER;Database=X;Integrated Security=true;TrustServerCertificate=true",
                    Target = "Server=ANOTHER_SERVER;Database=Y;Integrated Security=true;TrustServerCertificate=true",
                },
            ],
        };

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, template, WriteOptions, ct);
    }
}

public sealed class ConnectionDefaults
{
    public string? Source { get; set; }
    public string? Target { get; set; }
}

public sealed class JobDefinition
{
    /// <summary>Name used for selection and in reports. Derived from the database name when missing.</summary>
    public string? Name { get; set; }

    /// <summary>Full connection string. Overrides the defaults when given.</summary>
    public string? Source { get; set; }

    public string? Target { get; set; }

    /// <summary>Database name that is the same on both sides, a shortcut for the most common case.</summary>
    public string? Database { get; set; }

    /// <summary>For pairs where the two sides use different database names.</summary>
    public string? SourceDatabase { get; set; }

    public string? TargetDatabase { get; set; }
}
