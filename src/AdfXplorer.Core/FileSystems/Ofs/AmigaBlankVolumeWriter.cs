using System.Buffers.Binary;
using System.Text;
using AdfXplorer.Core.DiskImage;

namespace AdfXplorer.Core.FileSystems.Ofs;

/// <summary>
/// Creates a blank, correctly-checksummed OFS-or-FFS volume (boot block + root block + bitmap block,
/// no files) - real-Amiga/WinUAE-openable, not just readable by this project's own readers. Root/bitmap
/// block layout is byte-identical between OFS and FFS (only the boot-block flags byte differs), so
/// <see cref="OfsFileSystemWriter"/> and <see cref="Ffs.FfsFileSystemWriter"/> are both thin wrappers
/// around this.
///
/// Block layout/checksums reuse the same references as the readers
/// (https://github.com/adflib/ADFlib/blob/master/src/adf_blk.h). The bitmap block is confirmed against
/// ADFlib's <c>src/adf_blk.h</c> (struct layout: a 4-byte checksum at offset 0 followed by a 127-long
/// <c>map[]</c>) and <c>src/adf_bitm.c</c> (bit semantics: bit 1 = free, bit 0 = used; block number
/// <c>B</c> (B &gt;= 2) maps to <c>map[((B-2)/32) % 127]</c> bit <c>(B-2) % 32</c>, LSB-first).
/// </summary>
internal static class AmigaBlankVolumeWriter
{
    private const int SectorSize = AdfImage.SectorSize;
    private const int BitmapMapWords = 127;

    public static AdfImage CreateBlank(int sectorCount, string volumeLabel, byte bootFlags)
    {
        var data = new byte[(long)sectorCount * SectorSize];
        WriteBlankInto(data, 0, sectorCount, volumeLabel, bootFlags);
        return new AdfImage(data);
    }

    /// <summary>
    /// Writes a blank volume's boot/root/bitmap blocks into <paramref name="hostData"/> starting at
    /// <paramref name="baseBlock"/> (block-number units, not bytes) - lets a caller embed a formatted
    /// OFS/FFS volume directly into a larger shared array (e.g. one partition of an RDB <c>.hdf</c>)
    /// instead of building a standalone image and copying it in. All block numbers written into the
    /// volume's own structures (root/bitmap pointers) are relative to <paramref name="baseBlock"/>,
    /// exactly as <see cref="AdfImage.CreateWindow"/> expects.
    /// </summary>
    internal static void WriteBlankInto(byte[] hostData, int baseBlock, int sectorCount, string volumeLabel, byte bootFlags)
    {
        if (sectorCount < 4)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sectorCount), sectorCount, "Need at least 4 blocks (boot x2, root, bitmap).");
        }

        int rootBlock = sectorCount / 2;
        int bitmapBlock = rootBlock + 1;
        if (bitmapBlock >= sectorCount)
        {
            throw new ArgumentOutOfRangeException(nameof(sectorCount), sectorCount, "Image too small.");
        }

        long baseOffset = (long)baseBlock * SectorSize;

        WriteBootBlock(hostData, baseOffset, rootBlock, bootFlags);
        WriteRootBlock(hostData, baseOffset, rootBlock, bitmapBlock, volumeLabel);
        WriteBitmapBlock(hostData, baseOffset, bitmapBlock, sectorCount, rootBlock);
    }

    private static void WriteBootBlock(byte[] data, long baseOffset, int rootBlock, byte bootFlags)
    {
        int off = (int)baseOffset;
        data[off + 0] = (byte)'D';
        data[off + 1] = (byte)'O';
        data[off + 2] = (byte)'S';
        data[off + 3] = bootFlags;
        WriteInt32(data, off + 8, rootBlock);

        uint checksum = OfsChecksum.ComputeBootChecksum(
            data.AsSpan(off, SectorSize), data.AsSpan(off + SectorSize, SectorSize));
        WriteUInt32(data, off + OfsBlockOffsets.Boot_Checksum, checksum);
    }

    private static void WriteRootBlock(byte[] data, long baseOffset, int rootBlock, int bitmapBlock, string volumeLabel)
    {
        int off = (int)baseOffset + rootBlock * SectorSize;
        WriteInt32(data, off + OfsBlockOffsets.Type, BlockType.Header);
        WriteInt32(data, off + OfsBlockOffsets.Root_HashTableSize, OfsBlockOffsets.HashTableCount);
        WriteInt32(data, off + OfsBlockOffsets.Root_BitmapFlag, -1); // valid
        WriteInt32(data, off + OfsBlockOffsets.Root_BitmapPages, bitmapBlock);

        var (days, mins, ticks) = AmigaTime.FromDateTimeUtc(DateTime.UtcNow);
        WriteInt32(data, off + 420, days); // root alteration date
        WriteInt32(data, off + 424, mins);
        WriteInt32(data, off + 428, ticks);
        WriteInt32(data, off + 472, days); // creation date
        WriteInt32(data, off + 476, mins);
        WriteInt32(data, off + 480, ticks);

        WriteBcplString(
            data, off + OfsBlockOffsets.Root_NameLen, off + OfsBlockOffsets.Root_Name, volumeLabel,
            OfsBlockOffsets.Root_NameMaxLength);

        WriteInt32(data, off + OfsBlockOffsets.SecType, SecType.Root);

        uint checksum = BlockReader.ComputeNormalChecksum(
            data.AsSpan(off, SectorSize), OfsBlockOffsets.Checksum, 128);
        WriteUInt32(data, off + OfsBlockOffsets.Checksum, checksum);
    }

    private static void WriteBitmapBlock(byte[] data, long baseOffset, int bitmapBlock, int sectorCount, int rootBlock)
    {
        int off = (int)baseOffset + bitmapBlock * SectorSize;
        for (int block = 2; block < sectorCount; block++)
        {
            if (block == rootBlock || block == bitmapBlock)
            {
                continue; // left as "used" (bit stays 0)
            }

            MarkFree(data, off, block);
        }

        uint checksum = BlockReader.ComputeNormalChecksum(data.AsSpan(off, SectorSize), 0, 128);
        WriteUInt32(data, off, checksum);
    }

    /// <summary>
    /// Sets the free bit for <paramref name="blockNumber"/> per the format's bit-1-means-free
    /// convention. Block numbers are offset by 2 because blocks 0-1 (the boot block) are never
    /// represented in the bitmap at all.
    /// </summary>
    private static void MarkFree(byte[] data, int bitmapBlockOffset, int blockNumber)
    {
        int sectOfMap = blockNumber - 2;
        int mapIndex = (sectOfMap / 32) % BitmapMapWords;
        int bitPos = sectOfMap % 32;
        int wordOffset = bitmapBlockOffset + 4 + mapIndex * 4;

        uint word = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(wordOffset, 4));
        word |= 1u << bitPos;
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(wordOffset, 4), word);
    }

    private static void WriteInt32(byte[] data, int offset, int value) =>
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(offset, 4), value);

    private static void WriteUInt32(byte[] data, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset, 4), value);

    private static void WriteBcplString(byte[] data, int lengthOffset, int dataOffset, string value, int maxLen)
    {
        var bytes = Encoding.Latin1.GetBytes(value);
        int len = Math.Min(bytes.Length, maxLen);
        data[lengthOffset] = (byte)len;
        Array.Copy(bytes, 0, data, dataOffset, len);
    }
}
