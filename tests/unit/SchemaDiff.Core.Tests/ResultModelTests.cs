using SchemaDiff.Core.Analysis;
using SchemaDiff.Core.Model;
using static SchemaDiff.Core.Tests.TestFactory;

namespace SchemaDiff.Core.Tests;

public class ResultModelTests
{
    [Fact]
    public void Compare_result_counts_differences_by_kind()
    {
        var source = Database("dev", [Obj(Table("A"), 1), Obj(Table("B"), 2)]);
        var target = Database("prod", [Obj(Table("B"), 3), Obj(Table("C"), 4)]);

        var result = new CompareResult
        {
            Source = source,
            Target = target,
            EqualCount = 5,
            Differences =
            [
                new ObjectDiff(Table("A"), DiffKind.Added, []),
                new ObjectDiff(Table("B"), DiffKind.Changed, ["columns"]),
                new ObjectDiff(Table("C"), DiffKind.Removed, []),
                new ObjectDiff(View("V"), DiffKind.Indeterminate, ["body"]),
            ],
        };

        Assert.Equal(1, result.AddedCount);
        Assert.Equal(1, result.ChangedCount);
        Assert.Equal(1, result.RemovedCount);
        Assert.Equal(1, result.IndeterminateCount);
        Assert.Equal(9, result.TotalCompared());
    }

    [Fact]
    public void Database_snapshot_finds_objects_case_insensitively()
    {
        var snapshot = Database("dev", [Obj(Table("Customer"), 1)]);

        Assert.Equal(1, snapshot.Count);
        Assert.True(snapshot.Objects.ContainsKey(Table("CUSTOMER")));
    }

    [Theory]
    [InlineData(DeploymentRisk.Safe, 1000L, false)]
    [InlineData(DeploymentRisk.InPlace, 1000L, false)]
    [InlineData(DeploymentRisk.BlockedIfNotEmpty, 0L, false)]
    [InlineData(DeploymentRisk.BlockedIfNotEmpty, 10L, true)]
    [InlineData(DeploymentRisk.DataLoss, 10L, true)]
    [InlineData(DeploymentRisk.DataLoss, null, true)]
    public void A_risky_change_blocks_unless_the_table_is_known_to_be_empty(DeploymentRisk risk, long? rows, bool blocks)
    {
        var tableRisk = new TableRisk(Table("Orders"), risk, rows, []);

        Assert.Equal(blocks, tableRisk.WillBlock);
    }

    [Fact]
    public void Extraction_report_keeps_failed_queries_apart_from_warnings()
    {
        var report = new ExtractionReport();
        report.FailedQueries.Add("statistics");
        report.Warnings.Add("statistics could not be read");

        Assert.Contains("statistics", report.FailedQueries);
        Assert.DoesNotContain("Statistics", report.FailedQueries);
        Assert.Single(report.Warnings);
    }
}
