using SchemaDiff.Core.Analysis;
using SchemaDiff.Core.Model;

namespace SchemaDiff.Core.Contracts;

// Contracts between the Core modules, agreed in WBS-2.1. The CLI, the web backend and the
// AI assistant depend on these interfaces only, so each module can be developed and tested
// against fixture data before the others are finished. A change here needs a reviewed PR.

/// <summary>Reads the catalog of one database and turns it into a snapshot.</summary>
public interface ISnapshotExtractor
{
    Task<DatabaseSnapshot> ExtractAsync(string connectionString, CancellationToken cancellationToken = default);
}

/// <summary>Compares two snapshots by hash and reports what was added, removed or changed.</summary>
public interface ISchemaComparer
{
    CompareResult Compare(DatabaseSnapshot source, DatabaseSnapshot target);
}

/// <summary>Classifies changed tables by what will happen to their data during deployment.</summary>
public interface IRiskAnalyzer
{
    IReadOnlyList<TableRisk> Analyze(CompareResult result);
}

/// <summary>Produces a deployment script that brings the target in line with the source.</summary>
public interface IDeploymentScriptGenerator
{
    /// <param name="result">Comparison to deploy.</param>
    /// <param name="selection">Objects the user selected. Null means every difference.</param>
    string Generate(CompareResult result, IReadOnlySet<ObjectKey>? selection = null);
}
