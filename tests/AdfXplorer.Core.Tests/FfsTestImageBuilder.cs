using System.Buffers.Binary;
using System.Text;

namespace AdfXplorer.Core.Tests;

/// <summary>
/// Builds a minimal, valid, synthetic FFS-formatted .adf-shaped image in memory: boot block (flags=1),
/// root block, one small file (single data block, no extension needed), and one large file spanning
/// more than 72 data blocks (forcing one extension block) - so unit tests don't need a real Amiga disk
/// image as a fixture and specifically exercise extension-block chaining.
///
/// Mirrors <see cref="TestImageBuilder"/>'s approach (chains children off hash bucket 0, since
/// <c>OfsFileSystem</c>/<c>FfsFileSystem</c> both scan every bucket rather than relying on hash
/// placement) but with FFS's data-block-table storage instead of OFS's next_data chain.
/// </summary>
internal static class FfsTestImageBuilder
{
    private const int SectorSize = 512;
    private const int TotalSectors = 1760;
    private const int SlotsPerTable = 72;

    private const int RootBlock = 900;
    private const int SmallFileHeaderBlock = 901;
    private const int SmallFileDataBlock = 902;
    private const int BigFileHeaderBlock = 903;
    private const int BigFileFirstDataBlock = 904;
    private const int BigFileDataBlockCount = 100; // > 72, forces one extension block (28 more slots)
    private const int BigFileExtensionBlock = BigFileFirstDataBlock + BigFileDataBlockCount; // 1004

    public const string SmallFileName = "SMALL.TXT";
    public const string BigFileName = "BIG.BIN";

    /// <summary>Builds the image described in the class summary.</summary>
    /// <param name="smallFileContent">Receives the plaintext content of the single-block file.</param>
    /// <param name="bigFileContent">Receives the byte-pattern-filled content of the extension-block file.</param>
    public static byte[] Build(out string smallFileContent, out byte[] bigFileContent)
    {
        smallFileContent = "Hello from FFS!";

        bigFileContent = new byte[BigFileDataBlockCount * SectorSize];
        for (int i = 0; i < BigFileDataBlockCount; i++)
        {
            byte fill = (byte)(i % 256);
            Array.Fill(bigFileContent, fill, i * SectorSize, SectorSize);
        }

        var image = new byte[TotalSectors * SectorSize];

        image[0] = (byte)'D';
        image[1] = (byte)'O';
        image[2] = (byte)'S';
        image[3] = 1; // FFS, no international mode, no directory cache
        WriteInt32(image, 8, RootBlock);

        WriteRootBlock(image, RootBlock, "FfsTestDisk", firstChild: SmallFileHeaderBlock);

        WriteFileHeaderBlock(
            image, SmallFileHeaderBlock, SmallFileName, byteSize: smallFileContent.Length,
            dataBlocks: [SmallFileDataBlock], extension: 0, nextSameHash: BigFileHeaderBlock);
        WriteRawDataBlock(image, SmallFileDataBlock, Encoding.Latin1.GetBytes(smallFileContent));

        var firstTableBlocks = Enumerable.Range(BigFileFirstDataBlock, SlotsPerTable).ToArray();
        var extensionTableBlocks = Enumerable
            .Range(BigFileFirstDataBlock + SlotsPerTable, BigFileDataBlockCount - SlotsPerTable)
            .ToArray();

        WriteFileHeaderBlock(
            image, BigFileHeaderBlock, BigFileName, byteSize: bigFileContent.Length,
            dataBlocks: firstTableBlocks, extension: BigFileExtensionBlock, nextSameHash: 0);
        WriteExtensionBlock(image, BigFileExtensionBlock, dataBlocks: extensionTableBlocks, extension: 0);

        for (int i = 0; i < BigFileDataBlockCount; i++)
        {
            WriteRawDataBlock(
                image, BigFileFirstDataBlock + i, bigFileContent.AsSpan(i * SectorSize, SectorSize).ToArray());
        }

        ChecksumTestHelper.WriteBootChecksum(image);
        ChecksumTestHelper.WriteNormalChecksum(image, RootBlock);
        ChecksumTestHelper.WriteNormalChecksum(image, SmallFileHeaderBlock);
        ChecksumTestHelper.WriteNormalChecksum(image, BigFileHeaderBlock);
        ChecksumTestHelper.WriteNormalChecksum(image, BigFileExtensionBlock);
        // Raw FFS data blocks get no checksum - there is no field to write.

        return image;
    }

    /// <summary>Writes an FFS root block (T_HEADER/ST_ROOT) with a single hash-bucket-0 child chain.</summary>
    private static void WriteRootBlock(byte[] image, int block, string name, int firstChild)
    {
        int off = block * SectorSize;
        WriteInt32(image, off + 0, 2); // type = T_HEADER
        WriteInt32(image, off + 12, 72); // ht_size
        WriteInt32(image, off + 312, -1); // bm_flag: valid
        WriteInt32(image, off + 24, firstChild); // hash bucket 0
        WriteBcplString(image, off + 432, name, 30);
        WriteInt32(image, off + 508, 1); // sec_type = ST_ROOT
    }

    /// <summary>Writes a file header (or, via <see cref="WriteExtensionBlock"/>, an extension) block's
    /// 72-slot data-block table, right-aligned per FFS's fill convention (slot 71 = first block).</summary>
    private static void WriteDataBlockTable(byte[] image, int blockOffset, IReadOnlyList<int> dataBlocks)
    {
        WriteInt32(image, blockOffset + 8, dataBlocks.Count); // high_seq
        for (int i = 0; i < dataBlocks.Count; i++)
        {
            int slot = SlotsPerTable - 1 - i;
            WriteInt32(image, blockOffset + 24 + slot * 4, dataBlocks[i]);
        }
    }

    /// <summary>Writes an FFS file-header block (T_HEADER/ST_FILE) with its data-block table and, if the
    /// file needs more than <see cref="SlotsPerTable"/> data blocks, a link to an extension block.</summary>
    private static void WriteFileHeaderBlock(
        byte[] image, int block, string name, int byteSize, IReadOnlyList<int> dataBlocks, int extension,
        int nextSameHash)
    {
        int off = block * SectorSize;
        WriteInt32(image, off + 0, 2); // type = T_HEADER
        WriteDataBlockTable(image, off, dataBlocks);
        WriteInt32(image, off + 324, byteSize);
        WriteBcplString(image, off + 432, name, 30);
        WriteInt32(image, off + 496, nextSameHash);
        WriteInt32(image, off + 504, extension);
        WriteInt32(image, off + 508, -3); // sec_type = ST_FILE
    }

    /// <summary>Writes a T_LIST file-extension block continuing a file's data-block table beyond the
    /// header block's <see cref="SlotsPerTable"/> slots.</summary>
    private static void WriteExtensionBlock(byte[] image, int block, IReadOnlyList<int> dataBlocks, int extension)
    {
        int off = block * SectorSize;
        WriteInt32(image, off + 0, 16); // type = T_LIST
        WriteDataBlockTable(image, off, dataBlocks);
        WriteInt32(image, off + 504, extension);
        WriteInt32(image, off + 508, -3); // sec_type = ST_FILE
    }

    private static void WriteRawDataBlock(byte[] image, int block, byte[] content)
    {
        int off = block * SectorSize;
        content.CopyTo(image, off);
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
