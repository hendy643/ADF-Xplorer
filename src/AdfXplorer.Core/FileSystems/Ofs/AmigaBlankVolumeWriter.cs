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
        if (sectorCount < 4)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sectorCount), sectorCount, "Need at least 4 blocks (boot x2, root, bitmap).");
        }

        if (sectorCount > AmigaHashDirectoryFileSystem.MaxSupportedSectors)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sectorCount),
                sectorCount,
                $"Sector count {sectorCount:N0} exceeds the 4 GiB limit ({AmigaHashDirectoryFileSystem.MaxSupportedSectors:N0} sectors).");
        }

        var image = AdfImage.CreateEmpty(sectorCount);
        WriteBlankInto(image, 0, sectorCount, volumeLabel, bootFlags);
        return image;
    }

    /// <summary>
    /// Writes a blank volume's boot/root/bitmap blocks into <paramref name="image"/> starting at
    /// <paramref name="baseBlock"/> (block-number units, not bytes) - lets a caller embed a formatted
    /// OFS/FFS volume directly into an image (e.g. one partition of an RDB <c>.hdf</c>)
    /// instead of building a standalone image and copying it in. All block numbers written into the
    /// volume's own structures (root/bitmap pointers) are relative to <paramref name="baseBlock"/>,
    /// exactly as <see cref="AdfImage.CreateWindow"/> expects.
    /// </summary>
    internal static void WriteBlankInto(AdfImage image, long baseBlock, int sectorCount, string volumeLabel, byte bootFlags)
    {
        if (sectorCount < 4)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sectorCount), sectorCount, "Need at least 4 blocks (boot x2, root, bitmap).");
        }

        if (sectorCount > AmigaHashDirectoryFileSystem.MaxSupportedSectors)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sectorCount),
                sectorCount,
                $"Sector count {sectorCount:N0} exceeds the 4 GiB limit ({AmigaHashDirectoryFileSystem.MaxSupportedSectors:N0} sectors).");
        }

        int rootBlock = sectorCount / 2;
        int bitmapBlock = rootBlock + 1;
        if (bitmapBlock >= sectorCount)
        {
            throw new ArgumentOutOfRangeException(nameof(sectorCount), sectorCount, "Image too small.");
        }

        WriteBootBlock(image, baseBlock, rootBlock, bootFlags);
        WriteRootBlock(image, baseBlock, rootBlock, bitmapBlock, volumeLabel);
        WriteBitmapBlock(image, baseBlock, bitmapBlock, sectorCount, rootBlock);
    }

    private static void WriteBootBlock(AdfImage image, long baseBlock, int rootBlock, byte bootFlags)
    {
        var boot0 = image.GetBlockForWrite(baseBlock + 0);
        boot0[0] = (byte)'D';
        boot0[1] = (byte)'O';
        boot0[2] = (byte)'S';
        boot0[3] = bootFlags;
        WriteInt32(boot0, 8, rootBlock);

        var boot1 = image.GetBlockForWrite(baseBlock + 1);
        uint checksum = OfsChecksum.ComputeBootChecksum(boot0, boot1);
        WriteUInt32(boot0, OfsBlockOffsets.Boot_Checksum, checksum);
    }

    private static void WriteRootBlock(AdfImage image, long baseBlock, int rootBlock, int bitmapBlock, string volumeLabel)
    {
        var root = image.GetBlockForWrite(baseBlock + rootBlock);
        WriteInt32(root, OfsBlockOffsets.Type, BlockType.Header);
        WriteInt32(root, OfsBlockOffsets.Root_HashTableSize, OfsBlockOffsets.HashTableCount);
        WriteInt32(root, OfsBlockOffsets.Root_BitmapFlag, -1); // valid
        WriteInt32(root, OfsBlockOffsets.Root_BitmapPages, bitmapBlock);

        var (days, mins, ticks) = AmigaTime.FromDateTimeUtc(DateTime.UtcNow);
        WriteInt32(root, 420, days); // root alteration date
        WriteInt32(root, 424, mins);
        WriteInt32(root, 428, ticks);
        WriteInt32(root, 472, days); // creation date
        WriteInt32(root, 476, mins);
        WriteInt32(root, 480, ticks);

        WriteBcplString(
            root, OfsBlockOffsets.Root_NameLen, OfsBlockOffsets.Root_Name, volumeLabel,
            OfsBlockOffsets.Root_NameMaxLength);

        WriteInt32(root, OfsBlockOffsets.SecType, SecType.Root);

        uint checksum = BlockReader.ComputeNormalChecksum(
            root, OfsBlockOffsets.Checksum, 128);
        WriteUInt32(root, OfsBlockOffsets.Checksum, checksum);
    }

    private static void WriteBitmapBlock(AdfImage image, long baseBlock, int bitmapBlock, int sectorCount, int rootBlock)
    {
        var bitmap = image.GetBlockForWrite(baseBlock + bitmapBlock);
        int maxBlockInMap = Math.Min(sectorCount, 2 + BitmapMapWords * 32);
        for (int block = 2; block < maxBlockInMap; block++)
        {
            if (block == rootBlock || block == bitmapBlock)
            {
                continue; // left as "used" (bit stays 0)
            }

            MarkFree(bitmap, block);
        }

        uint checksum = BlockReader.ComputeNormalChecksum(bitmap, 0, 128);
        WriteUInt32(bitmap, 0, checksum);
    }

    /// <summary>
    /// Sets the free bit for <paramref name="blockNumber"/> per the format's bit-1-means-free
    /// convention. Block numbers are offset by 2 because blocks 0-1 (the boot block) are never
    /// represented in the bitmap at all.
    /// </summary>
    private static void MarkFree(Span<byte> bitmapBlock, int blockNumber)
    {
        int sectOfMap = blockNumber - 2;
        int mapIndex = (sectOfMap / 32) % BitmapMapWords;
        int bitPos = sectOfMap % 32;
        int wordOffset = 4 + mapIndex * 4;

        uint word = BinaryPrimitives.ReadUInt32BigEndian(bitmapBlock.Slice(wordOffset, 4));
        word |= 1u << bitPos;
        BinaryPrimitives.WriteUInt32BigEndian(bitmapBlock.Slice(wordOffset, 4), word);
    }

    private static void WriteInt32(Span<byte> data, int offset, int value) =>
        BinaryPrimitives.WriteInt32BigEndian(data.Slice(offset, 4), value);

    private static void WriteUInt32(Span<byte> data, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32BigEndian(data.Slice(offset, 4), value);

    private static void WriteBcplString(Span<byte> data, int lengthOffset, int dataOffset, string value, int maxLen)
    {
        var bytes = Encoding.Latin1.GetBytes(value);
        int len = Math.Min(bytes.Length, maxLen);
        data[lengthOffset] = (byte)len;
        bytes.AsSpan(0, len).CopyTo(data.Slice(dataOffset, len));
    }
}
