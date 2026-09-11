using System.Buffers.Binary;
using System.Text;

namespace AdfXplorer.Core.Tests;

/// <summary>
/// Builds a minimal, valid, synthetic OFS-formatted .adf image in memory, so unit tests don't need a
/// real (and likely copyrighted) Amiga disk image as a fixture. Block offsets mirror
/// AdfXplorer.Core.FileSystems.Ofs.OfsBlockOffsets (in turn taken from ADFlib's adf_blk.h - see that
/// file's header comment for the source reference).
///
/// Every directory's children are chained off hash bucket 0 rather than their "real" AmigaDOS hash
/// bucket: OfsFileSystem always scans every bucket, so placement doesn't matter for reading, and this
/// keeps the builder simple.
/// </summary>
internal static class TestImageBuilder
{
    private const int SectorSize = 512;
    private const int TotalSectors = 1760; // 880 KB DD floppy
    private const int RootBlock = 880;

    private const int FileHeaderBlock = 881;
    private const int FileDataBlock = 882;
    private const int SubDirBlock = 883;
    private const int NestedHeaderBlock = 884;
    private const int NestedDataBlock = 885;

    public const string FileName = "HELLO.TXT";
    public const string SubDirName = "SUBDIR";
    public const string NestedFileName = "NESTED.TXT";

    /// <summary>
    /// Builds an 880 KB DD-floppy-sized OFS image containing a root with one file and one subdirectory
    /// (which itself contains one nested file), all block checksums valid.
    /// </summary>
    /// <param name="volumeLabel">Volume name written into the root block's BCPL disk-name field.</param>
    /// <param name="fileContent">Receives the plaintext content written into the root-level file.</param>
    /// <param name="nestedFileContent">Receives the plaintext content written into the subdirectory's file.</param>
    public static byte[] BuildMinimalOfsImage(string volumeLabel, out string fileContent, out string nestedFileContent)
    {
        fileContent = "Hello, Amiga!";
        nestedFileContent = "Nested content from a subdirectory.";

        var image = new byte[TotalSectors * SectorSize];

        image[0] = (byte)'D';
        image[1] = (byte)'O';
        image[2] = (byte)'S';
        image[3] = 0; // OFS, no international mode, no directory cache
        WriteInt32(image, 8, RootBlock);

        WriteRootBlock(image, RootBlock, volumeLabel, firstChild: FileHeaderBlock);
        WriteFileHeaderBlock(
            image, FileHeaderBlock, FileName, parent: RootBlock, firstData: FileDataBlock,
            byteSize: fileContent.Length, nextSameHash: SubDirBlock);
        WriteDataBlock(image, FileDataBlock, FileHeaderBlock, fileContent, nextData: 0);

        WriteDirectoryBlock(image, SubDirBlock, SubDirName, parent: RootBlock, firstChild: NestedHeaderBlock, nextSameHash: 0);
        WriteFileHeaderBlock(
            image, NestedHeaderBlock, NestedFileName, parent: SubDirBlock, firstData: NestedDataBlock,
            byteSize: nestedFileContent.Length, nextSameHash: 0);
        WriteDataBlock(image, NestedDataBlock, NestedHeaderBlock, nestedFileContent, nextData: 0);

        ChecksumTestHelper.WriteBootChecksum(image);
        ChecksumTestHelper.WriteNormalChecksum(image, RootBlock);
        ChecksumTestHelper.WriteNormalChecksum(image, FileHeaderBlock);
        ChecksumTestHelper.WriteNormalChecksum(image, FileDataBlock);
        ChecksumTestHelper.WriteNormalChecksum(image, SubDirBlock);
        ChecksumTestHelper.WriteNormalChecksum(image, NestedHeaderBlock);
        ChecksumTestHelper.WriteNormalChecksum(image, NestedDataBlock);

        return image;
    }

    public static int RootBlockNumber => RootBlock;
    public static int FileHeaderBlockNumber => FileHeaderBlock;
    public static int FileDataBlockNumber => FileDataBlock;

    /// <summary>Writes an OFS root block (T_HEADER/ST_ROOT) with a single hash-bucket-0 child chain.</summary>
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

    /// <summary>Writes an OFS user-directory block (T_HEADER/ST_USERDIR) linked to its parent.</summary>
    private static void WriteDirectoryBlock(byte[] image, int block, string name, int parent, int firstChild, int nextSameHash)
    {
        int off = block * SectorSize;
        WriteInt32(image, off + 0, 2); // type = T_HEADER
        WriteInt32(image, off + 24, firstChild); // hash bucket 0
        WriteBcplString(image, off + 432, name, 30);
        WriteInt32(image, off + 496, nextSameHash);
        WriteInt32(image, off + 500, parent);
        WriteInt32(image, off + 508, 2); // sec_type = ST_USERDIR
    }

    /// <summary>Writes an OFS file-header block (T_HEADER/ST_FILE) pointing at its first data block.</summary>
    private static void WriteFileHeaderBlock(
        byte[] image, int block, string name, int parent, int firstData, int byteSize, int nextSameHash)
    {
        int off = block * SectorSize;
        WriteInt32(image, off + 0, 2); // type = T_HEADER
        WriteInt32(image, off + 16, firstData);
        WriteInt32(image, off + 324, byteSize);
        WriteBcplString(image, off + 432, name, 30);
        WriteInt32(image, off + 496, nextSameHash);
        WriteInt32(image, off + 500, parent);
        WriteInt32(image, off + 508, -3); // sec_type = ST_FILE
    }

    /// <summary>Writes an OFS data block (T_DATA), single-block only (<paramref name="nextData"/> = 0 means end of chain).</summary>
    private static void WriteDataBlock(byte[] image, int block, int headerKey, string content, int nextData)
    {
        int off = block * SectorSize;
        var bytes = Encoding.Latin1.GetBytes(content);
        WriteInt32(image, off + 0, 8); // type = T_DATA
        WriteInt32(image, off + 4, headerKey);
        WriteInt32(image, off + 8, 1); // seq_num
        WriteInt32(image, off + 12, bytes.Length); // data_size
        WriteInt32(image, off + 16, nextData);
        bytes.CopyTo(image, off + 24);
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
