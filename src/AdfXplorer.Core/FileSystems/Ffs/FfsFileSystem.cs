using AdfXplorer.Core.DiskImage;
using AdfXplorer.Core.FileSystems.Ofs;

namespace AdfXplorer.Core.FileSystems.Ffs;

/// <summary>
/// Read-only reader for the Amiga "Fast File System" (FFS, boot-block flags with bit 0 set - "DOS\1"
/// plain, "DOS\3" international, "DOS\5" international+dircache; the latter two read identically to
/// plain FFS here - see <see cref="AmigaHashDirectoryFileSystem"/>'s class comment).
///
/// Root/directory-block layout, checksums, and directory-hash-table walking are byte-identical to OFS
/// and shared via <see cref="AmigaHashDirectoryFileSystem"/>. The one real structural difference is file
/// *data* storage (confirmed against ADFlib's src/adf_blk.h and src/adf_file_block.c):
/// - Data blocks are raw, headerless, **checksum-less** 512-byte payloads (no per-block header/checksum
///   the way OFS data blocks have).
/// - A file header (or extension) block lists up to 72 data-block numbers directly in its 72-slot table
///   (the same offset 24 a directory block uses for its hash table, <see cref="OfsBlockOffsets.HashTable"/>),
///   filled **right-aligned**: the file's first data block is always at slot 71, the second at slot 70,
///   etc. - not left-aligned from slot 0. <see cref="OfsBlockOffsets.FileHeader_HighSeq"/> (offset 8)
///   gives how many of *this* block's slots are populated.
/// - Files needing more than 72 data blocks continue into **extension blocks** - struct-identical to a
///   file header block, chained via <see cref="OfsBlockOffsets.Extension"/> (offset 504, 0 = last),
///   each with its own 72-slot table and its own <c>highSeq</c>.
/// </summary>
public sealed class FfsFileSystem : AmigaHashDirectoryFileSystem
{
    /// <summary>Maximum number of 512-byte sectors supported by FFS (4 GiB / 8,388,608 sectors).</summary>
    public new const int MaxSupportedSectors = AmigaHashDirectoryFileSystem.MaxSupportedSectors;

    private const int SlotsPerTable = 72;

    private FfsFileSystem(AdfImage image, int rootBlock) : base(image, rootBlock)
    {
    }

    protected override string FormatDisplayName => "FFS";

    /// <summary>
    /// Detects a plain/international/dircache FFS volume by boot-block signature and flags, then
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

        // Bit 0 set = FFS. Accepts flags 1 (plain), 3 (international), 5 (international+dircache) - the
        // latter two read identically to plain FFS (see class comment); anything above 5 is an
        // unrecognized variant.
        int flags = boot[3];
        if (flags > 5 || (flags & 1) != 1)
        {
            return null;
        }

        if (!AmigaDosRootBlock.TryFind(image, out int rootBlockNumber))
        {
            return null;
        }

        return new FfsFileSystem(image, rootBlockNumber);
    }

    /// <summary>
    /// Reads a file's contents by streaming data blocks on demand through <see cref="FfsFileDataStream"/>,
    /// keeping only data-block sector indices in memory.
    /// </summary>
    protected override Stream OpenFileData(int headerBlock, long size) =>
        new FfsFileDataStream(Image, headerBlock, size, IsBlockAccepted);

    protected override IEnumerable<ChecksumReport> ScanFileDataBlocks(int headerBlock, string fileLabel)
    {
        // Raw data blocks carry no checksum (nothing to report); only extension blocks - which have the
        // same real checksum field as any other header-shaped block - are worth reporting.
        int extension = ReadExtension(headerBlock);
        int seq = 0;
        while (extension != 0)
        {
            seq++;
            var (stored, computed) = ReadBlockStatus(extension);
            yield return new ChecksumReport(
                $"extension block {seq} of file '{fileLabel}' (block {extension})", extension,
                OfsBlockOffsets.Checksum, stored == computed, stored, computed);

            extension = ReadExtension(extension);
        }
    }

    private int ReadExtension(int blockNum) =>
        BlockReader.ReadInt32(Image.ReadBlock(blockNum), OfsBlockOffsets.Extension);

    // --- write support ---

    protected override int DataBlockPayloadSize => AdfImage.SectorSize;

    protected override int WriteToDataBlock(int headerBlock, int logicalBlockIndex, int blockOffset, ReadOnlySpan<byte> data)
    {
        int tableIndex = logicalBlockIndex / SlotsPerTable;
        int slot = logicalBlockIndex % SlotsPerTable;
        int tableBlock = GetOrAllocateTableBlock(headerBlock, tableIndex);

        var table = Image.GetBlockForWrite(tableBlock);
        int slotOffset = OfsBlockOffsets.HashTable + (SlotsPerTable - 1 - slot) * 4;
        int dataBlockNum = BlockReader.ReadInt32(table, slotOffset);
        if (dataBlockNum == 0)
        {
            dataBlockNum = AllocateBlock();
            table = Image.GetBlockForWrite(tableBlock); // AllocateBlock may have zeroed a block; re-fetch defensively
            BlockWriter.WriteInt32(table, slotOffset, dataBlockNum);
        }

        int highSeq = BlockReader.ReadInt32(table, OfsBlockOffsets.FileHeader_HighSeq);
        if (slot + 1 > highSeq)
        {
            BlockWriter.WriteInt32(table, OfsBlockOffsets.FileHeader_HighSeq, slot + 1);
        }

        RewriteChecksum(table);

        var dataBlock = Image.GetBlockForWrite(dataBlockNum);
        int toWrite = Math.Min(data.Length, AdfImage.SectorSize - blockOffset);
        data[..toWrite].CopyTo(dataBlock.Slice(blockOffset, toWrite));

        return toWrite;
    }

    /// <summary>Walks (or extends) the extension-block chain to the table at <paramref name="tableIndex"/>
    /// (0 = <paramref name="headerBlock"/> itself), allocating a new extension block as needed.</summary>
    private int GetOrAllocateTableBlock(int headerBlock, int tableIndex)
    {
        int current = headerBlock;
        for (int i = 0; i < tableIndex; i++)
        {
            int next = BlockReader.ReadInt32(Image.ReadBlock(current), OfsBlockOffsets.Extension);
            if (next == 0)
            {
                next = AllocateBlock();
                var newExt = Image.GetBlockForWrite(next);
                BlockWriter.WriteInt32(newExt, OfsBlockOffsets.Type, BlockType.List);
                BlockWriter.WriteInt32(newExt, OfsBlockOffsets.Parent, headerBlock);
                BlockWriter.WriteInt32(newExt, OfsBlockOffsets.SecType, SecType.File);
                RewriteChecksum(newExt);

                var cur = Image.GetBlockForWrite(current);
                BlockWriter.WriteInt32(cur, OfsBlockOffsets.Extension, next);
                RewriteChecksum(cur);
            }

            current = next;
        }

        return current;
    }

    protected override void FreeDataBlocksFrom(int headerBlock, int logicalBlockIndex)
    {
        int startTableIndex = logicalBlockIndex / SlotsPerTable;
        int startSlot = logicalBlockIndex % SlotsPerTable;

        int tableBlock = headerBlock;
        int tableIndex = 0;
        while (tableIndex < startTableIndex)
        {
            int next = BlockReader.ReadInt32(Image.ReadBlock(tableBlock), OfsBlockOffsets.Extension);
            if (next == 0)
            {
                return; // chain doesn't extend this far - nothing to free
            }

            tableBlock = next;
            tableIndex++;
        }

        FreeTableSlotsFrom(tableBlock, startSlot);

        int firstExtensionToFree = BlockReader.ReadInt32(Image.ReadBlock(tableBlock), OfsBlockOffsets.Extension);
        var table = Image.GetBlockForWrite(tableBlock);
        BlockWriter.WriteInt32(table, OfsBlockOffsets.Extension, 0);
        RewriteChecksum(table);

        int current = firstExtensionToFree;
        while (current != 0)
        {
            int next = BlockReader.ReadInt32(Image.ReadBlock(current), OfsBlockOffsets.Extension);
            FreeTableSlotsFrom(current, 0);
            FreeBlock(current);
            current = next;
        }
    }

    private void FreeTableSlotsFrom(int tableBlock, int fromSlot)
    {
        var table = Image.GetBlockForWrite(tableBlock);
        int highSeq = BlockReader.ReadInt32(table, OfsBlockOffsets.FileHeader_HighSeq);
        for (int slot = fromSlot; slot < highSeq; slot++)
        {
            int slotOffset = OfsBlockOffsets.HashTable + (SlotsPerTable - 1 - slot) * 4;
            int dataBlockNum = BlockReader.ReadInt32(table, slotOffset);
            if (dataBlockNum != 0)
            {
                FreeBlock(dataBlockNum);
                table = Image.GetBlockForWrite(tableBlock); // FreeBlock may touch the bitmap block only, but re-fetch defensively
                BlockWriter.WriteInt32(table, slotOffset, 0);
            }
        }

        if (fromSlot < highSeq)
        {
            BlockWriter.WriteInt32(table, OfsBlockOffsets.FileHeader_HighSeq, fromSlot);
            RewriteChecksum(table);
        }
    }
}
