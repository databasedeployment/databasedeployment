using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;

namespace SchemaDiff.Core.Hashing;

/// <summary>
/// XxHash128 based hashing. It is not cryptographic, but it is more than enough to detect
/// schema changes and an order of magnitude faster than SHA-256. For 100k objects the
/// chance of a collision is around 1e-29.
/// </summary>
public static class Hash
{
    public static UInt128 Of(string text) =>
        XxHash128.HashToUInt128(Encoding.UTF8.GetBytes(text));

    /// <summary>Combines sub-part hashes. Order matters, so the caller must pass the parts in a deterministic order.</summary>
    public static UInt128 Combine(IEnumerable<UInt128> parts)
    {
        var hasher = new XxHash128();
        Span<byte> buffer = stackalloc byte[16];
        foreach (var part in parts)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(buffer, (ulong)part);
            BinaryPrimitives.WriteUInt64LittleEndian(buffer[8..], (ulong)(part >> 64));
            hasher.Append(buffer);
        }
        return FromBytes(hasher.GetCurrentHash());
    }

    public static string ToHex(UInt128 value) => value.ToString("x32");

    private static UInt128 FromBytes(byte[] bytes)
    {
        var lower = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        var upper = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(8));
        return new UInt128(upper, lower);
    }
}
