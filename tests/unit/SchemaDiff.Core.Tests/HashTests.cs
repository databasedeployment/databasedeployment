using SchemaDiff.Core.Hashing;

namespace SchemaDiff.Core.Tests;

public class HashTests
{
    [Fact]
    public void Same_text_always_gives_the_same_hash()
    {
        Assert.Equal(Hash.Of("col|id|sys.int"), Hash.Of("col|id|sys.int"));
    }

    [Fact]
    public void Different_text_gives_a_different_hash()
    {
        Assert.NotEqual(Hash.Of("col|id|sys.int"), Hash.Of("col|id|sys.bigint"));
    }

    [Fact]
    public void Combining_parts_depends_on_their_order()
    {
        var columns = Hash.Of("columns");
        var indexes = Hash.Of("indexes");

        Assert.Equal(Hash.Combine([columns, indexes]), Hash.Combine([columns, indexes]));
        Assert.NotEqual(Hash.Combine([columns, indexes]), Hash.Combine([indexes, columns]));
    }

    [Fact]
    public void Hex_form_is_32_lowercase_characters()
    {
        var hex = Hash.ToHex(Hash.Of("schema|sales"));

        Assert.Equal(32, hex.Length);
        Assert.Equal(hex.ToLowerInvariant(), hex);
    }
}
