namespace AdfXplorer.Core.FileSystems.Ofs;

/// <summary>
/// The AmigaDOS directory-block hash function: maps a filename to one of the 72 hash-table buckets
/// in a root or directory block. Not needed for enumeration (which scans every bucket), but used to
/// jump straight to a bucket for single-name lookups, and is the entry point any future write support
/// would need to place a new entry in the correct bucket.
///
/// Algorithm per the AmigaDOS technical reference, as documented in ADFlib's adf_info FAQ:
/// https://github.com/adflib/ADFlib/blob/master/doc/FAQ/adf_info_V0_9.txt
/// </summary>
internal static class AmigaHash
{
    public const int HashTableSize = 72;

    /// <summary>
    /// Computes the bucket index for <paramref name="name"/>. Seeded with the name's length (not 0),
    /// case-folded per-character (AmigaDOS filenames are case-insensitive), and masked to 11 bits after
    /// each step - all load-bearing quirks of the original algorithm that must be reproduced exactly or
    /// the computed bucket won't match what a real Amiga (or ADFlib) would place the entry in.
    /// </summary>
    public static int Compute(string name, int hashTableSize = HashTableSize)
    {
        uint hash = (uint)name.Length;
        foreach (char c in name)
        {
            hash = (hash * 13 + char.ToUpperInvariant(c)) & 0x7ff;
        }

        return (int)(hash % (uint)hashTableSize);
    }
}
