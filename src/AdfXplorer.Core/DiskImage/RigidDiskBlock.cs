using AdfXplorer.Core.FileSystems;
using AdfXplorer.Core.FileSystems.Ofs;

namespace AdfXplorer.Core.DiskImage;

/// <summary>
/// One partition entry from an Amiga Rigid Disk Block (RDB) partition table, in absolute blocks of the
/// image it was read from. <see cref="AdfImage.CreateWindow"/> turns this into a standalone image that
/// can be fed straight into <see cref="FileSystems.AmigaFileSystemRegistry.Mount"/> - a partition is,
/// from its own first block onward, structurally identical to a top-level .adf/.hdf.
/// </summary>
public sealed record RdbPartition(string DriveName, int StartBlock, int BlockCount);

/// <summary>
/// Reads the partition table from an Amiga hard disk image (the common real-world WinUAE ".hdf" case).
/// Not every .hdf has one - a "plain hardfile" is just one filesystem spanning the whole image, exactly
/// like a big .adf, and is handled by the existing single-filesystem path when
/// <see cref="TryReadPartitions"/> returns null.
///
/// Struct layout and field offsets are taken from two independently-checked sources that agree
/// field-for-field: the Linux kernel's own RDB parser
/// (https://github.com/torvalds/linux/blob/master/include/uapi/linux/affs_hardblocks.h)
/// and https://en.wikipedia.org/wiki/Amiga_rigid_disk_block. See also
/// docs/windows-explorer-plugin-research.md §5.2 for the derivation. The checksum algorithm (shared by
/// RDSK and PART blocks) is ADFlib's <c>adfNormalSum</c> - see <see cref="BlockReader.ComputeNormalChecksum"/>.
/// </summary>
public static class RigidDiskBlock
{
    /// <summary>RDB_ALLOCATION_LIMIT: the RDSK block may be anywhere in the first 16 blocks.</summary>
    private const int SearchLimit = 16;

    private const int Rdsk_BlockBytes = 16;
    private const int Rdsk_PartitionList = 28;

    // Shared by both RDSK and PART blocks - both structs start identically (ID, SummedLongs, ChkSum, ...).
    private const int Rdb_SummedLongs = 4;
    private const int Rdb_ChkSum = 8;

    private const int Part_Next = 16;
    private const int Part_DriveNameLen = 36;
    private const int Part_DriveNameData = 37;
    private const int Part_DriveNameMaxLength = 31;

    private const int Rdsk_FileSysHeaderList = 32;

    // FileSysHeaderBlock (FSHD) / LoadSegBlock (LSEG) - see RigidDiskBlockWriter's remarks and
    // docs/rdb-hdf-creation-and-filesystem-driver-embedding.md for the source of these offsets.
    private const int Fshd_Next = 16;
    private const int Fshd_Dn_SegListBlk = 72;
    private const int Lseg_Next = 16;

    // pb_Environment[17] (the DosEnvec) starts at offset 128; each entry is a 4-byte big-endian long.
    private const int Part_Env_Surfaces = 128 + 3 * 4;
    private const int Part_Env_BlocksPerTrack = 128 + 5 * 4;
    private const int Part_Env_LowCyl = 128 + 9 * 4;
    private const int Part_Env_HighCyl = 128 + 10 * 4;

    /// <summary>
    /// Returns this image's RDB partitions, or <c>null</c> if no RDB signature is found in the first
    /// <see cref="SearchLimit"/> blocks (i.e. this isn't a partitioned hard disk image). A checksum
    /// mismatch is resolved via <paramref name="onChecksumMismatch"/> (or defaults to
    /// <see cref="ChecksumDecision.Ignore"/> if <c>null</c> - today's existing lenient behavior). The
    /// RDSK block being <see cref="ChecksumDecision.Reject"/>ed returns <c>(null, false)</c>, exactly
    /// the existing "no RDB found" fallback. A PART block being rejected omits just that partition.
    /// </summary>
    public static (IReadOnlyList<RdbPartition>? Partitions, bool Repaired) TryReadPartitions(
        AdfImage image, ChecksumConflictHandler? onChecksumMismatch = null)
    {
        int rdbBlockNumber = FindRdskBlock(image);
        if (rdbBlockNumber < 0)
        {
            return (null, false);
        }

        bool repaired = false;
        if (!ResolveBlock(image, rdbBlockNumber, $"RDSK block {rdbBlockNumber}", onChecksumMismatch, ref repaired))
        {
            return (null, repaired);
        }

        var rdb = image.ReadBlock(rdbBlockNumber); // re-read: a Repair may have patched it
        int blockBytes = (int)BlockReader.ReadUInt32(rdb, Rdsk_BlockBytes);
        if (blockBytes != AdfImage.SectorSize)
        {
            // Unsupported sector size for this reader; bail out rather than misinterpret block numbers.
            return (null, repaired);
        }

        var partitions = new List<RdbPartition>();
        var visited = new HashSet<int>();
        int next = BlockReader.ReadInt32(rdb, Rdsk_PartitionList);

        while (next > 0 && next < image.SectorCount && visited.Add(next))
        {
            var part = image.ReadBlock(next);
            if (!HasSignature(part, 'P', 'A', 'R', 'T'))
            {
                break;
            }

            string driveName = BlockReader.ReadBcplString(
                part, Part_DriveNameLen, Part_DriveNameData, Part_DriveNameMaxLength);
            int nextBlock = BlockReader.ReadInt32(part, Part_Next);

            if (ResolveBlock(image, next, $"PART block {next} ('{driveName}')", onChecksumMismatch, ref repaired))
            {
                part = image.ReadBlock(next); // re-read: a Repair may have patched it
                int surfaces = BlockReader.ReadInt32(part, Part_Env_Surfaces);
                int blocksPerTrack = BlockReader.ReadInt32(part, Part_Env_BlocksPerTrack);
                int lowCyl = BlockReader.ReadInt32(part, Part_Env_LowCyl);
                int highCyl = BlockReader.ReadInt32(part, Part_Env_HighCyl);

                // RDB partitions are defined in CHS terms (cylinder range + geometry), not a block range -
                // convert by treating each cylinder as a fixed-size run of blocks (surfaces * blocksPerTrack).
                int cylinderBlocks = surfaces * blocksPerTrack;
                int startBlock = lowCyl * cylinderBlocks;
                int blockCount = (highCyl - lowCyl + 1) * cylinderBlocks; // highCyl is inclusive

                if (cylinderBlocks > 0 && startBlock >= 0 && blockCount > 0
                    && (long)startBlock + blockCount <= image.SectorCount)
                {
                    partitions.Add(new RdbPartition(driveName, startBlock, blockCount));
                }
            }

            next = nextBlock;
        }

        return (partitions, repaired);
    }

    /// <summary>
    /// Read-only deep scan: reports the checksum status of the RDSK block and every PART block in the
    /// chain, without mounting anything or modifying the image. Used by the "Validate"/"Repair"
    /// context-menu verbs. Yields nothing if no RDB is found.
    /// </summary>
    public static IEnumerable<ChecksumReport> ScanChecksums(AdfImage image)
    {
        int rdbBlockNumber = FindRdskBlock(image);
        if (rdbBlockNumber < 0)
        {
            yield break;
        }

        var rdsk = ReadRdskSummary(image, rdbBlockNumber);
        yield return new ChecksumReport(
            $"RDSK block {rdbBlockNumber}", rdbBlockNumber, Rdb_ChkSum,
            rdsk.Stored == rdsk.Computed, rdsk.Stored, rdsk.Computed);

        if (rdsk.BlockBytes != AdfImage.SectorSize)
        {
            yield break;
        }

        var visited = new HashSet<int>();
        int next = rdsk.PartitionList;

        while (next > 0 && next < image.SectorCount && visited.Add(next))
        {
            if (!HasPartSignature(image, next))
            {
                break;
            }

            var part = ReadPartSummary(image, next);
            yield return new ChecksumReport(
                $"PART block {next} ('{part.DriveName}')", next, Rdb_ChkSum,
                part.Stored == part.Computed, part.Stored, part.Computed);

            next = part.Next;
        }

        int fshdNext = rdsk.FileSysHeaderList;
        var visitedFshd = new HashSet<int>();

        while (fshdNext > 0 && fshdNext < image.SectorCount && visitedFshd.Add(fshdNext))
        {
            if (!HasSignature(image.ReadBlock(fshdNext), 'F', 'S', 'H', 'D'))
            {
                break;
            }

            var fshd = ReadFshdSummary(image, fshdNext);
            yield return new ChecksumReport(
                $"FSHD block {fshdNext}", fshdNext, Rdb_ChkSum, fshd.Stored == fshd.Computed, fshd.Stored, fshd.Computed);

            int lsegNext = fshd.SegListBlk;
            var visitedLseg = new HashSet<int>();
            while (lsegNext > 0 && lsegNext < image.SectorCount && visitedLseg.Add(lsegNext))
            {
                if (!HasSignature(image.ReadBlock(lsegNext), 'L', 'S', 'E', 'G'))
                {
                    break;
                }

                var lseg = ReadLsegSummary(image, lsegNext);
                yield return new ChecksumReport(
                    $"LSEG block {lsegNext}", lsegNext, Rdb_ChkSum, lseg.Stored == lseg.Computed, lseg.Stored, lseg.Computed);

                lsegNext = lseg.Next;
            }

            fshdNext = fshd.Next;
        }
    }

    private readonly record struct FshdSummary(uint Stored, uint Computed, int Next, int SegListBlk);

    private static FshdSummary ReadFshdSummary(AdfImage image, int blockNumber)
    {
        var block = image.ReadBlock(blockNumber);
        var (stored, computed) = ComputeStatus(block);
        int next = BlockReader.ReadInt32(block, Fshd_Next);
        int segListBlk = BlockReader.ReadInt32(block, Fshd_Dn_SegListBlk);
        return new FshdSummary(stored, computed, next, segListBlk);
    }

    private readonly record struct LsegSummary(uint Stored, uint Computed, int Next);

    private static LsegSummary ReadLsegSummary(AdfImage image, int blockNumber)
    {
        var block = image.ReadBlock(blockNumber);
        var (stored, computed) = ComputeStatus(block);
        int next = BlockReader.ReadInt32(block, Lseg_Next);
        return new LsegSummary(stored, computed, next);
    }

    private readonly record struct RdskSummary(uint Stored, uint Computed, int BlockBytes, int PartitionList, int FileSysHeaderList);

    private static RdskSummary ReadRdskSummary(AdfImage image, int blockNumber)
    {
        var block = image.ReadBlock(blockNumber);
        var (stored, computed) = ComputeStatus(block);
        int blockBytes = (int)BlockReader.ReadUInt32(block, Rdsk_BlockBytes);
        int partitionList = BlockReader.ReadInt32(block, Rdsk_PartitionList);
        int fileSysHeaderList = BlockReader.ReadInt32(block, Rdsk_FileSysHeaderList);
        return new RdskSummary(stored, computed, blockBytes, partitionList, fileSysHeaderList);
    }

    private readonly record struct PartSummary(string DriveName, uint Stored, uint Computed, int Next);

    private static PartSummary ReadPartSummary(AdfImage image, int blockNumber)
    {
        var block = image.ReadBlock(blockNumber);
        string driveName = BlockReader.ReadBcplString(
            block, Part_DriveNameLen, Part_DriveNameData, Part_DriveNameMaxLength);
        var (stored, computed) = ComputeStatus(block);
        int next = BlockReader.ReadInt32(block, Part_Next);
        return new PartSummary(driveName, stored, computed, next);
    }

    private static bool HasPartSignature(AdfImage image, int blockNumber) =>
        HasSignature(image.ReadBlock(blockNumber), 'P', 'A', 'R', 'T');

    /// <summary>Applies a repair reported by <see cref="ScanChecksums"/>. Does not persist to disk.</summary>
    public static void RepairChecksum(AdfImage image, ChecksumReport report) =>
        image.PatchChecksumField(report.BlockNumber, report.ChecksumFieldOffset, report.Computed);

    /// <summary>
    /// Validates one RDSK/PART block's checksum, consulting <paramref name="onChecksumMismatch"/> (or
    /// defaulting to <see cref="ChecksumDecision.Ignore"/>) on a mismatch. Returns whether the block
    /// should be trusted/used (false only for <see cref="ChecksumDecision.Reject"/>); sets
    /// <paramref name="repaired"/> to true if a repair was applied.
    /// </summary>
    private static bool ResolveBlock(
        AdfImage image, int blockNumber, string description, ChecksumConflictHandler? onChecksumMismatch,
        ref bool repaired)
    {
        var block = image.ReadBlock(blockNumber);
        var (stored, computed) = ComputeStatus(block);
        if (stored == computed)
        {
            return true;
        }

        var decision = onChecksumMismatch?.Invoke(description, stored, computed) ?? ChecksumDecision.Ignore;
        switch (decision)
        {
            case ChecksumDecision.Repair:
                image.PatchChecksumField(blockNumber, Rdb_ChkSum, computed);
                repaired = true;
                return true;
            case ChecksumDecision.Reject:
                return false;
            default:
                return true;
        }
    }

    private static (uint Stored, uint Computed) ComputeStatus(ReadOnlySpan<byte> block)
    {
        int summedLongs = BlockReader.ReadInt32(block, Rdb_SummedLongs);
        uint stored = BlockReader.ReadUInt32(block, Rdb_ChkSum);
        uint computed = BlockReader.ComputeNormalChecksum(block, Rdb_ChkSum, summedLongs);
        return (stored, computed);
    }

    private static int FindRdskBlock(AdfImage image)
    {
        int limit = Math.Min(SearchLimit, image.SectorCount);
        for (int b = 0; b < limit; b++)
        {
            if (HasSignature(image.ReadBlock(b), 'R', 'D', 'S', 'K'))
            {
                return b;
            }
        }

        return -1;
    }

    private static bool HasSignature(ReadOnlySpan<byte> block, char a, char b, char c, char d) =>
        block.Length >= 4 && block[0] == (byte)a && block[1] == (byte)b && block[2] == (byte)c && block[3] == (byte)d;
}
