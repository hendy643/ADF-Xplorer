using System.Buffers.Binary;
using System.Text;

namespace AdfXplorer.Core.Tests;

/// <summary>
/// Builds a minimal, valid, synthetic RDB-partitioned .hdf image in memory: an RDSK block, a
/// 2-partition chain, and (at each partition's own block offset) a minimal OFS filesystem with one
/// uniquely-named file - so unit tests don't need a real hard disk image as a fixture.
///
/// Separate from <see cref="TestImageBuilder"/> (which stays as-is for the non-RDB floppy case) so
/// neither can accidentally affect the other's passing tests.
/// </summary>
internal static class RdbTestImageBuilder
{
    private const int SectorSize = 512;

    // A tiny synthetic geometry: 1 surface, 32 blocks/track, so "cylinders" are small and easy to reason
    // about. Not a realistic disk geometry - just enough to exercise de_LowCyl/de_HighCyl arithmetic.
    private const int Surfaces = 1;
    private const int BlocksPerTrack = 32;
    private const int CylinderBlocks = Surfaces * BlocksPerTrack;

    private const int RdskBlock = 0;
    private const int Partition0Block = 1;
    private const int Partition1Block = 2;

    // Partition 0: cylinders [1, 10) -> blocks [32, 320)
    private const int Partition0LowCyl = 1;
    private const int Partition0HighCyl = 9;

    // Partition 1: cylinders [10, 20) -> blocks [320, 640)
    private const int Partition1LowCyl = 10;
    private const int Partition1HighCyl = 19;

    public const string Partition0DriveName = "DH0";
    public const string Partition1DriveName = "DH1";
    public const string Partition0FileName = "PART0FILE.TXT";
    public const string Partition1FileName = "PART1FILE.TXT";

    public const int RdskBlockNumber = RdskBlock;
    public const int Partition0BlockNumber = Partition0Block;
    public const int Partition1BlockNumber = Partition1Block;

    // Relative to each partition's own base block (returned by Build's out params).
    public const int OfsRootBlockOffset = 20;
    public const int OfsFileHeaderBlockOffset = 21;
    public const int OfsFileDataBlockOffset = 22;

    /// <summary>Builds the RDB image described in the class summary.</summary>
    /// <param name="partition0StartBlock">Receives partition 0's absolute starting block number.</param>
    /// <param name="partition0BlockCount">Receives partition 0's block count.</param>
    /// <param name="partition1StartBlock">Receives partition 1's absolute starting block number.</param>
    /// <param name="partition1BlockCount">Receives partition 1's block count.</param>
    /// <param name="partition0Content">Receives the plaintext content of partition 0's file.</param>
    /// <param name="partition1Content">Receives the plaintext content of partition 1's file.</param>
    public static byte[] Build(
        out int partition0StartBlock, out int partition0BlockCount,
        out int partition1StartBlock, out int partition1BlockCount,
        out string partition0Content, out string partition1Content)
    {
        partition0StartBlock = Partition0LowCyl * CylinderBlocks;
        partition0BlockCount = (Partition0HighCyl - Partition0LowCyl + 1) * CylinderBlocks;
        partition1StartBlock = Partition1LowCyl * CylinderBlocks;
        partition1BlockCount = (Partition1HighCyl - Partition1LowCyl + 1) * CylinderBlocks;
        partition0Content = "Hello from partition zero!";
        partition1Content = "Greetings from partition one.";

        int totalBlocks = partition1StartBlock + partition1BlockCount;
        var image = new byte[totalBlocks * SectorSize];

        WriteRdskBlock(image);
        WritePartitionBlock(
            image, Partition0Block, next: Partition1Block, Partition0DriveName, Partition0LowCyl, Partition0HighCyl);
        WritePartitionBlock(
            image, Partition1Block, next: -1, Partition1DriveName, Partition1LowCyl, Partition1HighCyl);

        WriteOfsFilesystem(image, partition0StartBlock, "PartitionZero", Partition0FileName, partition0Content);
        WriteOfsFilesystem(image, partition1StartBlock, "PartitionOne", Partition1FileName, partition1Content);

        ChecksumTestHelper.WriteRdbChecksum(image, RdskBlock, RdbSummedLongs);
        ChecksumTestHelper.WriteRdbChecksum(image, Partition0Block, RdbSummedLongs);
        ChecksumTestHelper.WriteRdbChecksum(image, Partition1Block, RdbSummedLongs);

        return image;
    }

    // Covers the whole 512-byte block, matching how real RDB tools typically set it.
    private const int RdbSummedLongs = SectorSize / 4;

    /// <summary>Writes the RDSK block whose <c>rdb_PartitionList</c> heads the partition-block chain.</summary>
    private static void WriteRdskBlock(byte[] image)
    {
        WriteSignature(image, RdskBlock * SectorSize, 'R', 'D', 'S', 'K');
        WriteInt32(image, RdskBlock * SectorSize + 4, RdbSummedLongs); // rdb_SummedLongs
        WriteInt32(image, RdskBlock * SectorSize + 16, SectorSize); // rdb_BlockBytes
        WriteInt32(image, RdskBlock * SectorSize + 28, Partition0Block); // rdb_PartitionList
    }

    /// <summary>Writes a PART block with just the geometry fields RigidDiskBlock needs to derive a
    /// partition's start block and length (de_Surfaces/de_BlocksPerTrack/de_LowCyl/de_HighCyl).</summary>
    private static void WritePartitionBlock(
        byte[] image, int block, int next, string driveName, int lowCyl, int highCyl)
    {
        int off = block * SectorSize;
        WriteSignature(image, off, 'P', 'A', 'R', 'T');
        WriteInt32(image, off + 4, RdbSummedLongs); // pb_SummedLongs
        WriteInt32(image, off + 16, next); // pb_Next
        WriteBcplString(image, off + 36, driveName, 31); // pb_DriveName

        int envOff = off + 128;
        WriteInt32(image, envOff + 3 * 4, Surfaces); // de_Surfaces
        WriteInt32(image, envOff + 5 * 4, BlocksPerTrack); // de_BlocksPerTrack
        WriteInt32(image, envOff + 9 * 4, lowCyl); // de_LowCyl
        WriteInt32(image, envOff + 10 * 4, highCyl); // de_HighCyl
    }

    /// <summary>
    /// Writes a minimal OFS filesystem (boot block + root block + one file, no subdirectories) whose
    /// own block 0 is at <paramref name="baseBlock"/> within <paramref name="image"/> - mirroring how a
    /// real RDB partition's filesystem starts fresh at its own first block.
    /// </summary>
    private static void WriteOfsFilesystem(byte[] image, int baseBlock, string volumeLabel, string fileName, string content)
    {
        const int RootBlock = 20;
        const int FileHeaderBlock = 21;
        const int FileDataBlock = 22;

        int bootOff = baseBlock * SectorSize;
        image[bootOff + 0] = (byte)'D';
        image[bootOff + 1] = (byte)'O';
        image[bootOff + 2] = (byte)'S';
        image[bootOff + 3] = 0;
        WriteInt32(image, bootOff + 8, RootBlock);

        int rootOff = (baseBlock + RootBlock) * SectorSize;
        WriteInt32(image, rootOff + 0, 2); // type = T_HEADER
        WriteInt32(image, rootOff + 12, 72); // ht_size
        WriteInt32(image, rootOff + 312, -1); // bm_flag: valid
        WriteInt32(image, rootOff + 24, FileHeaderBlock); // hash bucket 0
        WriteBcplString(image, rootOff + 432, volumeLabel, 30);
        WriteInt32(image, rootOff + 508, 1); // sec_type = ST_ROOT

        int fileOff = (baseBlock + FileHeaderBlock) * SectorSize;
        WriteInt32(image, fileOff + 0, 2); // type = T_HEADER
        WriteInt32(image, fileOff + 16, FileDataBlock); // first_data
        WriteInt32(image, fileOff + 324, content.Length); // byte_size
        WriteBcplString(image, fileOff + 432, fileName, 30);
        WriteInt32(image, fileOff + 500, RootBlock); // parent
        WriteInt32(image, fileOff + 508, -3); // sec_type = ST_FILE

        int dataOff = (baseBlock + FileDataBlock) * SectorSize;
        var bytes = Encoding.Latin1.GetBytes(content);
        WriteInt32(image, dataOff + 0, 8); // type = T_DATA
        WriteInt32(image, dataOff + 4, FileHeaderBlock); // header_key
        WriteInt32(image, dataOff + 8, 1); // seq_num
        WriteInt32(image, dataOff + 12, bytes.Length); // data_size
        WriteInt32(image, dataOff + 16, 0); // next_data
        bytes.CopyTo(image, dataOff + 24);

        ChecksumTestHelper.WriteBootChecksum(image, baseBlock);
        ChecksumTestHelper.WriteNormalChecksum(image, baseBlock + RootBlock);
        ChecksumTestHelper.WriteNormalChecksum(image, baseBlock + FileHeaderBlock);
        ChecksumTestHelper.WriteNormalChecksum(image, baseBlock + FileDataBlock);
    }

    private static void WriteSignature(byte[] image, int offset, char a, char b, char c, char d)
    {
        image[offset] = (byte)a;
        image[offset + 1] = (byte)b;
        image[offset + 2] = (byte)c;
        image[offset + 3] = (byte)d;
    }

    private static void WriteInt32(byte[] image, int offset, int value) =>
        BinaryPrimitives.WriteInt32BigEndian(image.AsSpan(offset, 4), value);

    private static void WriteBcplString(byte[] image, int offset, string value, int maxLen)
    {
        var bytes = Encoding.Latin1.GetBytes(value);
        int len = Math.Min(bytes.Length, maxLen);
        image[offset] = (byte)len;
        Array.Copy(bytes, 0, image, offset + 1, len);
    }
}
