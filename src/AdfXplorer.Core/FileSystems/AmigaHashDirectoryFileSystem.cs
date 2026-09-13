using AdfXplorer.Core.DiskImage;
using AdfXplorer.Core.FileSystems.Ofs;

namespace AdfXplorer.Core.FileSystems;

/// <summary>
/// Shared logic for Amiga filesystem formats that use the hash-table-based directory structure common
/// to OFS and FFS - root/directory block hash table at offset 24, file-header-block tail layout
/// (name/comment/dates/parent/sec-type), and boot+root checksum validation are all byte-identical
/// between those formats (confirmed against ADFlib's <c>AdfRootBlock</c>/<c>AdfEntryBlock</c>/
/// <c>AdfFileHeaderBlock</c> structs - https://github.com/adflib/ADFlib/blob/master/src/adf_blk.h).
///
/// Subclasses differ only in how file *data* is stored/read - see <see cref="OpenFileData"/> and
/// <see cref="ScanFileDataBlocks"/> - which is the one place OFS and FFS genuinely diverge on disk.
/// </summary>
public abstract class AmigaHashDirectoryFileSystem : IAmigaFileSystem, IChecksumAware, IAmigaFileSystemWriter
{
    /// <summary>How to handle a checksum mismatch on a block encountered during live browsing (no
    /// interactive prompting is possible from a WinFsp dispatcher thread) - chosen once, up front, by
    /// <see cref="ValidateChecksums"/>.</summary>
    private enum ChecksumPolicy { Ignore, Reject }

    /// <summary>
    /// Maximum number of 512-byte sectors supported by 32-bit Amiga OFS/FFS formats (4 GiB / 8,388,608 sectors).
    /// </summary>
    public const int MaxSupportedSectors = 8_388_608;

    protected readonly AdfImage Image;
    protected readonly int RootBlock;
    private ChecksumPolicy _laterEntryPolicy = ChecksumPolicy.Ignore;

    protected AmigaHashDirectoryFileSystem(AdfImage image, int rootBlock)
    {
        if (image.SectorCount > MaxSupportedSectors)
        {
            throw new ArgumentOutOfRangeException(
                nameof(image),
                $"Image sector count {image.SectorCount:N0} exceeds the 4 GiB limit ({MaxSupportedSectors:N0} sectors).");
        }

        Image = image;
        RootBlock = rootBlock;
    }

    /// <summary>
    /// The synthetic "blockDescription" <see cref="ValidateChecksums"/> passes when asking (once, up
    /// front) how to handle later per-entry checksum mismatches - not a real block, so <c>stored</c>/
    /// <c>computed</c> are both 0. Exposed so a <see cref="ChecksumConflictHandler"/> can recognize and
    /// render this differently from an actual mismatch (see <c>Program.PromptChecksumDecision</c>).
    /// </summary>
    public const string LaterEntryPolicyQuestion =
        "later file/folder checksum mismatches for the rest of this mount (no live repair - Repair here " +
        "behaves the same as Ignore)";

    /// <summary>Short format name ("OFS"/"FFS") used in boot/root <see cref="ChecksumReport"/> labels.</summary>
    protected abstract string FormatDisplayName { get; }

    public string FileSystemName => $"Amiga {FormatDisplayName}";

    public string VolumeLabel
    {
        get
        {
            var root = Image.ReadBlock(RootBlock);
            return BlockReader.ReadBcplString(
                root, OfsBlockOffsets.Root_NameLen, OfsBlockOffsets.Root_Name, OfsBlockOffsets.Root_NameMaxLength);
        }
    }

    public IReadOnlyList<AmigaDirectoryEntry> ListDirectory(string path)
    {
        int dirBlock = ResolveDirectoryBlock(path);
        var result = new List<AmigaDirectoryEntry>();
        foreach (int blockNum in EnumerateChildBlocks(dirBlock))
        {
            var entry = ReadEntry(blockNum);
            if (entry is not null)
            {
                result.Add(entry);
            }
        }

        return result;
    }

    public bool TryGetEntry(string path, out AmigaDirectoryEntry entry)
    {
        var (parentBlock, name) = SplitPath(path);
        if (name is null)
        {
            entry = new AmigaDirectoryEntry(VolumeLabel, IsDirectory: true, Size: 0, DateTime.UnixEpoch, Comment: "");
            return true;
        }

        int? blockNum = FindChildBlock(parentBlock, name);
        if (blockNum is null)
        {
            entry = null!;
            return false;
        }

        entry = BuildEntry(blockNum.Value);
        return true;
    }

    public Stream OpenRead(string path)
    {
        var (parentBlock, name) = SplitPath(path);
        if (name is null)
        {
            throw new IOException("Path refers to the root, which is not a file.");
        }

        int blockNum = FindChildBlock(parentBlock, name)
            ?? throw new FileNotFoundException($"'{path}' was not found.", path);

        var header = Image.ReadBlock(blockNum);
        if (BlockReader.ReadInt32(header, OfsBlockOffsets.SecType) != SecType.File)
        {
            throw new IOException($"'{path}' is not a file.");
        }

        long size = BlockReader.ReadUInt32(header, OfsBlockOffsets.FileHeader_ByteSize);
        return OpenFileData(blockNum, size);
    }

    /// <summary>Format-specific file-content read, given the file's own header block and byte size.</summary>
    protected abstract Stream OpenFileData(int headerBlock, long size);

    /// <summary>
    /// Usable payload bytes per data block for this format - 488 for OFS (each block reserves 24 bytes
    /// for a header), 512 for FFS (data blocks are raw, headerless). The shared write loop below
    /// (<see cref="WriteFile"/>/<see cref="SetFileSize"/>) uses this, not the physical 512-byte sector
    /// size, to compute logical-block boundaries.
    /// </summary>
    protected abstract int DataBlockPayloadSize { get; }

    /// <summary>
    /// Calculates the number of new blocks (data blocks and any format-specific metadata/extension blocks)
    /// that would need to be allocated to satisfy a write or file-growth operation starting at <paramref name="offset"/>
    /// and writing <paramref name="length"/> bytes.
    /// </summary>
    protected abstract int CalculateRequiredBlocks(int headerBlock, long offset, long length);

    /// <summary>
    /// Writes as much of <paramref name="data"/> as fits into the data block at
    /// <paramref name="logicalBlockIndex"/> within <paramref name="headerBlock"/>'s file, starting at
    /// <paramref name="blockOffset"/> bytes into that block's payload (allocating the block, and
    /// growing the header/extension-block chain, if it doesn't exist yet). Returns how many bytes were
    /// actually written - the shared <see cref="WriteFile"/> loop calls this repeatedly, advancing to
    /// the next logical block each time, until all of <paramref name="data"/> is written. Also
    /// responsible for updating that data block's own metadata/checksum, if the format has any (OFS
    /// does; FFS's raw data blocks don't).
    /// </summary>
    protected abstract int WriteToDataBlock(int headerBlock, int logicalBlockIndex, int blockOffset, ReadOnlySpan<byte> data);

    /// <summary>
    /// Frees every data/extension block for <paramref name="headerBlock"/>'s file from
    /// <paramref name="logicalBlockIndex"/> onward (0 = the whole file, for delete; N = truncation),
    /// leaving the chain correctly terminated at that point.
    /// </summary>
    protected abstract void FreeDataBlocksFrom(int headerBlock, int logicalBlockIndex);

    // --- IChecksumAware ---

    public bool ValidateChecksums(ChecksumConflictHandler? onChecksumMismatch, out bool repaired)
    {
        repaired = false;

        if (Image.SectorCount >= 2)
        {
            var (bootStored, bootComputed) = ReadBootStatus();
            if (bootStored != bootComputed)
            {
                var decision =
                    onChecksumMismatch?.Invoke($"{FormatDisplayName} boot block", bootStored, bootComputed)
                    ?? ChecksumDecision.Ignore;
                if (decision == ChecksumDecision.Repair)
                {
                    Image.PatchChecksumField(0, OfsBlockOffsets.Boot_Checksum, bootComputed);
                    repaired = true;
                }
                // Reject: noted only - nothing beyond the boot block's already-validated-by-signature
                // "DOS\x" + root pointer is actually needed from it.
            }
        }

        var (rootStored, rootComputed) = ReadBlockStatus(RootBlock);
        if (rootStored != rootComputed)
        {
            var decision =
                onChecksumMismatch?.Invoke($"{FormatDisplayName} root block", rootStored, rootComputed)
                ?? ChecksumDecision.Ignore;
            switch (decision)
            {
                case ChecksumDecision.Repair:
                    Image.PatchChecksumField(RootBlock, OfsBlockOffsets.Checksum, rootComputed);
                    repaired = true;
                    break;
                case ChecksumDecision.Reject:
                    return false;
            }
        }

        // Only ask how to handle *later* mismatches (found while browsing) if this disk actually has any
        // - asking unconditionally meant a perfectly healthy disk still got a policy question on every
        // single mount. Worth a full tree walk up front since it's the only way to know without prompting.
        if (onChecksumMismatch is not null && ScanDirectory(RootBlock, "/").Any(r => !r.Valid))
        {
            var laterDecision = onChecksumMismatch.Invoke(LaterEntryPolicyQuestion, 0, 0);
            _laterEntryPolicy = laterDecision == ChecksumDecision.Reject ? ChecksumPolicy.Reject : ChecksumPolicy.Ignore;
        }

        return true;
    }

    public IEnumerable<ChecksumReport> ScanChecksums()
    {
        if (Image.SectorCount >= 2)
        {
            var (bootStored, bootComputed) = ReadBootStatus();
            yield return new ChecksumReport(
                $"{FormatDisplayName} boot block", 0, OfsBlockOffsets.Boot_Checksum,
                bootStored == bootComputed, bootStored, bootComputed);
        }

        var (rootStored, rootComputed) = ReadBlockStatus(RootBlock);
        yield return new ChecksumReport(
            $"{FormatDisplayName} root block", RootBlock, OfsBlockOffsets.Checksum,
            rootStored == rootComputed, rootStored, rootComputed);

        foreach (var report in ScanDirectory(RootBlock, "/"))
        {
            yield return report;
        }
    }

    public void RepairChecksum(ChecksumReport report) =>
        Image.PatchChecksumField(report.BlockNumber, report.ChecksumFieldOffset, report.Computed);

    private IEnumerable<ChecksumReport> ScanDirectory(int dirBlock, string pathLabel)
    {
        foreach (int blockNum in EnumerateChildBlocks(dirBlock))
        {
            var (name, isDirectory, secType) = ReadNameAndType(blockNum);
            var (stored, computed) = ReadBlockStatus(blockNum);
            string label = $"{(isDirectory ? "directory" : "file")} '{pathLabel}{name}' (block {blockNum})";
            yield return new ChecksumReport(label, blockNum, OfsBlockOffsets.Checksum, stored == computed, stored, computed);

            if (isDirectory)
            {
                foreach (var report in ScanDirectory(blockNum, $"{pathLabel}{name}/"))
                {
                    yield return report;
                }
            }
            else if (secType == SecType.File)
            {
                foreach (var report in ScanFileDataBlocks(blockNum, $"{pathLabel}{name}"))
                {
                    yield return report;
                }
            }
        }
    }

    /// <summary>Format-specific checksum reports for a file's data-block chain (called from
    /// <see cref="ScanChecksums"/>'s tree walk).</summary>
    protected abstract IEnumerable<ChecksumReport> ScanFileDataBlocks(int headerBlock, string fileLabel);

    private (uint Stored, uint Computed) ReadBootStatus()
    {
        var block0 = Image.ReadBlock(0);
        var block1 = Image.ReadBlock(1);
        uint stored = BlockReader.ReadUInt32(block0, OfsBlockOffsets.Boot_Checksum);
        uint computed = OfsChecksum.ComputeBootChecksum(block0, block1);
        return (stored, computed);
    }

    protected (uint Stored, uint Computed) ReadBlockStatus(int blockNum)
    {
        var block = Image.ReadBlock(blockNum);
        uint stored = BlockReader.ReadUInt32(block, OfsBlockOffsets.Checksum);
        uint computed = BlockReader.ComputeNormalChecksum(block, OfsBlockOffsets.Checksum, 128);
        return (stored, computed);
    }

    private (string Name, bool IsDirectory, int SecType) ReadNameAndType(int blockNum)
    {
        var block = Image.ReadBlock(blockNum);
        string name = BlockReader.ReadBcplString(
            block, OfsBlockOffsets.FileHeader_NameLen, OfsBlockOffsets.FileHeader_Name,
            OfsBlockOffsets.FileHeader_NameMaxLength);
        int secType = BlockReader.ReadInt32(block, OfsBlockOffsets.SecType);
        return (name, secType == SecType.UserDir, secType);
    }

    /// <summary>
    /// Validates a block's checksum against the standing <see cref="_laterEntryPolicy"/> (no live
    /// prompting - the policy was already chosen once, up front, in <see cref="ValidateChecksums"/>).
    /// A mismatch always logs a best-effort warning to stderr; returns false only under
    /// <see cref="ChecksumPolicy.Reject"/>.
    /// </summary>
    protected bool IsBlockAccepted(int blockNum, string blockKind)
    {
        var (stored, computed) = ReadBlockStatus(blockNum);
        if (stored == computed)
        {
            return true;
        }

        if (_laterEntryPolicy == ChecksumPolicy.Reject)
        {
            Console.Error.WriteLine(
                $"Skipping {blockKind} at block {blockNum}: checksum mismatch " +
                $"(stored=0x{stored:X8}, computed=0x{computed:X8}).");
            return false;
        }

        Console.Error.WriteLine(
            $"Warning: {blockKind} at block {blockNum} has a checksum mismatch " +
            $"(stored=0x{stored:X8}, computed=0x{computed:X8}); using it anyway.");
        return true;
    }

    // --- IAmigaFileSystemWriter ---

    public bool SupportsSafeWrite
    {
        get
        {
            var root = Image.ReadBlock(RootBlock);
            // AmigaDOS convention: -1 means the bitmap is valid/up to date; 0 (or anything else)
            // means the OS considers it stale and would rebuild it by scanning every block on mount.
            if (BlockReader.ReadInt32(root, OfsBlockOffsets.Root_BitmapFlag) != -1)
            {
                return false;
            }

            if (BlockReader.ReadInt32(root, OfsBlockOffsets.Root_BitmapExtension) != 0)
            {
                return false;
            }

            for (int i = 1; i < OfsBlockOffsets.Root_BitmapPagesCount; i++)
            {
                if (BlockReader.ReadInt32(root, OfsBlockOffsets.Root_BitmapPages + i * 4) != 0)
                {
                    return false;
                }
            }

            int bitmapBlock = BlockReader.ReadInt32(root, OfsBlockOffsets.Root_BitmapPages);
            return bitmapBlock > 0 && bitmapBlock < Image.SectorCount;
        }
    }

    public int FreeBlockCount
    {
        get
        {
            int bitmapBlock = GetBitmapBlockOrThrow();
            var bitmap = Image.ReadBlock(bitmapBlock);
            int freeBlocks = 0;
            for (int b = 2; b < Image.SectorCount; b++)
            {
                if (b != bitmapBlock && IsBlockFree(bitmap, b))
                {
                    freeBlocks++;
                }
            }

            return freeBlocks;
        }
    }

    public long FreeBytes => (long)FreeBlockCount * AdfImage.SectorSize;

    public AmigaDirectoryEntry CreateFile(string path) => CreateEntry(path, SecType.File);

    public AmigaDirectoryEntry CreateDirectory(string path) => CreateEntry(path, SecType.UserDir);

    private AmigaDirectoryEntry CreateEntry(string path, int secType)
    {
        var (parentBlock, name) = SplitPath(path);
        if (name is null)
        {
            throw new IOException("Cannot create the root.");
        }

        if (FindChildBlock(parentBlock, name) is not null)
        {
            throw new IOException($"'{path}' already exists.");
        }

        int entryBlock = AllocateBlock();
        var block = Image.GetBlockForWrite(entryBlock);
        BlockWriter.WriteInt32(block, OfsBlockOffsets.Type, BlockType.Header);
        BlockWriter.WriteBcplString(
            block, OfsBlockOffsets.FileHeader_NameLen, OfsBlockOffsets.FileHeader_Name, name,
            OfsBlockOffsets.FileHeader_NameMaxLength);

        var (days, mins, ticks) = AmigaTime.FromDateTimeUtc(DateTime.UtcNow);
        BlockWriter.WriteInt32(block, OfsBlockOffsets.FileHeader_Days, days);
        BlockWriter.WriteInt32(block, OfsBlockOffsets.FileHeader_Mins, mins);
        BlockWriter.WriteInt32(block, OfsBlockOffsets.FileHeader_Ticks, ticks);
        BlockWriter.WriteInt32(block, OfsBlockOffsets.Parent, parentBlock);
        BlockWriter.WriteInt32(block, OfsBlockOffsets.SecType, secType);
        RewriteChecksum(block);

        InsertIntoDirectory(parentBlock, entryBlock, name);

        return BuildEntry(entryBlock);
    }

    public int WriteFile(string path, long offset, ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return 0;
        }

        int headerBlock = FindFileHeaderBlockOrThrow(path);
        int requiredBlocks = CalculateRequiredBlocks(headerBlock, offset, data.Length);
        if (requiredBlocks > FreeBlockCount)
        {
            throw new DiskFullException();
        }

        int payloadSize = DataBlockPayloadSize;
        long origSize = BlockReader.ReadUInt32(Image.ReadBlock(headerBlock), OfsBlockOffsets.FileHeader_ByteSize);
        int origBlocksNeeded = origSize == 0 ? 0 : (int)((origSize - 1) / payloadSize) + 1;

        int written = 0;
        try
        {
            while (written < data.Length)
            {
                long pos = offset + written;
                int logicalBlockIndex = (int)(pos / payloadSize);
                int blockOffset = (int)(pos % payloadSize);
                int chunk = WriteToDataBlock(headerBlock, logicalBlockIndex, blockOffset, data[written..]);
                if (chunk <= 0)
                {
                    throw new IOException($"Failed to write '{path}'.");
                }

                written += chunk;
            }

            long newEnd = offset + written;
            var header = Image.GetBlockForWrite(headerBlock);
            long currentSize = BlockReader.ReadUInt32(header, OfsBlockOffsets.FileHeader_ByteSize);
            if (newEnd > currentSize)
            {
                BlockWriter.WriteUInt32(header, OfsBlockOffsets.FileHeader_ByteSize, (uint)newEnd);
            }

            RewriteChecksum(header);
            return written;
        }
        catch
        {
            FreeDataBlocksFrom(headerBlock, origBlocksNeeded);
            var header = Image.GetBlockForWrite(headerBlock);
            BlockWriter.WriteUInt32(header, OfsBlockOffsets.FileHeader_ByteSize, (uint)origSize);
            RewriteChecksum(header);
            throw;
        }
    }

    public void SetFileSize(string path, long size)
    {
        if (size < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size));
        }

        int headerBlock = FindFileHeaderBlockOrThrow(path);
        int payloadSize = DataBlockPayloadSize;
        long currentSize = BlockReader.ReadUInt32(Image.ReadBlock(headerBlock), OfsBlockOffsets.FileHeader_ByteSize);

        if (size < currentSize)
        {
            int blocksNeeded = size == 0 ? 0 : (int)((size - 1) / payloadSize) + 1;
            FreeDataBlocksFrom(headerBlock, blocksNeeded);
            var header = Image.GetBlockForWrite(headerBlock);
            BlockWriter.WriteUInt32(header, OfsBlockOffsets.FileHeader_ByteSize, (uint)size);
            RewriteChecksum(header);
        }
        else if (size > currentSize)
        {
            long remaining = size - currentSize;
            int requiredBlocks = CalculateRequiredBlocks(headerBlock, currentSize, remaining);
            if (requiredBlocks > FreeBlockCount)
            {
                throw new DiskFullException();
            }

            int origBlocksNeeded = currentSize == 0 ? 0 : (int)((currentSize - 1) / payloadSize) + 1;
            long pos = currentSize;
            Span<byte> zeros = stackalloc byte[payloadSize];
            zeros.Clear();
            try
            {
                while (remaining > 0)
                {
                    int logicalBlockIndex = (int)(pos / payloadSize);
                    int blockOffset = (int)(pos % payloadSize);
                    int toWrite = (int)Math.Min(remaining, payloadSize - blockOffset);
                    int chunk = WriteToDataBlock(headerBlock, logicalBlockIndex, blockOffset, zeros[..toWrite]);
                    if (chunk <= 0)
                    {
                        throw new IOException($"Failed to grow '{path}'.");
                    }

                    pos += chunk;
                    remaining -= chunk;
                }

                var header = Image.GetBlockForWrite(headerBlock);
                BlockWriter.WriteUInt32(header, OfsBlockOffsets.FileHeader_ByteSize, (uint)size);
                RewriteChecksum(header);
            }
            catch
            {
                FreeDataBlocksFrom(headerBlock, origBlocksNeeded);
                var header = Image.GetBlockForWrite(headerBlock);
                BlockWriter.WriteUInt32(header, OfsBlockOffsets.FileHeader_ByteSize, (uint)currentSize);
                RewriteChecksum(header);
                throw;
            }
        }
    }

    public void Delete(string path)
    {
        var (parentBlock, name) = SplitPath(path);
        if (name is null)
        {
            throw new IOException("Cannot delete the root.");
        }

        int entryBlock = FindChildBlock(parentBlock, name)
            ?? throw new FileNotFoundException($"'{path}' was not found.", path);

        int secType = BlockReader.ReadInt32(Image.ReadBlock(entryBlock), OfsBlockOffsets.SecType);
        if (secType == SecType.UserDir)
        {
            if (EnumerateChildBlocks(entryBlock).Any())
            {
                throw new IOException($"'{path}' is not empty.");
            }
        }
        else
        {
            FreeDataBlocksFrom(entryBlock, 0);
        }

        RemoveFromDirectory(parentBlock, entryBlock, name);
        FreeBlock(entryBlock);
    }

    public void Rename(string oldPath, string newPath)
    {
        var (oldParent, oldName) = SplitPath(oldPath);
        if (oldName is null)
        {
            throw new IOException("Cannot rename the root.");
        }

        int entryBlock = FindChildBlock(oldParent, oldName)
            ?? throw new FileNotFoundException($"'{oldPath}' was not found.", oldPath);

        var (newParent, newName) = SplitPath(newPath);
        if (newName is null)
        {
            throw new IOException("Invalid destination path.");
        }

        if (!(newParent == oldParent && string.Equals(newName, oldName, StringComparison.OrdinalIgnoreCase))
            && FindChildBlock(newParent, newName) is not null)
        {
            throw new IOException($"'{newPath}' already exists.");
        }

        RemoveFromDirectory(oldParent, entryBlock, oldName);

        var block = Image.GetBlockForWrite(entryBlock);
        block.Slice(OfsBlockOffsets.FileHeader_NameLen, 1 + OfsBlockOffsets.FileHeader_NameMaxLength).Clear();
        BlockWriter.WriteBcplString(
            block, OfsBlockOffsets.FileHeader_NameLen, OfsBlockOffsets.FileHeader_Name, newName,
            OfsBlockOffsets.FileHeader_NameMaxLength);
        BlockWriter.WriteInt32(block, OfsBlockOffsets.Parent, newParent);
        RewriteChecksum(block);

        InsertIntoDirectory(newParent, entryBlock, newName);
    }

    public void SetLastWriteTime(string path, DateTime utc)
    {
        var (parentBlock, name) = SplitPath(path);
        if (name is null)
        {
            return; // root: no on-disk timestamp field to update
        }

        int entryBlock = FindChildBlock(parentBlock, name)
            ?? throw new FileNotFoundException($"'{path}' was not found.", path);

        var block = Image.GetBlockForWrite(entryBlock);
        var (days, mins, ticks) = AmigaTime.FromDateTimeUtc(utc);
        BlockWriter.WriteInt32(block, OfsBlockOffsets.FileHeader_Days, days);
        BlockWriter.WriteInt32(block, OfsBlockOffsets.FileHeader_Mins, mins);
        BlockWriter.WriteInt32(block, OfsBlockOffsets.FileHeader_Ticks, ticks);
        RewriteChecksum(block);
    }

    private int FindFileHeaderBlockOrThrow(string path)
    {
        var (parentBlock, name) = SplitPath(path);
        if (name is null)
        {
            throw new IOException("Path refers to the root, which is not a file.");
        }

        return FindChildBlock(parentBlock, name) ?? throw new FileNotFoundException($"'{path}' was not found.", path);
    }

    /// <summary>Recomputes and writes the "normal" checksum (offset 20) for any header-shaped block -
    /// root, directory, file-header, extension, or (OFS) data block. Used by format-specific data-block
    /// writers too, hence <c>protected</c>.</summary>
    protected void RewriteChecksum(Span<byte> block)
    {
        uint checksum = BlockReader.ComputeNormalChecksum(block, OfsBlockOffsets.Checksum, 128);
        BlockWriter.WriteUInt32(block, OfsBlockOffsets.Checksum, checksum);
    }

    // --- bitmap allocation ---

    private int GetBitmapBlockOrThrow()
    {
        var root = Image.ReadBlock(RootBlock);
        int bitmapBlock = BlockReader.ReadInt32(root, OfsBlockOffsets.Root_BitmapPages);
        if (bitmapBlock <= 0 || bitmapBlock >= Image.SectorCount)
        {
            throw new InvalidOperationException("This volume has no usable bitmap block; writing is not supported.");
        }

        return bitmapBlock;
    }

    private static (int WordIndex, int BitPos) BitmapIndex(int blockNumber)
    {
        // Blocks 0/1 (boot block) are never allocatable and have no bitmap bit; the map's bit 0
        // corresponds to block 2.
        int sectOfMap = blockNumber - 2;
        int wordIndex = (sectOfMap / 32) % BitmapBlockOffsets.MapWordCount;
        int bitPos = sectOfMap % 32;
        return (wordIndex, bitPos);
    }

    private static bool IsBlockFree(ReadOnlySpan<byte> bitmapBlock, int blockNumber)
    {
        var (wordIndex, bitPos) = BitmapIndex(blockNumber);
        uint word = BlockReader.ReadUInt32(bitmapBlock, BitmapBlockOffsets.Map + wordIndex * 4);
        return (word & (1u << bitPos)) != 0;
    }

    /// <summary>Allocates a free block (linear bitmap scan, marks it used, zeroes its content for
    /// hygiene), recomputing the bitmap block's checksum.</summary>
    protected int AllocateBlock()
    {
        int bitmapBlock = GetBitmapBlockOrThrow();
        var bitmap = Image.GetBlockForWrite(bitmapBlock);

        for (int b = 2; b < Image.SectorCount; b++)
        {
            if (b == bitmapBlock)
            {
                continue;
            }

            var (wordIndex, bitPos) = BitmapIndex(b);
            int wordOffset = BitmapBlockOffsets.Map + wordIndex * 4;
            uint word = BlockReader.ReadUInt32(bitmap, wordOffset);
            if ((word & (1u << bitPos)) == 0)
            {
                continue; // already used
            }

            word &= ~(1u << bitPos);
            BlockWriter.WriteUInt32(bitmap, wordOffset, word);
            RewriteBitmapChecksum(bitmap);
            BlockWriter.ZeroBlock(Image.GetBlockForWrite(b));
            return b;
        }

        throw new DiskFullException("The disk is full.");
    }

    /// <summary>Marks a block free again, recomputing the bitmap block's checksum.</summary>
    protected void FreeBlock(int blockNumber)
    {
        int bitmapBlock = GetBitmapBlockOrThrow();
        var bitmap = Image.GetBlockForWrite(bitmapBlock);
        var (wordIndex, bitPos) = BitmapIndex(blockNumber);
        int wordOffset = BitmapBlockOffsets.Map + wordIndex * 4;
        uint word = BlockReader.ReadUInt32(bitmap, wordOffset);
        word |= 1u << bitPos;
        BlockWriter.WriteUInt32(bitmap, wordOffset, word);
        RewriteBitmapChecksum(bitmap);
    }

    private static void RewriteBitmapChecksum(Span<byte> bitmapBlock)
    {
        uint checksum = BlockReader.ComputeNormalChecksum(bitmapBlock, BitmapBlockOffsets.Checksum, 128);
        BlockWriter.WriteUInt32(bitmapBlock, BitmapBlockOffsets.Checksum, checksum);
    }

    // --- directory chain mutation ---

    /// <summary>Appends <paramref name="entryBlock"/> to the tail of <paramref name="name"/>'s hash
    /// bucket within <paramref name="dirBlock"/> (or sets the bucket head, if empty) - matches
    /// ADFlib's insertion order (tail-append, not head-prepend).</summary>
    protected void InsertIntoDirectory(int dirBlock, int entryBlock, string name)
    {
        int bucket = AmigaHash.Compute(name);
        int head = ReadHashTableSlot(dirBlock, bucket);
        if (head == 0)
        {
            var dir = Image.GetBlockForWrite(dirBlock);
            BlockWriter.WriteInt32(dir, OfsBlockOffsets.HashTable + bucket * 4, entryBlock);
            RewriteChecksum(dir);
            return;
        }

        int current = head;
        int next = ReadNextSameHash(current);
        while (next != 0)
        {
            current = next;
            next = ReadNextSameHash(current);
        }

        var tail = Image.GetBlockForWrite(current);
        BlockWriter.WriteInt32(tail, OfsBlockOffsets.NextSameHash, entryBlock);
        RewriteChecksum(tail);
    }

    /// <summary>Unlinks <paramref name="entryBlock"/> from <paramref name="name"/>'s hash bucket within
    /// <paramref name="dirBlock"/> - rewrites the bucket's hash-table slot if it was the head, or the
    /// *previous sibling's* nextSameHash otherwise (getting this backwards corrupts a sibling entry,
    /// not just the one being removed).</summary>
    protected void RemoveFromDirectory(int dirBlock, int entryBlock, string name)
    {
        int bucket = AmigaHash.Compute(name);
        int current = ReadHashTableSlot(dirBlock, bucket);
        int previous = 0;

        while (current != 0 && current != entryBlock)
        {
            previous = current;
            current = ReadNextSameHash(current);
        }

        if (current != entryBlock)
        {
            throw new InvalidOperationException(
                $"'{name}' was not found in its expected hash bucket - directory structure is inconsistent.");
        }

        int nextSameHash = ReadNextSameHash(entryBlock);
        if (previous == 0)
        {
            var dir = Image.GetBlockForWrite(dirBlock);
            BlockWriter.WriteInt32(dir, OfsBlockOffsets.HashTable + bucket * 4, nextSameHash);
            RewriteChecksum(dir);
        }
        else
        {
            var prevBlock = Image.GetBlockForWrite(previous);
            BlockWriter.WriteInt32(prevBlock, OfsBlockOffsets.NextSameHash, nextSameHash);
            RewriteChecksum(prevBlock);
        }
    }

    // --- directory/name resolution ---

    private int ResolveDirectoryBlock(string path)
    {
        int block = RootBlock;
        foreach (var segment in SplitSegments(path))
        {
            int? child = FindChildBlock(block, segment);
            if (child is null)
            {
                throw new DirectoryNotFoundException($"'{segment}' was not found.");
            }

            var childBlock = Image.ReadBlock(child.Value);
            int secType = BlockReader.ReadInt32(childBlock, OfsBlockOffsets.SecType);
            if (secType != SecType.UserDir)
            {
                throw new DirectoryNotFoundException($"'{segment}' is not a directory.");
            }

            block = child.Value;
        }

        return block;
    }

    private (int ParentBlock, string? Name) SplitPath(string path)
    {
        var segments = SplitSegments(path);
        if (segments.Count == 0)
        {
            return (RootBlock, null);
        }

        int parent = ResolveDirectoryBlock(string.Join('/', segments.Take(segments.Count - 1)));
        return (parent, segments[^1]);
    }

    private static List<string> SplitSegments(string path) =>
        [.. path.Split('/', StringSplitOptions.RemoveEmptyEntries)];

    /// <summary>
    /// Finds a child of <paramref name="dirBlock"/> by name. Scans every hash bucket rather than
    /// jumping straight to <see cref="AmigaHash"/>'s bucket - simpler and robust to plausible
    /// write-tooling bugs in third-party images, and directories are always small enough (max 72
    /// buckets) for a full scan to be cheap. A block that fails checksum under the Reject policy is
    /// treated as if it doesn't exist (invisible to both this lookup and <see cref="ListDirectory"/>).
    /// </summary>
    private int? FindChildBlock(int dirBlock, string name)
    {
        foreach (int blockNum in EnumerateChildBlocks(dirBlock))
        {
            if (!IsBlockAccepted(blockNum, "directory entry"))
            {
                continue;
            }

            var block = Image.ReadBlock(blockNum);
            string entryName = BlockReader.ReadBcplString(
                block, OfsBlockOffsets.FileHeader_NameLen, OfsBlockOffsets.FileHeader_Name,
                OfsBlockOffsets.FileHeader_NameMaxLength);
            if (string.Equals(entryName, name, StringComparison.OrdinalIgnoreCase))
            {
                return blockNum;
            }
        }

        return null;
    }

    private IEnumerable<int> EnumerateChildBlocks(int dirBlock)
    {
        for (int bucket = 0; bucket < OfsBlockOffsets.HashTableCount; bucket++)
        {
            int entryBlock = ReadHashTableSlot(dirBlock, bucket);
            while (entryBlock != 0)
            {
                yield return entryBlock;
                entryBlock = ReadNextSameHash(entryBlock);
            }
        }
    }

    private int ReadHashTableSlot(int dirBlock, int bucket) =>
        BlockReader.ReadInt32(Image.ReadBlock(dirBlock), OfsBlockOffsets.HashTable + bucket * 4);

    private int ReadNextSameHash(int entryBlock) =>
        BlockReader.ReadInt32(Image.ReadBlock(entryBlock), OfsBlockOffsets.NextSameHash);

    /// <summary>Checksum-checked entry read, used by <see cref="ListDirectory"/> - returns null (and is
    /// omitted from the listing) if the block fails checksum under the Reject policy.</summary>
    private AmigaDirectoryEntry? ReadEntry(int blockNum) =>
        IsBlockAccepted(blockNum, "directory entry") ? BuildEntry(blockNum) : null;

    /// <summary>Unconditional entry read - used where the caller (<see cref="FindChildBlock"/>) has
    /// already checksum-validated the block.</summary>
    private AmigaDirectoryEntry BuildEntry(int blockNum)
    {
        var block = Image.ReadBlock(blockNum);
        string name = BlockReader.ReadBcplString(
            block, OfsBlockOffsets.FileHeader_NameLen, OfsBlockOffsets.FileHeader_Name,
            OfsBlockOffsets.FileHeader_NameMaxLength);
        string comment = BlockReader.ReadBcplString(
            block, OfsBlockOffsets.FileHeader_CommentLen, OfsBlockOffsets.FileHeader_Comment,
            OfsBlockOffsets.FileHeader_CommentMaxLength);

        int secType = BlockReader.ReadInt32(block, OfsBlockOffsets.SecType);
        bool isDirectory = secType == SecType.UserDir;
        long size = isDirectory ? 0 : BlockReader.ReadUInt32(block, OfsBlockOffsets.FileHeader_ByteSize);

        int days = BlockReader.ReadInt32(block, OfsBlockOffsets.FileHeader_Days);
        int mins = BlockReader.ReadInt32(block, OfsBlockOffsets.FileHeader_Mins);
        int ticks = BlockReader.ReadInt32(block, OfsBlockOffsets.FileHeader_Ticks);
        var timestamp = AmigaTime.ToDateTimeUtc(days, mins, ticks);

        return new AmigaDirectoryEntry(name, isDirectory, size, timestamp, comment);
    }
}
