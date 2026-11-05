using SchemaDiff.Core.Model;

namespace SchemaDiff.Core.Tests;

public class ObjectSnapshotTests
{
    [Theory]
    [InlineData("int", 4, 10, 0, "int")]
    [InlineData("nvarchar", 100, 0, 0, "nvarchar(50)")]
    [InlineData("nvarchar", -1, 0, 0, "nvarchar(max)")]
    [InlineData("varchar", 20, 0, 0, "varchar(20)")]
    [InlineData("decimal", 9, 18, 2, "decimal(18,2)")]
    [InlineData("varbinary", -1, 0, 0, "varbinary(max)")]
    public void Column_type_is_displayed_in_tsql_form(string type, short maxLength, byte precision, byte scale, string expected)
    {
        var column = TestFactory.Column("C", type, maxLength, precision, scale);

        Assert.Equal(expected, column.TypeDisplay);
    }

    [Fact]
    public void Typed_xml_column_shows_its_schema_collection()
    {
        var column = TestFactory.Column("Doc", "xml", -1) with
        {
            XmlCollection = "[dbo].[OrderSchema]",
            IsXmlDocument = true,
        };

        Assert.Equal("xml (DOCUMENT [dbo].[OrderSchema])", column.TypeDisplay);
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    public void Primary_keys_and_unique_constraints_are_scripted_as_constraints(bool pk, bool uq, bool expected)
    {
        var index = new IndexDefinition("IX", pk, uq, pk || uq, "NONCLUSTERED", false, [], [], null);

        Assert.Equal(expected, index.IsConstraint);
    }

    [Theory]
    [InlineData("ROW", "ROW")]
    [InlineData("PAGE", "PAGE")]
    [InlineData("NONE", null)]
    [InlineData("COLUMNSTORE", null)]
    public void Only_explicit_compression_settings_are_written(string compression, string? expected)
    {
        var index = new IndexDefinition("IX", false, false, false, "NONCLUSTERED", false, [], [], null, compression);

        Assert.Equal(expected, index.ExplicitCompression);
    }

    [Fact]
    public void Bounding_box_applies_to_geometry_grids_only()
    {
        var geometry = new SpatialIndexDefinition("SIX", "Shape", "GEOMETRY_GRID", 0, 0, 100, 100, null, null, null, null, 16);
        var geography = geometry with { Tessellation = "GEOGRAPHY_GRID" };

        Assert.True(geometry.HasBoundingBox);
        Assert.False(geography.HasBoundingBox);
        Assert.True(geography with { Tessellation = "GEOGRAPHY_AUTO_GRID" } is { IsAutoGrid: true });
    }

    [Fact]
    public void Part_hashes_are_keyed_by_exact_part_name()
    {
        var snapshot = TestFactory.Obj(TestFactory.Table("Customer"), UInt128.One);
        snapshot.Parts["columns"] = 1;

        Assert.True(snapshot.Parts.ContainsKey("columns"));
        Assert.False(snapshot.Parts.ContainsKey("Columns"));
    }
}
