using System.Data.Common;
using SchemaDiff.Core.Analysis;

namespace SchemaDiff.Core.Tests;

/// <summary>
/// The probe itself needs a live database. These tests check the probe list (no duplicates, every
/// entry is a COUNT query, the known gap classes are there) and the report logic with a fake
/// scalar runner, so a failing catalog view can be simulated without a server.
/// </summary>
public class CoverageProbeTests
{
    private sealed class FakeDbException(string message) : DbException(message);

    [Fact]
    public void No_duplicate_probe_items()
    {
        var names = CoverageProbe.Probes.Select(p => $"{p.Category}/{p.Item}").ToList();

        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Every_probe_is_a_count_select()
    {
        foreach (var (category, item, _, sql) in CoverageProbe.Probes)
        {
            Assert.False(string.IsNullOrWhiteSpace(category), "category must not be empty");
            Assert.False(string.IsNullOrWhiteSpace(item), "item must not be empty");
            Assert.StartsWith("SELECT", sql.TrimStart(), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("COUNT(*)", sql, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("Row-Level Security (security policies)")]
    [InlineData("Service Broker (queues, services, contracts)")]
    [InlineData("Application roles")]
    [InlineData("Dynamic Data Masking columns")]
    [InlineData("Always Encrypted keys (column master and encryption keys)")]
    [InlineData("External data sources and tables (PolyBase)")]
    [InlineData("Compressed partitions (DATA_COMPRESSION)")]
    [InlineData("XML schema collections")]
    public void Known_gap_classes_are_probed(string item)
    {
        Assert.Contains(CoverageProbe.Probes, p => p.Item == item);
    }

    [Fact]
    public void Probe_count_is_substantial()
    {
        Assert.True(CoverageProbe.Probes.Length >= 35, $"fewer probes than expected: {CoverageProbe.Probes.Length}");
    }

    [Theory]
    [InlineData("Tables")]
    [InlineData("Columns")]
    [InlineData("Check constraints")]
    [InlineData("Default constraints")]
    [InlineData("Views")]
    [InlineData("Procedures")]
    [InlineData("DML triggers")]
    public void Extracted_classes_are_marked_covered(string item)
    {
        Assert.True(CoverageProbe.Probes.Single(p => p.Item == item).Covered);
    }

    [Theory]
    [InlineData("Synonyms")]
    [InlineData("Sequences")]
    [InlineData("DDL triggers")]
    [InlineData("User-defined table types")]
    [InlineData("Full-text indexes")]
    public void Classes_not_extracted_yet_are_marked_as_gaps(string item)
    {
        // Flipped to covered when catalog coverage is extended (WBS-4.3).
        Assert.False(CoverageProbe.Probes.Single(p => p.Item == item).Covered);
    }

    [Fact]
    public async Task A_failing_probe_is_reported_and_the_rest_still_runs()
    {
        var failing = CoverageProbe.Probes.First(p => p.Sql.Contains("sys.security_policies", StringComparison.Ordinal));

        var items = await CoverageProbe.CountAsync((sql, _) =>
            sql == failing.Sql
                ? throw new FakeDbException("Invalid object name 'sys.security_policies'.\nMsg 208")
                : Task.FromResult<object?>(3));

        Assert.Equal(CoverageProbe.Probes.Length, items.Count);
        var failed = Assert.Single(items, i => i.Error is not null);
        Assert.Equal(failing.Item, failed.Item);
        Assert.Null(failed.Count);
        Assert.Equal("Invalid object name 'sys.security_policies'.", failed.Error);
        Assert.All(items.Where(i => i.Error is null), i => Assert.Equal(3, i.Count));
    }

    [Fact]
    public async Task Cancellation_is_not_swallowed()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CoverageProbe.CountAsync((_, ct) => Task.FromCanceled<object?>(ct), cts.Token));
    }

    [Fact]
    public void Gaps_are_uncovered_classes_that_exist_ordered_by_count()
    {
        var report = new CoverageReport("S", "D", "16.0", [
            new CoverageItem("Table", "Tables", true, 40, null),
            new CoverageItem("Other", "Synonyms", false, 2, null),
            new CoverageItem("Other", "Sequences", false, 9, null),
            new CoverageItem("Security", "Certificates", false, 0, null),
            new CoverageItem("Security", "Row-Level Security", false, null, "Invalid object name"),
        ], TimeSpan.Zero);

        Assert.Equal(["Sequences", "Synonyms"], report.Gaps.Select(g => g.Item));
    }

    // Permission awareness

    private static CoverageReport Report(bool hasViewDefinition = true, params CoverageItem[] items) =>
        new("S", "D", "16.0", items, TimeSpan.Zero, hasViewDefinition);

    [Fact]
    public void Unreadable_modules_are_probed_and_counted()
    {
        Assert.Contains(CoverageProbe.Probes, p => p.Item == CoverageProbe.UnreadableModulesItem);

        var report = Report(true, new CoverageItem("Module", CoverageProbe.UnreadableModulesItem, false, 7, null));

        Assert.Equal(7, report.UnreadableModules);
    }

    [Fact]
    public void Unreadable_modules_default_to_zero_when_the_probe_failed()
    {
        var report = Report(true, new CoverageItem("Module", CoverageProbe.UnreadableModulesItem, false, null, "denied"));

        Assert.Equal(0, report.UnreadableModules);
        Assert.Single(report.Failed);
    }

    [Fact]
    public void Report_is_complete_only_with_permission_and_no_failed_probe()
    {
        var ok = new CoverageItem("Table", "Tables", true, 4, null);
        var failed = new CoverageItem("Security", "Row-Level Security (security policies)", false, null, "Invalid object name");

        Assert.True(Report(true, ok).IsComplete);
        Assert.False(Report(false, ok).IsComplete);
        Assert.False(Report(true, ok, failed).IsComplete);
    }

    [Fact]
    public void Header_checks_view_definition_like_the_extraction_preflight()
    {
        Assert.Contains("HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'VIEW DEFINITION')", CoverageProbe.HeaderSql, StringComparison.Ordinal);
    }
}
