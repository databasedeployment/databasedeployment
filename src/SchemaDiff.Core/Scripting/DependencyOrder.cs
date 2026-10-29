using SchemaDiff.Core.Model;

namespace SchemaDiff.Core.Scripting;

/// <summary>
/// Orders objects so that each one is created after the objects it references. Every script
/// generator uses the same ordering, which keeps CREATE order consistent across sections.
/// </summary>
public static class DependencyOrder
{
    /// <param name="nodes">Objects to order.</param>
    /// <param name="references">Object to the objects it references. Entries outside <paramref name="nodes"/> are ignored.</param>
    /// <param name="comparer">Key comparer, usually case-insensitive.</param>
    /// <param name="hadCycle">True when a cycle was found. The objects in the cycle are still returned in seed order.</param>
    /// <param name="seedOrder">Order in which independent objects are visited. Defaults to kind, schema and name.</param>
    public static List<ObjectKey> Order(
        IReadOnlyCollection<ObjectKey> nodes,
        IReadOnlyDictionary<ObjectKey, List<ObjectKey>> references,
        IEqualityComparer<ObjectKey> comparer,
        out bool hadCycle,
        Func<IEnumerable<ObjectKey>, IEnumerable<ObjectKey>>? seedOrder = null)
    {
        var pending = new HashSet<ObjectKey>(nodes, comparer);
        var ordered = new List<ObjectKey>(nodes.Count);
        var done = new HashSet<ObjectKey>(comparer);
        var visiting = new HashSet<ObjectKey>(comparer);
        var cycle = false;

        void Visit(ObjectKey key)
        {
            if (done.Contains(key)) return;
            if (!visiting.Add(key)) { cycle = true; return; }

            foreach (var reference in references.GetValueOrDefault(key) ?? [])
                if (pending.Contains(reference)) Visit(reference);

            visiting.Remove(key);
            if (done.Add(key)) ordered.Add(Canonical(key));
        }

        // Return the caller's spelling of a key, not the spelling found in the reference list.
        ObjectKey Canonical(ObjectKey key) => pending.TryGetValue(key, out var original) ? original : key;

        seedOrder ??= keys => keys
            .OrderBy(k => k.Kind)
            .ThenBy(k => k.Schema, StringComparer.OrdinalIgnoreCase)
            .ThenBy(k => k.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var key in seedOrder(nodes))
            Visit(key);

        hadCycle = cycle;
        return ordered;
    }
}
