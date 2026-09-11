using System.Buffers.Binary;

namespace AdfXplorer.Core.DiskImage;

/// <summary>
/// A raw Amiga Disk File (.adf/.hdf) image: a flat sequence of fixed-size 512-byte sectors ("blocks").
/// This class only knows about linear block access - it has no notion of any Amiga filesystem format.
/// Supports both reading and writing blocks in-memory; call <see cref="SaveTo"/> to persist changes.
/// </summary>
public sealed class AdfImage
{
    /// <summary>Every Amiga/AmigaDOS block format this project reads or writes is a 512-byte sector - fixed for all drive types (floppy and RDB hard disk alike).</summary>
    public const int SectorSize = 512;

    private readonly byte[] _data;
    private readonly int _startBlock;
    private readonly int _sectorCount;

    /// <summary>Wraps a whole-image byte buffer for block access. The buffer is used directly (not copied), so writes to it outside this class are visible here too.</summary>
    public AdfImage(byte[] data)
    {
        if (data.Length == 0 || data.Length % SectorSize != 0)
        {
            throw new ArgumentException(
                $"Image size {data.Length} is not a positive multiple of the {SectorSize}-byte sector size.",
                nameof(data));
        }

        _data = data;
        _startBlock = 0;
        _sectorCount = data.Length / SectorSize;
    }

    private AdfImage(byte[] data, int startBlock, int sectorCount)
    {
        _data = data;
        _startBlock = startBlock;
        _sectorCount = sectorCount;
    }

    /// <summary>Reads an entire image file into memory up front - these images are small enough (floppy/hard-disk sized) that streaming isn't worth the complexity.</summary>
    public static AdfImage FromFile(string path) => new(File.ReadAllBytes(path));

    /// <summary>Number of blocks visible through this image or window - not necessarily the whole underlying file when this is a partition window (see <see cref="CreateWindow"/>).</summary>
    public int SectorCount => _sectorCount;

    /// <summary>Size of the visible region in bytes; for a partition window this is the partition's size, not the whole disk's.</summary>
    public long SizeInBytes => (long)_sectorCount * SectorSize;

    /// <summary>Reads one block, addressed relative to this image/window's own block 0 (not the underlying file's, when this is a window).</summary>
    public ReadOnlySpan<byte> ReadBlock(int blockNumber)
    {
        if ((uint)blockNumber >= (uint)_sectorCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(blockNumber), blockNumber, $"Block must be in [0, {_sectorCount}).");
        }

        int absoluteBlock = _startBlock + blockNumber;
        return _data.AsSpan(absoluteBlock * SectorSize, SectorSize);
    }

    /// <summary>
    /// A read-only "window" over blocks [<paramref name="startBlock"/>, <paramref name="startBlock"/> +
    /// <paramref name="blockCount"/>) of this image, renumbered so the window's own block 0 maps to
    /// this image's <paramref name="startBlock"/>. Lets an RDB partition (see
    /// <see cref="RigidDiskBlock"/>) be mounted through the exact same pipeline as a standalone
    /// .adf/.hdf, since a partition is structurally just its own self-contained image from its first
    /// block onward.
    /// </summary>
    public AdfImage CreateWindow(int startBlock, int blockCount)
    {
        if (startBlock < 0 || blockCount <= 0 || (long)startBlock + blockCount > _sectorCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(startBlock), $"Window [{startBlock}, {startBlock + blockCount}) is out of range [0, {_sectorCount}).");
        }

        return new AdfImage(_data, _startBlock + startBlock, blockCount);
    }

    /// <summary>
    /// A mutable view of <paramref name="blockNumber"/>'s bytes, backed directly by this image's
    /// in-memory buffer - the one general write path everything else (bitmap, directory, file data
    /// mutation) goes through. Does not touch disk; call <see cref="SaveTo"/> to persist. A window
    /// (see <see cref="CreateWindow"/>) and its parent share the same backing array, so a write through
    /// either is visible through the other, and <see cref="SaveTo"/> on the top-level image always
    /// captures writes made through any of its windows too.
    /// </summary>
    public Span<byte> GetBlockForWrite(int blockNumber)
    {
        if ((uint)blockNumber >= (uint)_sectorCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(blockNumber), blockNumber, $"Block must be in [0, {_sectorCount}).");
        }

        int absoluteBlock = _startBlock + blockNumber;
        return _data.AsSpan(absoluteBlock * SectorSize, SectorSize);
    }

    /// <summary>
    /// Overwrites the 4-byte big-endian field at <paramref name="fieldOffset"/> within
    /// <paramref name="blockNumber"/>. Does not touch disk; call <see cref="SaveTo"/> to persist.
    /// </summary>
    public void PatchChecksumField(int blockNumber, int fieldOffset, uint value) =>
        BinaryPrimitives.WriteUInt32BigEndian(GetBlockForWrite(blockNumber).Slice(fieldOffset, 4), value);

    /// <summary>Writes this image's current in-memory bytes to <paramref name="path"/>, overwriting it.</summary>
    public void SaveTo(string path) => File.WriteAllBytes(path, _data);
}
