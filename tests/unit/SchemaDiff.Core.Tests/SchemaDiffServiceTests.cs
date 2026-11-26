using System.Collections.Concurrent;
using SchemaDiff.Core.Extraction;
using SchemaDiff.Core.Model;

namespace SchemaDiff.Core.Tests;

/// <summary>
/// The service ties extraction, snapshot building and comparison together. The part after
/// extraction runs on catalogs, so it is tested here without a database.
/// </summary>
public class SchemaDiffServiceTests
{
    private static CatalogSet Catalog(string database, params (int Id, string Name)[] procs) => new()
    {
        ServerName = "TESTSRV",
        DatabaseName = database,
        Objects = [.. procs.Select(p => new ObjectRow(p.Id, "dbo", p.Name, "P", DateTime.UnixEpoch, 0))],
        Modules = [.. procs.Select(p => new ModuleRow(p.Id, $"CREATE PROCEDURE dbo.{p.Name} AS SELECT 1", true, true))],
    };

    [Fact]
    public void Catalogs_are_built_and_compared()
    {
        var source = Catalog("dev", (1, "Shared"), (2, "OnlyInDev"));
        var target = Catalog("prod", (1, "Shared"), (3, "OnlyInProd"));

        var result = SchemaDiffService.CompareCatalogs(
            (source, new ExtractionReport()), (target, new ExtractionReport()), SnapshotOptions.Default);

        Assert.Equal("dev", result.Source.Database);
        Assert.Equal("prod", result.Target.Database);
        Assert.Equal(1, result.EqualCount);
        Assert.Equal("OnlyInDev", Assert.Single(result.Differences, d => d.Kind == DiffKind.Added).Key.Name);
        Assert.Equal("OnlyInProd", Assert.Single(result.Differences, d => d.Kind == DiffKind.Removed).Key.Name);
    }

    [Fact]
    public void Phases_move_from_building_to_comparing_to_done()
    {
        var phases = new ConcurrentQueue<ComparePhase>();
        var progress = new CompareProgress(snapshot => phases.Enqueue(snapshot.Phase));

        SchemaDiffService.CompareCatalogs(
            (Catalog("dev", (1, "P")), new ExtractionReport()),
            (Catalog("prod", (1, "P")), new ExtractionReport()),
            SnapshotOptions.Default, progress);

        var distinct = phases.Distinct().ToList();
        Assert.Equal([ComparePhase.Building, ComparePhase.Comparing, ComparePhase.Done], distinct);
        Assert.Equal(ComparePhase.Done, progress.Current.Phase);
    }

    [Fact]
    public void The_build_total_covers_both_sides_before_the_first_module_is_done()
    {
        // When the total grew while the second side was building, the percentage would jump back.
        var seen = new ConcurrentQueue<ProgressSnapshot>();
        var progress = new CompareProgress(seen.Enqueue);

        SchemaDiffService.CompareCatalogs(
            (Catalog("dev", (1, "A"), (2, "B"), (3, "C")), new ExtractionReport()),
            (Catalog("prod", (1, "A"), (2, "B")), new ExtractionReport()),
            SnapshotOptions.Default, progress);

        Assert.All(seen.Where(s => s.BuildDone > 0), s => Assert.Equal(5, s.BuildTotal));
        Assert.Equal(5, progress.Current.BuildDone);
    }

    [Fact]
    public void Extraction_warnings_stay_with_their_side()
    {
        var sourceReport = new ExtractionReport();
        sourceReport.Warnings.Add("row counts could not be read");

        var result = SchemaDiffService.CompareCatalogs(
            (Catalog("dev"), sourceReport), (Catalog("prod"), new ExtractionReport()), SnapshotOptions.Default);

        Assert.Contains("row counts could not be read", result.Source.Report.Warnings);
        Assert.Empty(result.Target.Report.Warnings);
    }

    [Fact]
    public async Task An_unreachable_server_fails_the_comparison()
    {
        const string unreachable = "Server=tcp:127.0.0.1,1;Database=x;User ID=x;Password=y;Connect Timeout=2;Encrypt=false";

        await Assert.ThrowsAnyAsync<Exception>(() => SchemaDiffService.CompareAsync(unreachable, unreachable));
    }
}
