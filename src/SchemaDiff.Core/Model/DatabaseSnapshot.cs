namespace SchemaDiff.Core.Model;

public sealed class DatabaseSnapshot
{
    public required string Server { get; init; }
    public required string Database { get; init; }
    public required Dictionary<ObjectKey, ObjectSnapshot> Objects { get; init; }
    public ExtractionReport Report { get; set; } = new();

    /// <summary>
    /// True when column order was left out of the hash because the user chose to ignore it.
    /// Risk analysis needs this so it does not bring back a difference the user deliberately ignored.
    /// </summary>
    public bool IgnoredColumnOrder { get; init; }

    /// <summary>True when collation was left out of the hash ("ignore collation").</summary>
    public bool IgnoredCollation { get; init; }

    /// <summary>
    /// Whether the user asked for case-sensitive names. Keys always match case-insensitively.
    /// This flag only decides whether a difference in casing is reported as a change.
    /// </summary>
    public bool CaseSensitiveNames { get; init; }

    /// <summary>
    /// Whether column names are case-sensitive. When off, column names are lowercased in the
    /// canonical text so casing differences are ignored, and the script generator does not emit
    /// <c>sp_rename</c>. See <see cref="CaseSensitiveNames"/> for object names.
    /// </summary>
    public bool CaseSensitiveColumnNames { get; init; }

    /// <summary>
    /// Maps an object to the objects it references. Script generation uses it to create new
    /// objects in the right order. It is not part of the comparison.
    /// </summary>
    public Dictionary<ObjectKey, List<ObjectKey>> References { get; init; } = [];

    public int Count => Objects.Count;
}

/// <summary>Timings and warnings collected while the catalog is extracted.</summary>
public sealed class ExtractionReport
{
    public Dictionary<string, TimeSpan> QueryTimings { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> RowCounts { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Names of optional queries that could not run. The difference between "no data" and
    /// "could not read the data" matters. Treating an unreadable class as empty would end up
    /// generating DROP statements for objects that exist on the target.
    /// </summary>
    public HashSet<string> FailedQueries { get; } = new(StringComparer.Ordinal);
    public TimeSpan TotalExtraction { get; set; }
    public TimeSpan Normalization { get; set; }
    public List<string> Warnings { get; } = [];
}
