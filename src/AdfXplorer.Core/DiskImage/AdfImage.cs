using System.Buffers.Binary;
using System.IO.Compression;

namespace AdfXplorer.Core.DiskImage;

/// <summary>
/// A raw Amiga Disk File (.adf/.hdf) image: a flat sequence of fixed-size 512-byte sectors ("blocks").
/// This class acts as a neutral block storage container with linear sector access, independent
/// of any specific Amiga filesystem format or capacity limits.
/// Created images use an in-memory buffer; existing images are read one sector at a time from the source
/// file, retaining only sectors modified through <see cref="GetBlockForWrite"/>.
/// </summary>
public sealed class AdfImage : IDisposable
{
    private sealed class MemoryBacking
    {
        public Dictionary<long, byte[]> Blocks { get; } = [];
        public object SyncRoot { get; } = new();
    }

    private sealed class FileBacking : IDisposable
    {
        public FileBacking(string path)
        {
            Path = System.IO.Path.GetFullPath(path);
            Stream = new FileStream(Path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        }

        public string Path { get; }
        public FileStream Stream { get; }
        public Dictionary<long, byte[]> ModifiedBlocks { get; } = [];
        public object SyncRoot { get; } = new();

        public void Dispose() => Stream.Dispose();
    }

    /// <summary>Every Amiga/AmigaDOS block format this project reads or writes is a 512-byte sector - fixed for all drive types (floppy and RDB hard disk alike).</summary>
    public const int SectorSize = 512;

    private readonly byte[]? _data;
    private readonly MemoryBacking? _memoryBacking;
    private readonly FileBacking? _fileBacking;
    private readonly bool _ownsFileBacking;
    private readonly long _startBlock;
    private readonly long _sectorCount;
    private Dictionary<long, byte[]>? _journal;

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
        _sectorCount = (long)data.Length / SectorSize;
    }

    private AdfImage(byte[] data, long startBlock, long sectorCount)
    {
        _data = data;
        _startBlock = startBlock;
        _sectorCount = sectorCount;
    }

    private AdfImage(MemoryBacking memoryBacking, long startBlock, long sectorCount)
    {
        _memoryBacking = memoryBacking;
        _startBlock = startBlock;
        _sectorCount = sectorCount;
    }

    private AdfImage(FileBacking fileBacking, long startBlock, long sectorCount, bool ownsFileBacking)
    {
        _fileBacking = fileBacking;
        _startBlock = startBlock;
        _sectorCount = sectorCount;
        _ownsFileBacking = ownsFileBacking;
    }

    /// <summary>
    /// Creates a blank in-memory image for the given number of sectors.
    /// Blocks are allocated lazily as written, keeping memory usage minimal regardless of sector count.
    /// </summary>
    public static AdfImage CreateEmpty(long sectorCount)
    {
        if (sectorCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sectorCount), sectorCount, "Sector count must be positive.");
        }

        return new AdfImage(new MemoryBacking(), startBlock: 0, sectorCount);
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
        return new AdfImage(new FileBacking(path), startBlock: 0, sectorCount, ownsFileBacking: true);
    }

    /// <summary>
    /// Loads an image from a gzip-compressed source (e.g. a ".hdz" file - an ordinary .hdf, gzipped).
    /// Unlike <see cref="FromFile"/>, the whole image is decompressed into memory up front rather than
    /// streamed block-by-block, because a <see cref="GZipStream"/> isn't seekable. The result uses the
    /// same in-memory backing as <see cref="AdfImage(byte[])"/> - callers must treat it as read-only,
    /// since <see cref="SaveTo"/> on that backing writes raw (non-gzipped) bytes and would silently
    /// corrupt the original .hdz if ever invoked against its path.
    /// </summary>
    public static AdfImage FromGzipFile(string path)
    {
        using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var gzip = new GZipStream(fileStream, CompressionMode.Decompress);
        using var buffer = new MemoryStream();
        gzip.CopyTo(buffer);
        return new AdfImage(buffer.ToArray());
    }

    /// <summary>Number of blocks visible through this image or window - not necessarily the whole underlying file when this is a partition window (see <see cref="CreateWindow"/>).</summary>
    public long SectorCount => _sectorCount;

    /// <summary>Size of the visible region in bytes; for a partition window this is the partition's size, not the whole disk's.</summary>
    public long SizeInBytes => _sectorCount * SectorSize;

    /// <summary>Reads one complete block into an isolated buffer, addressed relative to this image/window's own block 0.</summary>
    public byte[] ReadBlock(long blockNumber)
    {
        ValidateBlockNumber(blockNumber);
        return ReadAbsoluteBlock(_startBlock + blockNumber);
    }

    private byte[] ReadAbsoluteBlock(long absoluteBlock)
    {
        if (_data is not null)
        {
            return _data.AsSpan((int)absoluteBlock * SectorSize, SectorSize).ToArray();
        }

        if (_fileBacking is not null)
        {
            lock (_fileBacking.SyncRoot)
            {
                if (_fileBacking.ModifiedBlocks.TryGetValue(absoluteBlock, out var modified))
                {
                    return (byte[])modified.Clone();
                }

                var block = new byte[SectorSize];
                _fileBacking.Stream.Position = absoluteBlock * SectorSize;
                _fileBacking.Stream.ReadExactly(block);
                return block;
            }
        }

        lock (_memoryBacking!.SyncRoot)
        {
            if (_memoryBacking.Blocks.TryGetValue(absoluteBlock, out var block))
            {
                return (byte[])block.Clone();
            }

            return new byte[SectorSize];
        }
    }

    /// <summary>
    /// A read-only "window" over blocks [<paramref name="startBlock"/>, <paramref name="startBlock"/> +
    /// <paramref name="blockCount"/>) of this image, renumbered so the window's own block 0 maps to
    /// this image's <paramref name="startBlock"/>.
    /// </summary>
    public AdfImage CreateWindow(long startBlock, long blockCount)
    {
        if (startBlock < 0 || blockCount <= 0 || startBlock + blockCount > _sectorCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(startBlock), $"Window [{startBlock}, {startBlock + blockCount}) is out of range [0, {_sectorCount}).");
        }

        long absoluteStartBlock = _startBlock + startBlock;
        if (_data is not null)
        {
            return new AdfImage(_data, absoluteStartBlock, blockCount);
        }

        if (_fileBacking is not null)
        {
            return new AdfImage(_fileBacking, absoluteStartBlock, blockCount, ownsFileBacking: false);
        }

        return new AdfImage(_memoryBacking!, absoluteStartBlock, blockCount);
    }

    /// <summary>
    /// A mutable view of one block. For file-backed images, the block enters the write cache before its
    /// span is returned, ensuring subsequent reads observe changes and <see cref="SaveTo"/> persists it.
    /// </summary>
    public Span<byte> GetBlockForWrite(long blockNumber)
    {
        ValidateBlockNumber(blockNumber);
        long absoluteBlock = _startBlock + blockNumber;
        if (_journal is not null && !_journal.ContainsKey(absoluteBlock))
        {
            _journal[absoluteBlock] = ReadAbsoluteBlock(absoluteBlock);
        }

        if (_data is not null)
        {
            return _data.AsSpan((int)absoluteBlock * SectorSize, SectorSize);
        }

        if (_fileBacking is not null)
        {
            lock (_fileBacking.SyncRoot)
            {
                if (!_fileBacking.ModifiedBlocks.TryGetValue(absoluteBlock, out var block))
                {
                    block = new byte[SectorSize];
                    _fileBacking.Stream.Position = absoluteBlock * SectorSize;
                    _fileBacking.Stream.ReadExactly(block);
                    _fileBacking.ModifiedBlocks.Add(absoluteBlock, block);
                }

                return block;
            }
        }

        lock (_memoryBacking!.SyncRoot)
        {
            if (!_memoryBacking.Blocks.TryGetValue(absoluteBlock, out var block))
            {
                block = new byte[SectorSize];
                _memoryBacking.Blocks.Add(absoluteBlock, block);
            }

            return block;
        }
    }

    /// <summary>
    /// Overwrites the 4-byte big-endian field at <paramref name="fieldOffset"/> within
    /// <paramref name="blockNumber"/>. Does not touch disk; call <see cref="SaveTo"/> to persist.
    /// </summary>
    public void PatchChecksumField(long blockNumber, int fieldOffset, uint value) =>
        BinaryPrimitives.WriteUInt32BigEndian(GetBlockForWrite(blockNumber).Slice(fieldOffset, 4), value);

    /// <summary>
    /// Starts recording the pre-write content of every block subsequently touched through
    /// <see cref="GetBlockForWrite"/>, so a multi-block operation (e.g. a file write spanning several
    /// data/extension/bitmap blocks) can be rolled back atomically with <see cref="RollbackTransaction"/>
    /// if it fails partway through, instead of the caller having to manually re-derive and undo each
    /// side effect (block content, allocation-bitmap state, header fields) separately.
    /// </summary>
    public void BeginTransaction() => _journal = new Dictionary<long, byte[]>();

    /// <summary>Stops recording and discards the journal, keeping every change made since <see cref="BeginTransaction"/>.</summary>
    public void CommitTransaction() => _journal = null;

    /// <summary>Restores every block touched since <see cref="BeginTransaction"/> to its content at that time.</summary>
    public void RollbackTransaction()
    {
        if (_journal is null)
        {
            return;
        }

        var snapshot = _journal;
        _journal = null; // stop recording before restoring, so the writes below aren't journaled themselves
        foreach (var (absoluteBlock, original) in snapshot)
        {
            original.CopyTo(GetBlockForWrite(absoluteBlock - _startBlock));
        }
    }

    /// <summary>Writes this image's current bytes to <paramref name="path"/>.</summary>
    public void SaveTo(string path)
    {
        if (_data is not null)
        {
            File.WriteAllBytes(path, _data);
            return;
        }

        if (_fileBacking is not null)
        {
            if (!Path.GetFullPath(path).Equals(_fileBacking.Path, StringComparison.OrdinalIgnoreCase))
            {
                throw new NotSupportedException("A file-backed image can only be saved to its source path.");
            }

            lock (_fileBacking.SyncRoot)
            {
                foreach (var (blockNumber, block) in _fileBacking.ModifiedBlocks)
                {
                    _fileBacking.Stream.Position = blockNumber * SectorSize;
                    _fileBacking.Stream.Write(block);
                }

                _fileBacking.Stream.Flush(flushToDisk: true);
                _fileBacking.ModifiedBlocks.Clear();
            }

            return;
        }

        if (_memoryBacking is not null)
        {
            lock (_memoryBacking.SyncRoot)
            {
                using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
                stream.SetLength(_sectorCount * SectorSize);
                foreach (var (blockNumber, block) in _memoryBacking.Blocks.OrderBy(kv => kv.Key))
                {
                    stream.Position = blockNumber * SectorSize;
                    stream.Write(block);
                }

                stream.Flush(flushToDisk: true);
            }
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

    private void ValidateBlockNumber(long blockNumber)
    {
        if (blockNumber < 0 || blockNumber >= _sectorCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(blockNumber), blockNumber, $"Block must be in [0, {_sectorCount}).");
        }
    }
}
