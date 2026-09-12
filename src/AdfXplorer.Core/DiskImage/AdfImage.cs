using System.Buffers.Binary;

namespace AdfXplorer.Core.DiskImage;

/// <summary>
/// A raw Amiga Disk File (.adf/.hdf) image: a flat sequence of fixed-size 512-byte sectors ("blocks").
/// This class only knows about linear block access - it has no notion of any Amiga filesystem format.
/// Created images use an in-memory buffer; existing images are read one sector at a time from the source
/// file, retaining only sectors modified through <see cref="GetBlockForWrite"/>.
/// </summary>
public sealed class AdfImage : IDisposable
{
    private sealed class FileBacking : IDisposable
    {
        public FileBacking(string path)
        {
            Path = System.IO.Path.GetFullPath(path);
            Stream = new FileStream(Path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        }

        public string Path { get; }
        public FileStream Stream { get; }
        public Dictionary<int, byte[]> ModifiedBlocks { get; } = [];
        public object SyncRoot { get; } = new();

        public void Dispose() => Stream.Dispose();
    }

    /// <summary>Every Amiga/AmigaDOS block format this project reads or writes is a 512-byte sector - fixed for all drive types (floppy and RDB hard disk alike).</summary>
    public const int SectorSize = 512;

    private readonly byte[]? _data;
    private readonly FileBacking? _fileBacking;
    private readonly bool _ownsFileBacking;
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

    private AdfImage(FileBacking fileBacking, int startBlock, int sectorCount, bool ownsFileBacking)
    {
        _fileBacking = fileBacking;
        _startBlock = startBlock;
        _sectorCount = sectorCount;
        _ownsFileBacking = ownsFileBacking;
    }

    /// <summary>
    /// Opens an image for streamed sector access. Reads allocate only a single 512-byte result buffer;
    /// writes are cached per changed sector and flushed by <see cref="SaveTo"/>.
    /// </summary>
    public static AdfImage FromFile(string path)
    {
        var fileInfo = new FileInfo(path);
        if (fileInfo.Length == 0 || fileInfo.Length % SectorSize != 0)
        {
            throw new ArgumentException(
                $"Image size {fileInfo.Length} is not a positive multiple of the {SectorSize}-byte sector size.",
                nameof(path));
        }

        long sectorCount = fileInfo.Length / SectorSize;
        if (sectorCount > int.MaxValue)
        {
            throw new ArgumentException(
                $"Image has {sectorCount:N0} sectors, exceeding the {int.MaxValue:N0}-sector limit.",
                nameof(path));
        }

        return new AdfImage(new FileBacking(path), startBlock: 0, (int)sectorCount, ownsFileBacking: true);
    }

    /// <summary>Number of blocks visible through this image or window - not necessarily the whole underlying file when this is a partition window (see <see cref="CreateWindow"/>).</summary>
    public int SectorCount => _sectorCount;

    /// <summary>Size of the visible region in bytes; for a partition window this is the partition's size, not the whole disk's.</summary>
    public long SizeInBytes => (long)_sectorCount * SectorSize;

    /// <summary>Reads one complete block into an isolated buffer, addressed relative to this image/window's own block 0.</summary>
    public byte[] ReadBlock(int blockNumber)
    {
        ValidateBlockNumber(blockNumber);
        int absoluteBlock = _startBlock + blockNumber;
        if (_data is not null)
        {
            return _data.AsSpan(absoluteBlock * SectorSize, SectorSize).ToArray();
        }

        lock (_fileBacking!.SyncRoot)
        {
            if (_fileBacking.ModifiedBlocks.TryGetValue(absoluteBlock, out var modified))
            {
                return modified;
            }

            var block = new byte[SectorSize];
            _fileBacking.Stream.Position = (long)absoluteBlock * SectorSize;
            _fileBacking.Stream.ReadExactly(block);
            return block;
        }
    }

    /// <summary>
    /// A read-only "window" over blocks [<paramref name="startBlock"/>, <paramref name="startBlock"/> +
    /// <paramref name="blockCount"/>) of this image, renumbered so the window's own block 0 maps to
    /// this image's <paramref name="startBlock"/>.
    /// </summary>
    public AdfImage CreateWindow(int startBlock, int blockCount)
    {
        if (startBlock < 0 || blockCount <= 0 || (long)startBlock + blockCount > _sectorCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(startBlock), $"Window [{startBlock}, {startBlock + blockCount}) is out of range [0, {_sectorCount}).");
        }

        int absoluteStartBlock = _startBlock + startBlock;
        return _data is not null
            ? new AdfImage(_data, absoluteStartBlock, blockCount)
            : new AdfImage(_fileBacking!, absoluteStartBlock, blockCount, ownsFileBacking: false);
    }

    /// <summary>
    /// A mutable view of one block. For file-backed images, the block enters the write cache before its
    /// span is returned, ensuring subsequent reads observe changes and <see cref="SaveTo"/> persists it.
    /// </summary>
    public Span<byte> GetBlockForWrite(int blockNumber)
    {
        ValidateBlockNumber(blockNumber);
        int absoluteBlock = _startBlock + blockNumber;
        if (_data is not null)
        {
            return _data.AsSpan(absoluteBlock * SectorSize, SectorSize);
        }

        lock (_fileBacking!.SyncRoot)
        {
            if (!_fileBacking.ModifiedBlocks.TryGetValue(absoluteBlock, out var block))
            {
                block = new byte[SectorSize];
                _fileBacking.Stream.Position = (long)absoluteBlock * SectorSize;
                _fileBacking.Stream.ReadExactly(block);
                _fileBacking.ModifiedBlocks.Add(absoluteBlock, block);
            }

            return block;
        }
    }

    /// <summary>
    /// Overwrites the 4-byte big-endian field at <paramref name="fieldOffset"/> within
    /// <paramref name="blockNumber"/>. Does not touch disk; call <see cref="SaveTo"/> to persist.
    /// </summary>
    public void PatchChecksumField(int blockNumber, int fieldOffset, uint value) =>
        BinaryPrimitives.WriteUInt32BigEndian(GetBlockForWrite(blockNumber).Slice(fieldOffset, 4), value);

    /// <summary>Writes this image's current bytes to <paramref name="path"/>.</summary>
    public void SaveTo(string path)
    {
        if (_data is not null)
        {
            File.WriteAllBytes(path, _data);
            return;
        }

        if (!Path.GetFullPath(path).Equals(_fileBacking!.Path, StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("A file-backed image can only be saved to its source path.");
        }

        lock (_fileBacking.SyncRoot)
        {
            foreach (var (blockNumber, block) in _fileBacking.ModifiedBlocks)
            {
                _fileBacking.Stream.Position = (long)blockNumber * SectorSize;
                _fileBacking.Stream.Write(block);
            }

            _fileBacking.Stream.Flush(flushToDisk: true);
            _fileBacking.ModifiedBlocks.Clear();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsFileBacking)
        {
            _fileBacking!.Dispose();
        }
    }

    private void ValidateBlockNumber(int blockNumber)
    {
        if ((uint)blockNumber >= (uint)_sectorCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(blockNumber), blockNumber, $"Block must be in [0, {_sectorCount}).");
        }
    }
}
