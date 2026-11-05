using SchemaDiff.Core.Model;

namespace SchemaDiff.Core.Tests;

/// <summary>
/// The label shown in the UI must resolve back to its ObjectKind.
///
/// When the user ticks an object in the tree, its label ("Full-Text Catalog") comes back to
/// the server and is turned into an ObjectKind. If that fails, the object is silently left
/// out of the script even though the user selected it. That loss would be impossible to
/// notice, so the round trip is checked for every kind.
/// </summary>
public class ObjectKindLabelTests
{
    public static TheoryData<ObjectKind> AllKinds
    {
        get
        {
            var data = new TheoryData<ObjectKind>();
            foreach (var kind in Enum.GetValues<ObjectKind>())
                if (kind != ObjectKind.Unknown) data.Add(kind);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Display_label_resolves_back_to_its_kind(ObjectKind kind)
    {
        var label = ObjectKindLabels.Display(kind);

        Assert.Equal(kind, ObjectKindLabels.Resolve(label));
    }

    [Theory]
    [InlineData("Full-Text Catalog", ObjectKind.FullTextCatalog)]
    [InlineData("User-Defined Type", ObjectKind.UserDefinedType)]
    public void Hyphenated_labels_resolve(string label, ObjectKind expected)
    {
        Assert.Equal(expected, ObjectKindLabels.Resolve(label));
    }

    [Fact]
    public void Every_kind_has_a_distinct_label()
    {
        var labels = Enum.GetValues<ObjectKind>()
            .Where(k => k != ObjectKind.Unknown)
            .Select(ObjectKindLabels.Display)
            .ToList();

        Assert.Equal(labels.Count, labels.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Unknown_label_does_not_silently_become_a_real_kind()
    {
        Assert.Equal(ObjectKind.Unknown, ObjectKindLabels.Resolve("Something Else"));
    }
}
