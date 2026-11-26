using SchemaDiff.Core.Diff;
using SchemaDiff.Core.Extraction;
using SchemaDiff.Core.Model;

namespace SchemaDiff.Core;

/// <summary>
/// Entry point for comparing one source and target pair. The CLI and the web backend call
/// this, so extraction, snapshot building and comparison always run in the same order.
/// </summary>
public static class SchemaDiffService
{
    /// <summary>Extracts one database and builds its snapshot.</summary>
    public static async Task<DatabaseSnapshot> CaptureAsync(
        string connectionString,
        SnapshotOptions? options = null,
        ExtractionGate? gate = null,
        CancellationToken ct = default,
        CompareProgress? progress = null)
    {
        options ??= SnapshotOptions.Default;
        var extractor = new CatalogExtractor(gate);
        var (catalog, report) = await extractor.ExtractAsync(connectionString, ct, progress);
        return SnapshotBuilder.Build(catalog, report, options);
    }

    /// <summary>
    /// Extracts both sides in parallel, so neither side waits for the other. Extraction and
    /// building run as separate steps to keep the progress phases clean: both sides are extracted
    /// first (Extracting), then the snapshots are built (Building) and finally compared (Comparing).
    /// </summary>
    public static async Task<CompareResult> CompareAsync(
        string sourceConnectionString,
        string targetConnectionString,
        SnapshotOptions? options = null,
        ExtractionGate? gate = null,
        CancellationToken ct = default,
        CompareProgress? progress = null)
    {
        options ??= SnapshotOptions.Default;
        progress ??= CompareProgress.None;

        var sourceExtractor = new CatalogExtractor(gate);
        var targetExtractor = new CatalogExtractor(gate);
        var sourceTask = sourceExtractor.ExtractAsync(sourceConnectionString, ct, progress);
        var targetTask = targetExtractor.ExtractAsync(targetConnectionString, ct, progress);
        await Task.WhenAll(sourceTask, targetTask);

        return CompareCatalogs(await sourceTask, await targetTask, options, progress);
    }

    /// <summary>
    /// Builds both snapshots from extracted catalogs and compares them. Kept separate from
    /// extraction so the build and compare steps can be tested without a database.
    /// </summary>
    internal static CompareResult CompareCatalogs(
        (CatalogSet Catalog, ExtractionReport Report) source,
        (CatalogSet Catalog, ExtractionReport Report) target,
        SnapshotOptions options,
        CompareProgress? progress = null)
    {
        progress ??= CompareProgress.None;
        progress.EnterPhase(ComparePhase.Building);

        // The build total is set from the module count of both sides up front. Otherwise the total
        // would grow when the second side starts building and the percentage would drop back.
        progress.AddBuildItems(source.Catalog.Modules.Count + target.Catalog.Modules.Count);

        var sourceSnapshot = SnapshotBuilder.Build(source.Catalog, source.Report, options, progress);
        var targetSnapshot = SnapshotBuilder.Build(target.Catalog, target.Report, options, progress);

        progress.EnterPhase(ComparePhase.Comparing);
        var result = SchemaComparer.Compare(sourceSnapshot, targetSnapshot);
        progress.EnterPhase(ComparePhase.Done);
        return result;
    }
}
