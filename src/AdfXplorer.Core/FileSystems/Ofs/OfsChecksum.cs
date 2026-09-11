namespace AdfXplorer.Core.FileSystems.Ofs;

/// <summary>
/// The OFS boot-block checksum: a different, Amiga-ROM-compatible algorithm from the "normal" block
/// checksum used everywhere else (<see cref="BlockReader.ComputeNormalChecksum"/>). Taken from ADFlib's
/// <c>adfBootSum</c> (src/adf_raw.c): https://github.com/adflib/ADFlib/blob/master/src/adf_raw.c
/// </summary>
internal static class OfsChecksum
{
    /// <summary>
    /// Sums all 256 big-endian 32-bit words across both boot blocks (1024 bytes total), skipping word
    /// index 1 (the checksum field, at byte offset 4 of block 0), using end-around-carry addition
    /// (increment on unsigned overflow rather than wrapping) rather than <see cref="BlockReader"/>'s
    /// plain sum-and-negate, then bitwise-NOT rather than negate. A valid boot block's checksum field
    /// equals this computed value.
    /// </summary>
    public static uint ComputeBootChecksum(ReadOnlySpan<byte> block0, ReadOnlySpan<byte> block1)
    {
        uint sum = 0;
        for (int i = 0; i < 256; i++)
        {
            if (i == 1)
            {
                continue;
            }

            var block = i < 128 ? block0 : block1;
            int byteOffset = (i % 128) * 4;
            uint word = BlockReader.ReadUInt32(block, byteOffset);

            if (0xFFFFFFFFU - sum < word)
            {
                sum++;
            }

            sum += word;
        }

        return ~sum;
    }
}
