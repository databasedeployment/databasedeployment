using SchemaDiff.Core.Extraction;
using SchemaDiff.Core.Model;
using SchemaDiff.Core.Scripting;

namespace SchemaDiff.Core.Tests;

/// <summary>
/// SQL Server rejects ALTER COLUMN when an index covers the column (Msg 5074), even if the index
/// itself did not change. The script has to drop such an index before the ALTER and create it
/// again afterwards.
/// </summary>
public class AlterColumnIndexTests
{
    private const int TableId = 100;
    private static readonly SnapshotOptions Options = SnapshotOptions.Default with { KeepDisplayScripts = true };

    private static ColumnRow Col(int columnId, string name, string type, short length, byte precision) =>
        new(TableId, columnId, name, "sys", type, length, precision, 0, true, null, false, false,
            null, null, null, null, null, null, null);

    // IX_Keep covers Keep on both sides and never changes. Only the type of Keep or Other varies.
    private static DatabaseSnapshot Table(string keepType, short keepLength, byte keepPrecision, string otherType = "int") =>
        SnapshotBuilder.Build(new CatalogSet
        {
            DatabaseName = "t",
            ServerName = "S",
            Objects = [new ObjectRow(TableId, "dbo", "T", "U", DateTime.UnixEpoch, 0)],
            Columns =
            [
                Col(1, "Id", "int", 4, 10),
                Col(2, "Keep", keepType, keepLength, keepPrecision),
                Col(3, "Other", otherType, otherType == "int" ? (short)4 : (short)8, otherType == "int" ? (byte)10 : (byte)19),
            ],
            Indexes = [new IndexRow(TableId, 2, "IX_Keep", "NONCLUSTERED", false, false, false, 0, false, false, null)],
            IndexColumns = [new IndexColumnRow(TableId, 2, 1, 2, 1, false, false)],
        }, new ExtractionReport(), Options);

    private static string Script(DatabaseSnapshot source, DatabaseSnapshot target) =>
        TableScriptGenerator.Generate(FixtureCompare.Compare(source, target)).Sql;

    [Fact]
    public void Unchanged_index_on_altered_column_is_dropped_then_recreated()
    {
        var sql = Script(Table("bigint", 8, 19), Table("int", 4, 10));

        var dropAt = sql.IndexOf("DROP INDEX [IX_Keep]", StringComparison.Ordinal);
        var alterAt = sql.IndexOf("ALTER COLUMN [Keep]", StringComparison.Ordinal);
        var createAt = sql.IndexOf("CREATE NONCLUSTERED INDEX [IX_Keep]", StringComparison.Ordinal);

        Assert.True(dropAt >= 0, "IX_Keep has to be dropped before the ALTER");
        Assert.True(alterAt >= 0, "an ALTER COLUMN for Keep is expected");
        Assert.True(createAt >= 0, "IX_Keep has to be created again");
        Assert.True(dropAt < alterAt, "DROP INDEX must come before ALTER COLUMN (Msg 5074)");
        Assert.True(alterAt < createAt, "CREATE INDEX must come after ALTER COLUMN");
    }

    [Fact]
    public void Unchanged_index_is_left_alone_when_its_column_is_not_altered()
    {
        var sql = Script(Table("int", 4, 10, otherType: "bigint"), Table("int", 4, 10));

        Assert.Contains("ALTER COLUMN [Other]", sql);
        Assert.DoesNotContain("IX_Keep", sql);
    }
}
