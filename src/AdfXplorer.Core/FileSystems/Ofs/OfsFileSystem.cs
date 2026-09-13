using AdfXplorer.Core.DiskImage;

namespace AdfXplorer.Core.FileSystems.Ofs;

/// <summary>
/// Read-only reader for the Amiga "Original File System" (OFS, boot-block flags with bit 0 clear -
/// "DOS\0" plain, "DOS\2" international, "DOS\4" international+dircache; the latter two read
/// identically to plain OFS here - see <see cref="AmigaHashDirectoryFileSystem"/>'s class comment).
///
/// Block layout reference: https://github.com/adflib/ADFlib/blob/master/src/adf_blk.h (see
/// <see cref="OfsBlockOffsets"/>). Directory-hash algorithm reference:
/// https://github.com/adflib/ADFlib/blob/master/doc/FAQ/adf_info_V0_9.txt (see <see cref="AmigaHash"/>).
/// Checksum algorithms: <see cref="BlockReader.ComputeNormalChecksum"/> (root/directory/file-header/data
/// blocks) and <see cref="OfsChecksum.ComputeBootChecksum"/> (the boot block only).
///
/// Directory-walk, entry-building, and checksum-validation logic is shared with
/// <see cref="Ffs.FfsFileSystem"/> via <see cref="AmigaHashDirectoryFileSystem"/> - this class only
/// contains what's genuinely OFS-specific: format detection and the data-block chain read.
/// </summary>
public sealed class OfsFileSystem : AmigaHashDirectoryFileSystem
{
    /// <summary>Maximum number of 512-byte sectors supported by OFS (4 GiB / 8,388,608 sectors).</summary>
    public new const int MaxSupportedSectors = AmigaHashDirectoryFileSystem.MaxSupportedSectors;

    private OfsFileSystem(AdfImage image, int rootBlock) : base(image, rootBlock)
    {
    }

    protected override string FormatDisplayName => "OFS";

    /// <summary>
    /// Detects a plain/international/dircache OFS volume by boot-block signature and flags, then
    /// validates the root block it points to actually looks like a root before committing to it -
    /// returns <see langword="null"/> (rather than throwing) on any mismatch so callers can fall through
    /// to trying other filesystem formats against the same image.
    /// </summary>
    public static IAmigaFileSystem? TryMount(AdfImage image)
    {
        if (image.SectorCount < 2 || image.SectorCount > MaxSupportedSectors)
        {
            return null;
        }

        var boot = image.ReadBlock(0);
        if (boot[0] != (byte)'D' || boot[1] != (byte)'O' || boot[2] != (byte)'S')
        {
            return null;
        }

        // Bit 0 clear = OFS. Accepts flags 0 (plain), 2 (international), 4 (international+dircache) -
        // the latter two read identically to plain OFS (see class comment); anything above 5 is an
        // unrecognized variant.
        int flags = boot[3];
        if (flags > 5 || (flags & 1) != 0)
        {
            return null;
        }

        if (!AmigaDosRootBlock.TryFind(image, out int rootBlockNumber))
        {
            return null;
        }

        return new OfsFileSystem(image, rootBlockNumber);
    }

    /// <summary>
    /// Reads a file's contents by streaming data blocks on demand through <see cref="OfsFileDataStream"/>,
    /// keeping only data-block sector indices in memory.
    /// </summary>
    protected override Stream OpenFileData(int headerBlock, long size) =>
        new OfsFileDataStream(Image, headerBlock, size, IsBlockAccepted);

    protected override IEnumerable<ChecksumReport> ScanFileDataBlocks(int headerBlock, string fileLabel)
    {
        var header = Image.ReadBlock(headerBlock);
        int nextData = BlockReader.ReadInt32(header, OfsBlockOffsets.FileHeader_FirstData);
        long size = BlockReader.ReadUInt32(header, OfsBlockOffsets.FileHeader_ByteSize);

        int written = 0;
        int seq = 0;
        while (nextData != 0 && written < size)
        {
            seq++;
            var (stored, computed) = ReadBlockStatus(nextData);
            yield return new ChecksumReport(
                $"data block {seq} of file '{fileLabel}' (block {nextData})", nextData, OfsBlockOffsets.Checksum,
                stored == computed, stored, computed);

            var data = Image.ReadBlock(nextData);
            int dataSize = (int)BlockReader.ReadUInt32(data, OfsBlockOffsets.Data_DataSize);
            written += Math.Min(dataSize, (int)size - written);
            nextData = BlockReader.ReadInt32(data, OfsBlockOffsets.Data_NextData);
        }
    }

    // --- write support ---

    protected override int DataBlockPayloadSize => OfsBlockOffsets.Data_PayloadMaxSize;

    protected override int CalculateRequiredBlocks(int headerBlock, long offset, long length)
    {
        if (length <= 0)
        {
            return 0;
        }

        long newEnd = offset + length;
        int totalBlocksNeeded = (int)((newEnd - 1) / DataBlockPayloadSize) + 1;

        int existingCount = 0;
        int current = BlockReader.ReadInt32(Image.ReadBlock(headerBlock), OfsBlockOffsets.FileHeader_FirstData);
        while (current != 0)
        {
            existingCount++;
            current = BlockReader.ReadInt32(Image.ReadBlock(current), OfsBlockOffsets.Data_NextData);
        }

        return Math.Max(0, totalBlocksNeeded - existingCount);
    }

    protected override int WriteToDataBlock(int headerBlock, int logicalBlockIndex, int blockOffset, ReadOnlySpan<byte> data)
    {
        int dataBlockNum = GetOrAllocateDataBlock(headerBlock, logicalBlockIndex);
        var block = Image.GetBlockForWrite(dataBlockNum);

        int capacity = OfsBlockOffsets.Data_PayloadMaxSize - blockOffset;
        int toWrite = Math.Min(data.Length, capacity);
        data[..toWrite].CopyTo(block.Slice(OfsBlockOffsets.Data_Payload + blockOffset, toWrite));

        int existingDataSize = BlockReader.ReadInt32(block, OfsBlockOffsets.Data_DataSize);
        int newDataSize = Math.Max(existingDataSize, blockOffset + toWrite);
        BlockWriter.WriteInt32(block, OfsBlockOffsets.Type, BlockType.Data);
        BlockWriter.WriteInt32(block, OfsBlockOffsets.Data_HeaderKey, headerBlock);
        BlockWriter.WriteInt32(block, OfsBlockOffsets.Data_SeqNum, logicalBlockIndex + 1);
        BlockWriter.WriteInt32(block, OfsBlockOffsets.Data_DataSize, newDataSize);
        RewriteChecksum(block);

        return toWrite;
    }

    /// <summary>Walks (or extends) the <c>next_data</c> chain to <paramref name="logicalBlockIndex"/>,
    /// allocating and linking new blocks as needed.</summary>
    private int GetOrAllocateDataBlock(int headerBlock, int logicalBlockIndex)
    {
        int prev = 0; // 0 sentinel = "link via the header's first_data field", not a previous data block
        int current = BlockReader.ReadInt32(Image.ReadBlock(headerBlock), OfsBlockOffsets.FileHeader_FirstData);

        for (int i = 0; ; i++)
        {
            if (current == 0)
            {
                current = AllocateBlock();
                if (prev == 0)
                {
                    var header = Image.GetBlockForWrite(headerBlock);
                    BlockWriter.WriteInt32(header, OfsBlockOffsets.FileHeader_FirstData, current);
                }
                else
                {
                    var prevBlock = Image.GetBlockForWrite(prev);
                    BlockWriter.WriteInt32(prevBlock, OfsBlockOffsets.Data_NextData, current);
                    RewriteChecksum(prevBlock);
                }
            }

            if (i == logicalBlockIndex)
            {
                return current;
            }

            prev = current;
            current = BlockReader.ReadInt32(Image.ReadBlock(current), OfsBlockOffsets.Data_NextData);
        }
    }

    protected override void FreeDataBlocksFrom(int headerBlock, int logicalBlockIndex)
    {
        int current = BlockReader.ReadInt32(Image.ReadBlock(headerBlock), OfsBlockOffsets.FileHeader_FirstData);
        int newTailBlock = 0; // 0 sentinel = the header's first_data should become 0
        int index = 0;

        while (current != 0 && index < logicalBlockIndex)
        {
            newTailBlock = current;
            current = BlockReader.ReadInt32(Image.ReadBlock(current), OfsBlockOffsets.Data_NextData);
            index++;
        }

        while (current != 0)
        {
            int next = BlockReader.ReadInt32(Image.ReadBlock(current), OfsBlockOffsets.Data_NextData);
            FreeBlock(current);
            current = next;
        }

        if (newTailBlock == 0)
        {
            var header = Image.GetBlockForWrite(headerBlock);
            BlockWriter.WriteInt32(header, OfsBlockOffsets.FileHeader_FirstData, 0);
        }
        else
        {
            var tail = Image.GetBlockForWrite(newTailBlock);
            BlockWriter.WriteInt32(tail, OfsBlockOffsets.Data_NextData, 0);
            RewriteChecksum(tail);
        }
    }
}
