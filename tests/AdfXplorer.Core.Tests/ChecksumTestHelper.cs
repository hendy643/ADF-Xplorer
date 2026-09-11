using System.Buffers.Binary;

namespace AdfXplorer.Core.Tests;

/// <summary>
/// Writes correct Amiga block checksums into a synthetic test image. Deliberately re-implements the
/// checksum algorithms independently of AdfXplorer.Core's internal implementation (which isn't visible
/// to this assembly anyway) - two independent implementations agreeing is a better correctness check
/// than reusing the same code on both sides of a test.
/// </summary>
internal static class ChecksumTestHelper
{
    private const int SectorSize = 512;

    /// <summary>
    /// Writes the "normal" block checksum (root/directory/file-header/data blocks): sum all 128
    /// big-endian words in the 512-byte block, skipping the checksum field at offset 20, negate.
    /// </summary>
    public static void WriteNormalChecksum(byte[] image, int block)
    {
        int off = block * SectorSize;
        uint sum = 0;
        for (int i = 0; i < SectorSize / 4; i++)
        {
            int byteOffset = i * 4;
            if (byteOffset == 20)
            {
                continue;
            }

            sum += BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(off + byteOffset, 4));
        }

        uint checksum = unchecked((uint)-(int)sum);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(off + 20, 4), checksum);
    }

    /// <summary>
    /// Writes the boot-block checksum (the two blocks starting at <paramref name="bootBlock"/>
    /// combined, end-around-carry, skip word index 1, then bitwise NOT) into the checksum field (byte
    /// offset 4 of <paramref name="bootBlock"/>). <paramref name="bootBlock"/> is 0 for a top-level
    /// image/floppy, or an RDB partition's own first block.
    /// </summary>
    public static void WriteBootChecksum(byte[] image, int bootBlock = 0)
    {
        int baseOffset = bootBlock * SectorSize;
        uint sum = 0;
        for (int i = 0; i < 256; i++)
        {
            if (i == 1)
            {
                continue;
            }

            uint word = BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(baseOffset + i * 4, 4));
            if (0xFFFFFFFFU - sum < word)
            {
                sum++;
            }

            sum += word;
        }

        uint checksum = ~sum;
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(baseOffset + 4, 4), checksum);
    }

    /// <summary>
    /// Writes the RDB "normal" checksum (RDSK/PART blocks): sum <paramref name="summedLongs"/>
    /// big-endian words starting at the block, skipping the checksum field at offset 8, negate.
    /// </summary>
    public static void WriteRdbChecksum(byte[] image, int block, int summedLongs)
    {
        int off = block * SectorSize;
        uint sum = 0;
        for (int i = 0; i < summedLongs; i++)
        {
            int byteOffset = i * 4;
            if (byteOffset == 8)
            {
                continue;
            }

            sum += BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(off + byteOffset, 4));
        }

        uint checksum = unchecked((uint)-(int)sum);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(off + 8, 4), checksum);
    }
}
