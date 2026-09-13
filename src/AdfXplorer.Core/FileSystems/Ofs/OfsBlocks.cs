using System.Buffers.Binary;
using System.Text;
using AdfXplorer.Core.DiskImage;

namespace AdfXplorer.Core.FileSystems.Ofs;

/// <summary>
/// Byte offsets into the 512-byte OFS/root/directory/file-header/data blocks.
///
/// These offsets are taken directly from ADFlib's canonical on-disk struct definitions
/// (AdfRootBlock / AdfEntryBlock / AdfFileHeaderBlock / AdfOFSDataBlock), the reference C
/// implementation for reading/writing Amiga Disk Format images:
/// https://github.com/adflib/ADFlib/blob/master/src/adf_blk.h
///
/// Root and directory ("entry") blocks share the same layout for the fields used here (both have
/// a 72-entry hash table at offset 24, and the same name/date/hash-chain/parent/sec-type tail), so
/// one set of "Header_*" offsets below covers both, plus the FileHeader-specific fields.
/// </summary>
internal static class OfsBlockOffsets
{
    // Common to every T_HEADER-type block (root, directory/"entry", file header) and the OFS data
    // block - all of them place their checksum field at byte offset 20.
    public const int Type = 0;
    public const int Checksum = 20;
    public const int SecType = 508;

    // Boot block (blocks 0-1, 1024 bytes together): its checksum field offset differs from every
    // other block type, and is verified with a different algorithm - see OfsChecksum.
    public const int Boot_Checksum = 4;

    // Root/directory ("entry") block: 72-entry hash table of child block pointers.
    public const int HashTable = 24;
    public const int HashTableCount = AmigaHash.HashTableSize;
    public const int NextSameHash = 496;
    public const int Parent = 500;

    /// <summary>
    /// File header/extension ("list") block only: pointer to the next extension block (0 = last),
    /// used by FFS files needing more than 72 data-block slots. Same offset in both block kinds since
    /// an extension block is struct-identical to a file header block (ADFlib's <c>AdfFileExtBlock</c>).
    /// </summary>
    public const int Extension = 504;

    // Root block only.
    public const int Root_HashTableSize = 12; // should read 72
    public const int Root_BootBlockRootPointer = 8;
    public const int Root_BitmapFlag = 312; // -1 = valid
    public const int Root_BitmapPages = 316; // 25 longs; [0] is the one bitmap block this project writes
    public const int Root_BitmapPagesCount = 25;
    public const int Root_BitmapExtension = 416; // non-zero = a bitmap-extension chain is in use
    public const int Root_NameLen = 432;
    public const int Root_Name = 433;
    public const int Root_NameMaxLength = 30;

    // File header block (and directory/"entry" block, which shares the same tail layout).
    /// <summary>
    /// File header/extension block only: OFS leaves this 0 (unused); FFS uses it as the count of
    /// populated slots in *this* block's 72-slot data-block table (see <see cref="HashTable"/>, reused
    /// as the data-block-number array for FFS files/extensions - same offset 24 either way).
    /// </summary>
    public const int FileHeader_HighSeq = 8;
    public const int FileHeader_FirstData = 16;
    public const int FileHeader_ByteSize = 324;
    public const int FileHeader_CommentLen = 328;
    public const int FileHeader_Comment = 329;
    public const int FileHeader_CommentMaxLength = 91; // structural upper bound (329..420)
    public const int FileHeader_Days = 420;
    public const int FileHeader_Mins = 424;
    public const int FileHeader_Ticks = 428;
    public const int FileHeader_NameLen = 432;
    public const int FileHeader_Name = 433;
    public const int FileHeader_NameMaxLength = 30;

    // OFS data block.
    public const int Data_HeaderKey = 4;
    public const int Data_SeqNum = 8;
    public const int Data_DataSize = 12;
    public const int Data_NextData = 16;
    public const int Data_Payload = 24;
    public const int Data_PayloadMaxSize = 488;
}

/// <summary>
/// Locates an AmigaDOS root block from the boot block. HDToolbox-formatted hard-disk partitions may
/// leave the boot block's root pointer zero; the filesystem's conventional root position is then the
/// midpoint of its partition.
/// </summary>
internal static class AmigaDosRootBlock
{
    public static bool TryFind(AdfImage image, out int rootBlockNumber)
    {
        rootBlockNumber = BlockReader.ReadInt32(image.ReadBlock(0), OfsBlockOffsets.Root_BootBlockRootPointer);
        if (rootBlockNumber == 0)
        {
            rootBlockNumber = (int)(image.SectorCount / 2);
        }

        if (rootBlockNumber <= 0 || rootBlockNumber >= image.SectorCount)
        {
            return false;
        }

        var root = image.ReadBlock(rootBlockNumber);
        return BlockReader.ReadInt32(root, OfsBlockOffsets.Type) == BlockType.Header
            && BlockReader.ReadInt32(root, OfsBlockOffsets.SecType) == SecType.Root;
    }
}

internal static class BlockType
{
    public const int Header = 2;
    public const int Data = 8;
    public const int List = 16; // FFS extension block
}

internal static class SecType
{
    public const int Root = 1;
    public const int UserDir = 2;
    public const int File = -3;
}

/// <summary>Layout of the one bitmap block this project reads/writes (ADFlib's <c>AdfBitmapBlock</c>):
/// a checksum at offset 0 followed by a 127-long <c>map[]</c> (bit 1 = free, bit 0 = used, block number
/// <c>B</c> (B &gt;= 2) at <c>map[((B-2)/32) % 127]</c> bit <c>(B-2) % 32</c>, LSB-first).</summary>
internal static class BitmapBlockOffsets
{
    public const int Checksum = 0;
    public const int Map = 4;
    public const int MapWordCount = 127;
}

internal static class BlockReader
{
    public static int ReadInt32(ReadOnlySpan<byte> block, int offset) =>
        BinaryPrimitives.ReadInt32BigEndian(block.Slice(offset, 4));

    public static uint ReadUInt32(ReadOnlySpan<byte> block, int offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(block.Slice(offset, 4));

    /// <summary>
    /// Reads an AmigaDOS BCPL-style string: a one-byte length prefix followed by that many raw bytes.
    /// </summary>
    public static string ReadBcplString(ReadOnlySpan<byte> block, int lengthOffset, int dataOffset, int maxLength)
    {
        int len = block[lengthOffset];
        if (len > maxLength)
        {
            len = maxLength;
        }

        return Encoding.Latin1.GetString(block.Slice(dataOffset, len));
    }

    /// <summary>
    /// The Amiga "normal" block checksum (ADFlib's <c>adfNormalSum</c>): sum
    /// <paramref name="wordCount"/> big-endian 32-bit words starting at the block's start (clamped to
    /// the block's actual length), skipping the word at <paramref name="checksumFieldOffset"/>, then
    /// negate. A valid block's stored checksum equals this computed value.
    ///
    /// Shared by RigidDiskBlock/PartitionBlock (wordCount = their own SummedLongs field) and OFS root/
    /// directory/file-header/data blocks (wordCount always 128, offset always 20).
    /// </summary>
    public static uint ComputeNormalChecksum(ReadOnlySpan<byte> block, int checksumFieldOffset, int wordCount)
    {
        int clampedWords = Math.Clamp(wordCount, 1, block.Length / 4);
        uint sum = 0;
        for (int i = 0; i < clampedWords; i++)
        {
            int byteOffset = i * 4;
            if (byteOffset == checksumFieldOffset)
            {
                continue;
            }

            sum += ReadUInt32(block, byteOffset);
        }

        return unchecked((uint)-(int)sum);
    }
}

/// <summary>The write-side mirror of <see cref="BlockReader"/>: same span-based idiom, mutable.</summary>
internal static class BlockWriter
{
    public static void WriteInt32(Span<byte> block, int offset, int value) =>
        BinaryPrimitives.WriteInt32BigEndian(block.Slice(offset, 4), value);

    public static void WriteUInt32(Span<byte> block, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32BigEndian(block.Slice(offset, 4), value);

    /// <summary>
    /// Writes an AmigaDOS BCPL-style string: a one-byte length prefix followed by the raw bytes,
    /// truncated to <paramref name="maxLength"/>. Does not clear any previously-longer value's leftover
    /// trailing bytes beyond the new length - callers writing into a freshly-zeroed block (the normal
    /// case for a new entry) don't need to; a rename onto a shorter name should zero the block's name
    /// field first if reusing an existing block.
    /// </summary>
    public static void WriteBcplString(Span<byte> block, int lengthOffset, int dataOffset, string value, int maxLength)
    {
        var bytes = Encoding.Latin1.GetBytes(value);
        int len = Math.Min(bytes.Length, maxLength);
        block[lengthOffset] = (byte)len;
        bytes.AsSpan(0, len).CopyTo(block.Slice(dataOffset, len));
    }

    public static void ZeroBlock(Span<byte> block) => block.Clear();
}
