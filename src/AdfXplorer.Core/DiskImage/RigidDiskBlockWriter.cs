using AdfXplorer.Core.FileSystems.Ofs;

namespace AdfXplorer.Core.DiskImage;

/// <summary>
/// One partition to create in a new RDB-partitioned <c>.hdf</c> image. <paramref name="DosType"/> is not
/// limited to the OFS/FFS values this project itself can read/write - an arbitrary third-party
/// filesystem's DosType (e.g. SFS/0's <c>0x53465300</c>) is valid; the partition is then created as
/// reserved-but-unformatted space (this project doesn't implement that filesystem's own on-disk
/// structures - see <see cref="RigidDiskBlockWriter"/>'s remarks). <paramref name="DriverImage"/>, when
/// given, is that filesystem's loadable driver binary (what would otherwise be copied to <c>L:</c> by
/// hand) - it's embedded via the <c>FileSysHeaderBlock</c>/<c>LoadSegBlock</c> mechanism so AmigaOS can
/// find and load it directly from the RDB, without implying this project understands that filesystem's
/// directory/file layout at all.
/// </summary>
public sealed record HdfPartitionSpec(string DriveName, int SizeMegabytes, uint DosType, byte[]? DriverImage = null);

/// <summary>
/// Creates a new RDB-partitioned <c>.hdf</c> image from scratch: an RDSK block, a chain of PART blocks
/// (one per <see cref="HdfPartitionSpec"/>), and - for any partition supplying a
/// <see cref="HdfPartitionSpec.DriverImage"/> - a <c>FileSysHeaderBlock</c>/<c>LoadSegBlock</c> chain
/// embedding that driver, linked into <c>rdb_FileSysHeaderList</c>.
///
/// Struct layout/field offsets for RDSK and PART mirror the private constants already used (and tested)
/// by <see cref="RigidDiskBlock"/>'s reader. <c>FileSysHeaderBlock</c>/<c>LoadSegBlock</c> layout was
/// confirmed by directly fetching amitools' (https://github.com/cnvogelg/amitools) maintained RDB
/// read/write implementation - see docs/rdb-hdf-creation-and-filesystem-driver-embedding.md's "RESOLVED"
/// section for the full derivation and byte tables. All four block types share the same "normal"
/// sum-and-negate checksum as the rest of this project's Amiga block formats
/// (<see cref="BlockReader.ComputeNormalChecksum"/>).
///
/// <b>Scope note:</b> only the six plain OFS/FFS DosType values are actually formatted (a real
/// boot/root/bitmap block, via <see cref="AmigaBlankVolumeWriter"/>) - any other DosType's partition is
/// left zeroed. Embedding a driver lets AmigaOS *load the handler*; it does not lay down that
/// filesystem's own on-disk structures, which this project doesn't implement.
/// </summary>
public static class RigidDiskBlockWriter
{
    private const int SectorSize = AdfImage.SectorSize;
    private const int CylinderBlocks = 32; // synthetic geometry: 1 surface, 32 blocks/track.
    private const int RdbSummedLongs = SectorSize / 4; // checksum covers the whole 512-byte block.

    // RDSK block (matches RigidDiskBlock.cs's reader offsets).
    private const int Rdsk_SummedLongs = 4;
    private const int Rdsk_ChkSum = 8;
    private const int Rdsk_HostId = 12;
    private const int Rdsk_BlockBytes = 16;
    private const int Rdsk_Flags = 20;
    private const int Rdsk_PartitionList = 28;
    private const int Rdsk_FileSysHeaderList = 32;
    private const int Rdsk_InitCode = 36;

    // PART block (matches RigidDiskBlock.cs's reader offsets).
    private const int Part_SummedLongs = 4;
    private const int Part_ChkSum = 8;
    private const int Part_HostId = 12;
    private const int Part_Next = 16;
    private const int Part_Flags = 20;
    private const int Part_DevFlags = 32;
    private const int Part_DriveNameLen = 36;
    private const int Part_DriveNameData = 37;
    private const int Part_DriveNameMaxLength = 31;
    private const int Part_Env_Size = 128 + 0 * 4;
    private const int Part_Env_BlockSize = 128 + 1 * 4;
    private const int Part_Env_Surfaces = 128 + 3 * 4;
    private const int Part_Env_BlocksPerTrack = 128 + 5 * 4;
    private const int Part_Env_Reserved = 128 + 6 * 4;
    private const int Part_Env_Interleave = 128 + 8 * 4;
    private const int Part_Env_LowCyl = 128 + 9 * 4;
    private const int Part_Env_HighCyl = 128 + 10 * 4;
    private const int Part_Env_NumBuffer = 128 + 11 * 4;
    private const int Part_Env_MaxTransfer = 128 + 13 * 4;
    private const int Part_Env_Mask = 128 + 14 * 4;
    private const int Part_Env_DosType = 128 + 16 * 4;

    // FileSysHeaderBlock (FSHD) - see docs/rdb-hdf-creation-and-filesystem-driver-embedding.md.
    private const uint Id_Fshd = 0x46534844;
    private const int Fshd_Size = 4;
    private const int Fshd_ChkSum = 8;
    private const int Fshd_HostId = 12;
    private const int Fshd_Next = 16;
    private const int Fshd_Flags = 20;
    private const int Fshd_DosType = 32;
    private const int Fshd_Version = 36;
    private const int Fshd_PatchFlags = 40;
    private const int Fshd_Dn_StackSize = 60;
    private const int Fshd_Dn_SegListBlk = 72;
    private const int Fshd_Dn_GlobalVec = 76;
    private const int Fshd_FixedSizeLongs = 64;
    private const uint Fshd_PatchFlag_SegListBlk = 0x80;

    // LoadSegBlock (LSEG).
    private const uint Id_Lseg = 0x4C534547;
    private const int Lseg_Size = 4;
    private const int Lseg_ChkSum = 8;
    private const int Lseg_HostId = 12;
    private const int Lseg_Next = 16;
    private const int Lseg_DataStart = 20;
    private const int Lseg_PayloadPerBlock = SectorSize - Lseg_DataStart; // 492 bytes.

    // 0xFFFFFFFF as a signed int (its on-disk bit pattern is identical either way).
    private const int NoBlock = -1;

    /// <summary>
    /// Lays out and writes a complete RDB image in memory: control blocks (RDSK, PART chain, optional
    /// FSHD/LSEG driver chains) first, then each partition's data area rounded up to a synthetic
    /// cylinder boundary, matching how a real Amiga RDB tool would place things.
    /// </summary>
    public static AdfImage Create(IReadOnlyList<HdfPartitionSpec> partitions)
    {
        if (partitions.Count == 0)
        {
            throw new ArgumentException("At least one partition is required.", nameof(partitions));
        }

        // Block 0: RDSK. Then one PART block per partition.
        int nextBlock = 1;
        int rdskBlock = 0;
        int firstPartBlock = nextBlock;
        var partBlocks = new int[partitions.Count];
        for (int i = 0; i < partitions.Count; i++)
        {
            partBlocks[i] = nextBlock++;
        }

        // FSHD/LSEG chains for any partition supplying a driver image.
        var fshdBlocks = new int?[partitions.Count];
        var lsegChains = new List<int>[partitions.Count];
        int firstFshdBlock = -1;
        int previousFshdBlock = -1;

        for (int i = 0; i < partitions.Count; i++)
        {
            lsegChains[i] = [];
            byte[]? driver = partitions[i].DriverImage;
            if (driver is null || driver.Length == 0)
            {
                continue;
            }

            int chunkCount = (driver.Length + Lseg_PayloadPerBlock - 1) / Lseg_PayloadPerBlock;
            for (int c = 0; c < chunkCount; c++)
            {
                lsegChains[i].Add(nextBlock++);
            }

            int fshdBlock = nextBlock++;
            fshdBlocks[i] = fshdBlock;
            if (firstFshdBlock < 0)
            {
                firstFshdBlock = fshdBlock;
            }

            previousFshdBlock = fshdBlock;
        }

        // Reserve the control-block area up to the next cylinder boundary; partitions start there.
        long controlBlocks = RoundUpToCylinder(nextBlock);
        long firstPartitionStart = controlBlocks;

        var partitionStarts = new long[partitions.Count];
        var partitionBlockCounts = new long[partitions.Count];
        long cursor = firstPartitionStart;
        for (int i = 0; i < partitions.Count; i++)
        {
            var spec = partitions[i];
            if (spec.SizeMegabytes <= 0)
            {
                throw new ArgumentException($"Partition '{spec.DriveName}' must have a positive size.", nameof(partitions));
            }

            long sizeBytes = (long)spec.SizeMegabytes * 1024 * 1024;
            long sectorCount = (sizeBytes + SectorSize - 1) / SectorSize;
            if (IsBuiltInOfsOrFfs(spec.DosType, out _) && sectorCount > AdfXplorer.Core.FileSystems.AmigaHashDirectoryFileSystem.MaxSupportedSectors)
            {
                throw new ArgumentException(
                    $"Partition '{spec.DriveName}' ({spec.SizeMegabytes:N0} MB) exceeds the 4 GiB limit (4,096 MB) for OFS/FFS filesystems.",
                    nameof(partitions));
            }

            long blocks = RoundUpToCylinder(sectorCount);

            partitionStarts[i] = cursor;
            partitionBlockCounts[i] = blocks;
            cursor += blocks;
        }

        long totalBlocks = cursor;
        var image = AdfImage.CreateEmpty(totalBlocks);

        WriteRdsk(image, rdskBlock, firstPartBlock, firstFshdBlock);

        for (int i = 0; i < partitions.Count; i++)
        {
            int next = i + 1 < partitions.Count ? partBlocks[i + 1] : -1;
            WritePart(
                image, partBlocks[i], next, partitions[i], partitionStarts[i], partitionBlockCounts[i]);
        }

        for (int i = 0; i < partitions.Count; i++)
        {
            if (fshdBlocks[i] is not int fshdBlock)
            {
                continue;
            }

            int nextFshd = FindNextFshd(fshdBlocks, i);
            WriteFshdAndDriverChain(image, fshdBlock, lsegChains[i], nextFshd, partitions[i]);
        }

        for (int i = 0; i < partitions.Count; i++)
        {
            var spec = partitions[i];
            if (IsBuiltInOfsOrFfs(spec.DosType, out byte bootFlags))
            {
                AmigaBlankVolumeWriter.WriteBlankInto(
                    image, partitionStarts[i], (int)partitionBlockCounts[i], spec.DriveName, bootFlags);
            }
            // Any other DosType: reserved, unformatted (zeroed) space - see class remarks.
        }

        return image;
    }

    private static int FindNextFshd(int?[] fshdBlocks, int fromIndexExclusive)
    {
        for (int j = fromIndexExclusive + 1; j < fshdBlocks.Length; j++)
        {
            if (fshdBlocks[j] is int b)
            {
                return b;
            }
        }

        return -1;
    }

    private static bool IsBuiltInOfsOrFfs(uint dosType, out byte bootFlags)
    {
        const uint Dos0 = 0x444F5300;
        if (dosType >= Dos0 && dosType <= Dos0 + 5)
        {
            bootFlags = (byte)(dosType - Dos0);
            return true;
        }

        bootFlags = 0;
        return false;
    }

    private static long RoundUpToCylinder(long blocks) =>
        ((blocks + CylinderBlocks - 1) / CylinderBlocks) * CylinderBlocks;

    private static void WriteRdsk(AdfImage image, int rdskBlock, int firstPartBlock, int firstFshdBlock)
    {
        var block = image.GetBlockForWrite(rdskBlock);
        WriteSignature(block, 'R', 'D', 'S', 'K');
        BlockWriter.WriteInt32(block, Rdsk_SummedLongs, RdbSummedLongs);
        BlockWriter.WriteInt32(block, Rdsk_HostId, 7); // SCSI host ID 7 - the conventional default, unused by this in-memory image
        BlockWriter.WriteInt32(block, Rdsk_BlockBytes, SectorSize);
        BlockWriter.WriteInt32(block, Rdsk_Flags, 0x17); // rdb_Flags: disk park/spinup/etc bits AmigaOS checks - 0x17 is amitools' standard default
        BlockWriter.WriteInt32(block, Rdsk_PartitionList, firstPartBlock);
        BlockWriter.WriteInt32(block, Rdsk_FileSysHeaderList, firstFshdBlock < 0 ? NoBlock : firstFshdBlock);
        BlockWriter.WriteInt32(block, Rdsk_InitCode, NoBlock);

        WriteChecksum(block, Rdsk_ChkSum);
    }

    private static void WritePart(
        AdfImage image, int blockNum, int next, HdfPartitionSpec spec, long startBlock, long blockCount)
    {
        var block = image.GetBlockForWrite(blockNum);
        WriteSignature(block, 'P', 'A', 'R', 'T');
        BlockWriter.WriteInt32(block, Part_SummedLongs, RdbSummedLongs);
        BlockWriter.WriteInt32(block, Part_HostId, 7);
        BlockWriter.WriteInt32(block, Part_Next, next < 0 ? NoBlock : next);
        BlockWriter.WriteInt32(block, Part_Flags, 0);
        BlockWriter.WriteInt32(block, Part_DevFlags, 0);
        BlockWriter.WriteBcplString(
            block, Part_DriveNameLen, Part_DriveNameData, spec.DriveName, Part_DriveNameMaxLength);

        long cylinderBlocks = CylinderBlocks;
        long lowCyl = startBlock / cylinderBlocks;
        long highCyl = startBlock / cylinderBlocks + blockCount / cylinderBlocks - 1;

        BlockWriter.WriteInt32(block, Part_Env_Size, 16); // SIZE_DEFAULT_ENV
        BlockWriter.WriteInt32(block, Part_Env_BlockSize, 128); // longs per block
        BlockWriter.WriteInt32(block, Part_Env_Surfaces, 1);
        BlockWriter.WriteInt32(block, Part_Env_BlocksPerTrack, (int)cylinderBlocks);
        BlockWriter.WriteInt32(block, Part_Env_Reserved, 2);
        BlockWriter.WriteInt32(block, Part_Env_Interleave, 0);
        BlockWriter.WriteInt32(block, Part_Env_LowCyl, (int)lowCyl);
        BlockWriter.WriteInt32(block, Part_Env_HighCyl, (int)highCyl);
        BlockWriter.WriteInt32(block, Part_Env_NumBuffer, 30);
        BlockWriter.WriteInt32(block, Part_Env_MaxTransfer, unchecked((int)0xFFFFFF));
        BlockWriter.WriteInt32(block, Part_Env_Mask, unchecked((int)0x7FFFFFFE));
        BlockWriter.WriteUInt32(block, Part_Env_DosType, spec.DosType);

        WriteChecksum(block, Part_ChkSum);
    }

    private static void WriteFshdAndDriverChain(
        AdfImage image, int fshdBlock, List<int> lsegBlocks, int nextFshdBlock, HdfPartitionSpec spec)
    {
        byte[] driver = spec.DriverImage!;

        for (int c = 0; c < lsegBlocks.Count; c++)
        {
            int start = c * Lseg_PayloadPerBlock;
            int rawLen = Math.Min(Lseg_PayloadPerBlock, driver.Length - start);
            int paddedLen = ((rawLen + 3) / 4) * 4; // hunk-format executables are always longword-sized.

            int blockNum = lsegBlocks[c];
            var lseg = image.GetBlockForWrite(blockNum);
            WriteSignature(lseg, 'L', 'S', 'E', 'G');
            BlockWriter.WriteInt32(lseg, Lseg_Size, (Lseg_DataStart + paddedLen) / 4);
            BlockWriter.WriteInt32(lseg, Lseg_HostId, 0);

            int next = c + 1 < lsegBlocks.Count ? lsegBlocks[c + 1] : NoBlock;
            BlockWriter.WriteInt32(lseg, Lseg_Next, next);

            driver.AsSpan(start, rawLen).CopyTo(lseg.Slice(Lseg_DataStart, rawLen));

            WriteChecksum(lseg, Lseg_ChkSum);
        }

        var fshd = image.GetBlockForWrite(fshdBlock);
        WriteSignature(fshd, 'F', 'S', 'H', 'D');
        BlockWriter.WriteInt32(fshd, Fshd_Size, Fshd_FixedSizeLongs);
        BlockWriter.WriteInt32(fshd, Fshd_HostId, 0);
        BlockWriter.WriteInt32(fshd, Fshd_Next, nextFshdBlock < 0 ? NoBlock : nextFshdBlock);
        BlockWriter.WriteInt32(fshd, Fshd_Flags, 0);
        BlockWriter.WriteUInt32(fshd, Fshd_DosType, spec.DosType);
        BlockWriter.WriteInt32(fshd, Fshd_Version, 0);
        BlockWriter.WriteUInt32(fshd, Fshd_PatchFlags, Fshd_PatchFlag_SegListBlk);
        BlockWriter.WriteInt32(fshd, Fshd_Dn_StackSize, 0x2000);
        BlockWriter.WriteInt32(fshd, Fshd_Dn_SegListBlk, lsegBlocks[0]);
        BlockWriter.WriteInt32(fshd, Fshd_Dn_GlobalVec, NoBlock);

        WriteChecksum(fshd, Fshd_ChkSum);
    }

    private static void WriteSignature(Span<byte> block, char a, char b, char c, char d)
    {
        block[0] = (byte)a;
        block[1] = (byte)b;
        block[2] = (byte)c;
        block[3] = (byte)d;
    }

    private static void WriteChecksum(Span<byte> block, int checksumFieldOffset)
    {
        uint checksum = BlockReader.ComputeNormalChecksum(block, checksumFieldOffset, RdbSummedLongs);
        BlockWriter.WriteUInt32(block, checksumFieldOffset, checksum);
    }
}
