using SchemaDiff.Core.Model;

namespace SchemaDiff.Core.Hashing;

/// <summary>
/// Computes the sub-part hashes of an object ("columns", "indexes", "checks", "foreignKeys",
/// "body", "setOptions" and so on) and the object hash built from them.
///
/// The rules are what makes a hash comparison trustworthy, so they live in one place:
/// <list type="bullet">
/// <item>An empty part is not stored. "No checks" and "an empty checks part" are the same thing.</item>
/// <item>The object hash combines the parts in ordinal order of their names, so the order in which
/// the builder sets them never matters.</item>
/// <item>The part name goes into the object hash together with the part hash. The same text under
/// a different part is a different object.</item>
/// </list>
/// </summary>
internal static class PartHasher
{
    /// <summary>Stores the canonical text of a part and its hash. Empty text is skipped.</summary>
    public static void Set(ObjectSnapshot snapshot, string part, string canonical)
    {
        if (canonical.Length == 0) return;
        snapshot.PartCanonical[part] = canonical;
        snapshot.Parts[part] = Hash.Of(canonical);
    }

    /// <summary>
    /// Like <see cref="Set"/>, but the hash is computed with the line name (fields[1]) lowercased
    /// while PartCanonical keeps the raw text, so the tree shows index and constraint names with
    /// their real casing. In case-insensitive mode a name that only changed its casing does not
    /// change the hash and the object is not shown as changed for nothing. Only the name token is
    /// folded. Values (definition, expression, column list) go into the hash unchanged, so real
    /// value differences are not masked.
    /// </summary>
    public static void SetFoldingNames(ObjectSnapshot snapshot, string part, string canonical, bool caseSensitiveNames)
    {
        if (canonical.Length == 0) return;
        snapshot.PartCanonical[part] = canonical;
        snapshot.Parts[part] = Hash.Of(caseSensitiveNames ? canonical : FoldLineNames(canonical));
    }

    /// <summary>
    /// Computes the object hash from its parts and writes the canonical text of the whole object,
    /// one "-- [part]" section per part in the same order as the hash.
    /// </summary>
    public static void Seal(ObjectSnapshot snapshot)
    {
        var ordered = snapshot.Parts.OrderBy(p => p.Key, StringComparer.Ordinal).ToList();
        snapshot.Hash = Combine(ordered);
        snapshot.Canonical = string.Join('\n',
            ordered.Select(p => $"-- [{p.Key}]\n{snapshot.PartCanonical[p.Key]}"));
    }

    /// <summary>Object hash of parts that are already in name order. Each part adds its name hash and its value hash.</summary>
    internal static UInt128 Combine(IEnumerable<KeyValuePair<string, UInt128>> orderedParts) =>
        Hash.Combine(orderedParts.SelectMany(p => new[] { Hash.Of(p.Key), p.Value }));

    /// <summary>Lowercases the name field of every line in a canonical text (the token after the
    /// first "|"). The rest of the line is unchanged. Only used as hash input.</summary>
    internal static string FoldLineNames(string canonical)
    {
        var lines = canonical.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var fields = lines[i].Split('|');
            if (fields.Length >= 2 && fields[1].Length > 0)
            {
                fields[1] = fields[1].ToLowerInvariant();
                lines[i] = string.Join('|', fields);
            }
        }
        return string.Join('\n', lines);
    }
}
